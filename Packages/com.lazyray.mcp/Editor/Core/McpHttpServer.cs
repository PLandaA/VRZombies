using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Core
{
    /// <summary>
    /// IPC server for the MCP bridge using Windows Named Pipes.
    /// 
    /// Why Named Pipes instead of TCP?
    ///   TCP sockets on Windows enter TIME_WAIT (60-120s) after close.
    ///   During Unity domain reload, the old socket cannot be cleanly closed
    ///   (thread abort), leaving the port inaccessible.
    ///   Named Pipes have NO TIME_WAIT — pipe name is freed instantly.
    ///   
    /// Protocol (JSON lines):
    ///   Client sends: {"endpoint":"/tool","method":"POST","body":{...}}\n
    ///   Server sends: {"status":200,"result":{...}}\n
    ///   Connection closes.
    /// </summary>
    [InitializeOnLoad]
    public static class McpHttpServer
    {
        // v6.3: Per-instance pipe name. Every Unity editor (identified by its
        // PID) serves its own pipe, so multiple open projects never collide on
        // the same pipe name — the root cause of the alternating OK/BUSY
        // behavior seen when AssetImportWorkers or a second editor competed
        // for the fixed "unity-mcp" name. The bridge discovers live pipes via
        // InstanceRegistry instead of hardcoding this name.
        private static readonly string PIPE_NAME =
            "unity-mcp-" + System.Diagnostics.Process.GetCurrentProcess().Id;

        private static Thread _listenerThread;
        private static volatile bool _isRunning;
        private static ToolExecutor _toolExecutor;
        private static int _port = 7823;

        private static NamedPipeServerStream _currentPipe;

        // v6.1.3: proper shutdown signaling for the listener thread.
        // Replaces the racy "dummy client connect" trick, which could lose
        // the race against the next domain's immediate restart and leave a
        // ZOMBIE listener alive — two listeners then alternate accepting
        // connections, one of them with a queue nobody pumps (symptom:
        // requests alternate OK / BUSY).
        private static ManualResetEventSlim _shutdownSignal;

        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new();
        private static readonly ConcurrentDictionary<string, string> _pendingResults = new();
        private static readonly ConcurrentDictionary<string, ManualResetEventSlim> _pendingSignals = new();

        public static bool IsRunning => _isRunning && _listenerThread is { IsAlive: true };
        public static int Port => _port;
        public static string PipeName => PIPE_NAME;

        private static double _lastHealthCheck;
        private const double HEALTH_CHECK_INTERVAL = 2.0;

        // Captured on the main thread at InitializeOnLoad so background
        // threads can post wake-up requests to the editor loop.
        private static SynchronizationContext _mainThreadContext;

        static McpHttpServer()
        {
            // v6.3: AssetImportWorkers load editor assemblies too, so this
            // static ctor runs inside them. They must NEVER serve a pipe or
            // appear in the instance registry — with the old fixed pipe name
            // they competed with the real editor for connections (alternating
            // OK/BUSY that survived editor restarts).
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            _mainThreadContext = SynchronizationContext.Current;

            EditorApplication.update += ProcessMainThreadQueue;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;

            // v6.1.1 FIX: Start IMMEDIATELY instead of via delayCall.
            //
            // delayCall only fires on an editor tick. When Unity is unfocused
            // (user is in Claude Desktop while the compile happens), the editor
            // throttles its loop after the domain reload and the delayCall can
            // sit for MINUTES — the pipe never comes back until the user clicks
            // on Unity. InitializeOnLoad runs as part of the reload itself
            // (focus-independent), so starting here fixes the post-compile drop.
            TryStartImmediate();
        }

        private static void TryStartImmediate()
        {
            LazyRaySettings settings = null;
            try { settings = Resources.Load<LazyRaySettings>("LazyRaySettings"); }
            catch { /* asset db not ready — fall through to deferred path */ }

            if (settings != null)
            {
                _port = settings.serverPort;
                if (settings.autoStartServer)
                    StartServer();
                return;
            }

            // First install (asset doesn't exist yet) or very early init:
            // fall back to the old deferred path, which may create the asset.
            EditorApplication.delayCall += () =>
            {
                var s = LazyRaySettings.Instance;
                _port = s.serverPort;
                if (s.autoStartServer)
                    StartServer();
            };
        }

        private static void OnBeforeAssemblyReload()
        {
            // Note: do NOT unregister here — the server restarts right after
            // the domain reload and re-registers. The heartbeat gap during
            // the reload stays below the bridge's stale threshold.
            ForceStop();
        }

        private static void OnEditorQuitting()
        {
            InstanceRegistry.Unregister();
            ForceStop();
        }

        private static void ForceStop()
        {
            _isRunning = false;

            foreach (var signal in _pendingSignals.Values)
            {
                try { signal.Set(); } catch { }
            }

            // Wake the listener out of its overlapped accept (see ListenLoop).
            try { _shutdownSignal?.Set(); } catch { }

            if (_listenerThread != null && _listenerThread.IsAlive)
            {
                try { _listenerThread.Join(2000); } catch { }
                if (_listenerThread.IsAlive)
                    Debug.LogWarning("[LazyRay] Listener thread did not exit in 2s. " +
                                     "If tools start alternating OK/BUSY, restart Unity.");
                _listenerThread = null;
            }

            try { _currentPipe?.Dispose(); } catch { }
            _currentPipe = null;
            _shutdownSignal = null;
        }

        [MenuItem("Tools/LazyRay/Start Server")]
        public static void StartServer()
        {
            ForceStop();

            // v6.4: cache project paths on the main thread so thread-safe
            // tools can run on the pipe thread without Unity APIs.
            LazyRayPaths.Initialize();

            // v6.5: assembly metadata snapshot for the thread-safe
            // validate_script. Rebuilt here (covers domain reloads) and
            // after every compile (SubscribeRebuilds).
            AssemblyInfoCache.Rebuild();
            AssemblyInfoCache.SubscribeRebuilds();

            _toolExecutor = new ToolExecutor(LazyRaySettings.Instance);

            try
            {
                _isRunning = true;
                _shutdownSignal = new ManualResetEventSlim(false);
                var shutdown = _shutdownSignal; // capture THIS run's signal
                _listenerThread = new Thread(() => ListenLoop(shutdown))
                {
                    IsBackground = true,
                    Name = "LazyRayMCP"
                };
                _listenerThread.Start();

                // v6.3: announce this instance so the bridge can discover it.
                // Runs on the main thread here (StartServer is main-thread only).
                InstanceRegistry.Register(PIPE_NAME);

                Debug.Log($"[LazyRay] MCP Server started (pipe: {PIPE_NAME})");

                ThreadPool.QueueUserWorkItem(_ => SelfTest());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LazyRay] Failed to start MCP server: {ex.Message}");
                _isRunning = false;
            }
        }

        private static void SelfTest()
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    Thread.Sleep(300 * attempt);
                    if (!_isRunning) return;

                    using var client = new NamedPipeClientStream(".", PIPE_NAME, PipeDirection.InOut);
                    client.Connect(3000);

                    string request = JsonConvert.SerializeObject(new { endpoint = "/status", method = "GET" }) + "\n";
                    byte[] reqBytes = Encoding.UTF8.GetBytes(request);
                    client.Write(reqBytes, 0, reqBytes.Length);
                    client.Flush();

                    string response = ReadLine(client, 5000);

                    if (response != null && response.Contains("\"status\":\"ok\""))
                    {
                        Debug.Log($"[LazyRay] Self-test passed (attempt {attempt})");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == 3)
                        Debug.LogError($"[LazyRay] Self-test FAILED: {ex.Message}");
                }
            }
        }

        [MenuItem("Tools/LazyRay/Stop Server")]
        public static void StopServer()
        {
            ForceStop();
            Debug.Log("[LazyRay] MCP Server stopped.");
        }

        [MenuItem("Tools/LazyRay/Restart Server")]
        public static void RestartServer()
        {
            StartServer();
        }

        [MenuItem("Tools/LazyRay/Stop Server", true)]
        private static bool ValidateStop() => _isRunning;

        [MenuItem("Tools/LazyRay/Start Server", true)]
        private static bool ValidateStart() => !_isRunning;

        // ─── Named Pipe Listen Loop ────────────────────────────────

        private static void ListenLoop(ManualResetEventSlim shutdown)
        {
            while (_isRunning && !shutdown.IsSet)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        PIPE_NAME,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.Asynchronous // REQUIRED for overlapped accept
                    );

                    _currentPipe = pipe;

                    // Overlapped accept: wait for EITHER a client connection OR
                    // the shutdown signal. Deterministic — no dummy client, no race.
                    var ar = pipe.BeginWaitForConnection(null, null);
                    int idx = WaitHandle.WaitAny(new WaitHandle[] { ar.AsyncWaitHandle, shutdown.WaitHandle });

                    if (idx == 1 || !_isRunning)
                    {
                        try { pipe.Dispose(); } catch { }
                        break;
                    }

                    pipe.EndWaitForConnection(ar);

                    var p = pipe;
                    _currentPipe = null;
                    ThreadPool.QueueUserWorkItem(_ => HandleClient(p));
                }
                catch (ObjectDisposedException) when (!_isRunning) { break; }
                catch (IOException) when (!_isRunning) { break; }
                catch (Exception ex)
                {
                    if (_isRunning && !shutdown.IsSet)
                    {
                        Debug.LogError($"[LazyRay] Pipe error: {ex.GetType().Name}: {ex.Message}");
                        try { pipe?.Dispose(); } catch { }
                        Thread.Sleep(100);
                    }
                    else
                    {
                        try { pipe?.Dispose(); } catch { }
                        break;
                    }
                }
            }
        }

        // ─── Client Handler ────────────────────────────────────────

        private static void HandleClient(NamedPipeServerStream pipe)
        {
            try
            {
                using (pipe)
                {
                    string requestLine = ReadLine(pipe, 15000);
                    if (string.IsNullOrEmpty(requestLine))
                    {
                        WriteLine(pipe, JsonConvert.SerializeObject(new { status = 400, error = "Empty request" }));
                        return;
                    }

                    var req = JObject.Parse(requestLine);
                    string endpoint = req.Value<string>("endpoint") ?? "/";
                    string method = req.Value<string>("method") ?? "GET";
                    var body = req["body"];

                    string result;
                    try
                    {
                        result = endpoint switch
                        {
                            "/ping" => HandlePing(),
                            "/status" => HandleStatus(),
                            "/tool" => HandleToolCall(method, body),
                            "/tools" => HandleListTools(),
                            _ => JsonConvert.SerializeObject(new { error = $"Unknown: {endpoint}" })
                        };
                    }
                    catch (Exception ex)
                    {
                        result = JsonConvert.SerializeObject(new { error = ex.Message });
                    }

                    WriteLine(pipe, JsonConvert.SerializeObject(new { status = 200, result }));
                }
            }
            catch (IOException)
            {
                // Client disconnected before we finished writing (e.g. the
                // bridge timed out and will retry). Expected — not an error.
            }
            catch (Exception ex)
            {
                if (_isRunning)
                    Debug.LogError($"[LazyRay] Client handler error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ─── Pipe I/O ──────────────────────────────────────────────

        private static string ReadLine(PipeStream pipe, int timeoutMs)
        {
            var sb = new StringBuilder();
            var buf = new byte[4096];
            var startTime = Environment.TickCount;

            while (true)
            {
                if (Environment.TickCount - startTime > timeoutMs)
                    throw new TimeoutException("ReadLine timed out");

                if (!pipe.IsConnected) return sb.ToString();

                int bytesRead = pipe.Read(buf, 0, buf.Length);
                if (bytesRead == 0) { Thread.Sleep(10); continue; }

                string chunk = Encoding.UTF8.GetString(buf, 0, bytesRead);
                int nlIdx = chunk.IndexOf('\n');
                if (nlIdx >= 0)
                {
                    sb.Append(chunk, 0, nlIdx);
                    return sb.ToString();
                }

                sb.Append(chunk);
                if (sb.Length > 1_000_000) throw new InvalidOperationException("Request too large");
            }
        }

        private static void WriteLine(PipeStream pipe, string line)
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");
            pipe.Write(data, 0, data.Length);
            pipe.Flush();
        }

        // ─── Endpoint Handlers ─────────────────────────────────────

        private static string HandlePing()
        {
            return ExecuteOnMainThreadAndWait("__ping", null);
        }

        private static string HandleStatus()
        {
            return JsonConvert.SerializeObject(new
            {
                status = "ok",
                server_running = _isRunning,
                compilation = CompilationTracker.GetStatus(),
                timestamp = DateTime.Now.ToString("o")
            });
        }

        private static string HandleListTools()
        {
            return ExecuteOnMainThreadAndWait("__list_tools", null);
        }

        private static string HandleToolCall(string method, JToken body)
        {
            if (method != "POST")
                return JsonConvert.SerializeObject(new { error = "POST required" });
            if (body == null)
                return JsonConvert.SerializeObject(new { error = "Missing body" });

            var payload = body as JObject ?? JObject.Parse(body.ToString());
            string toolName = payload.Value<string>("tool");
            var input = payload["input"] as JObject ?? new JObject();

            if (string.IsNullOrEmpty(toolName))
                return JsonConvert.SerializeObject(new { error = "Missing 'tool' field" });

            // v6.6: tag the request with the calling Claude instance (bridge
            // PID) so per-client state (deferred batches) stays separated
            // when two instances work on the SAME project.
            if (input != null)
                input["__client"] = body?.Value<string>("client") ?? "legacy";

            // v6.4 FAST PATH: thread-safe tools execute right here on the
            // pipe thread — zero main-thread queue wait, never BUSY, and
            // they keep working while Unity compiles, imports or is starved.
            var executor = _toolExecutor;
            if (executor != null && executor.IsThreadSafeTool(toolName))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string fastResult = executor.ExecuteTool(toolName, input);
                sw.Stop();
                TrackActivity(toolName);
                return JsonConvert.SerializeObject(new
                {
                    tool = toolName,
                    result = fastResult,
                    route = "fast",
                    unity_ms = sw.ElapsedMilliseconds
                });
            }

            string result = ExecuteOnMainThreadAndWait(toolName, input);
            TrackActivity(toolName);
            return JsonConvert.SerializeObject(new { tool = toolName, result });
        }

        // ─── Main Thread Execution ─────────────────────────────────

        // Requests abandoned by a dispatcher timeout. The queued action checks
        // this before running, so a timed-out edit can NEVER execute later as
        // a "ghost" action — which also makes it SAFE for the bridge to retry.
        private static readonly ConcurrentDictionary<string, bool> _cancelledRequests = new();

        // Request currently executing on the main thread (null if none).
        // Lets the timeout path distinguish "stuck INSIDE the action" (not
        // retry-safe) from "never started" (cancelled, retry-safe).
        private static volatile string _currentlyExecuting;

        private static string ExecuteOnMainThreadAndWait(string toolName, JObject input)
        {
            string requestId = Guid.NewGuid().ToString();
            var signal = new ManualResetEventSlim(false);
            _pendingSignals[requestId] = signal;

            _mainThreadQueue.Enqueue(() =>
            {
                // Abandoned by timeout — do NOT execute (prevents ghost edits
                // firing minutes later when the main thread unfreezes).
                if (_cancelledRequests.TryRemove(requestId, out _))
                    return;

                _currentlyExecuting = requestId;
                try
                {
                    string toolResult;

                    if (toolName == "__ping")
                    {
                        // Collect loaded scenes info
                        var scenes = new List<object>();
                        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                        {
                            var s = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                            if (s.isLoaded) scenes.Add(new { name = s.name, path = s.path, is_active = s == UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene() });
                        }

                        toolResult = JsonConvert.SerializeObject(new
                        {
                            status = "ok",
                            project = Application.productName,
                            unity_version = Application.unityVersion,
                            scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().name,
                            is_compiling = CompilationTracker.IsCompiling,
                            is_playing = EditorApplication.isPlaying,
                            is_paused = EditorApplication.isPaused,
                            play_mode = EditorApplication.isPlaying ? (EditorApplication.isPaused ? "paused" : "playing") : "edit",
                            loaded_scenes = scenes,
                            timestamp = DateTime.Now.ToString("o")
                        });
                    }
                    else if (toolName == "__list_tools")
                    {
                        var tools = _toolExecutor.GetToolDefinitions();
                        toolResult = JsonConvert.SerializeObject(new { tools });
                    }
                    else
                    {
                        toolResult = _toolExecutor.ExecuteTool(toolName, input);
                    }

                    _pendingResults[requestId] = toolResult;
                }
                catch (Exception ex)
                {
                    _pendingResults[requestId] = $"ERROR: {ex.Message}";
                }
                finally
                {
                    _currentlyExecuting = null;
                    if (_pendingSignals.TryGetValue(requestId, out var s))
                        s.Set();
                }
            });

            // Nudge the editor loop from the pipe thread (best effort).
            // If Unity is unfocused/throttled, this asks for a tick as soon
            // as the loop pumps once, so queued requests drain quickly
            // instead of waiting on the throttled tick rate.
            try
            {
                _mainThreadContext?.Post(_ =>
                {
                    try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
                }, null);
            }
            catch { }

            // 28s (not 30s) so Unity answers BEFORE the bridge's own 30s
            // timeout instead of racing it.
            bool completed = signal.Wait(TimeSpan.FromSeconds(28));
            _pendingSignals.TryRemove(requestId, out _);
            signal.Dispose();

            if (!completed)
            {
                // Two distinct cases — the difference matters for retry safety:
                if (_currentlyExecuting == requestId)
                {
                    // The action STARTED and the main thread is stuck INSIDE it
                    // (typically AssetDatabase.Refresh or an import). It may
                    // still complete later. The bridge must NOT auto-retry this.
                    return "ERROR: STILL_RUNNING — this operation started and Unity's main " +
                           "thread is stuck inside it (likely AssetDatabase.Refresh/import). " +
                           "It may complete later. Do NOT retry blindly — verify state first " +
                           "(e.g. read_file) once Unity responds again.";
                }

                // Never started: cancel it so it can never run later, then
                // return a BUSY error the bridge knows is SAFE TO RETRY.
                _cancelledRequests[requestId] = true;
                return "ERROR: BUSY — Unity main thread did not respond in 28s " +
                       "(import/progress dialog/heavy operation?). The request was " +
                       "cancelled and did NOT execute; it is safe to retry.";
            }

            _pendingResults.TryRemove(requestId, out string result);
            return result ?? "ERROR: No result returned";
        }

        private static void ProcessMainThreadQueue()
        {
            // Health check: detect zombie state (flag says running but thread is dead)
            if (_isRunning && (_listenerThread == null || !_listenerThread.IsAlive))
            {
                if (EditorApplication.timeSinceStartup - _lastHealthCheck > HEALTH_CHECK_INTERVAL)
                {
                    _lastHealthCheck = EditorApplication.timeSinceStartup;
                    Debug.LogWarning("[LazyRay] Listener thread died — auto-restarting server...");
                    StartServer();
                    return;
                }
            }

            int processed = 0;
            while (processed < 5 && _mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Debug.LogError($"[LazyRay] Main thread error: {ex}"); }
                processed++;
            }

            // If work remains, request another tick right away — otherwise a
            // throttled (unfocused) editor drains the queue at ~1 item/sec.
            if (!_mainThreadQueue.IsEmpty)
            {
                try { EditorApplication.QueuePlayerLoopUpdate(); } catch { }
            }
        }

        // ─── Activity Tracking ─────────────────────────────────────

        private static int _requestCount;
        private static string _lastToolName;
        private static DateTime _lastActivityTime;

        public static int RequestCount => _requestCount;
        public static string LastToolName => _lastToolName;
        public static DateTime LastActivityTime => _lastActivityTime;

        private static void TrackActivity(string toolName)
        {
            Interlocked.Increment(ref _requestCount);
            _lastToolName = toolName;
            _lastActivityTime = DateTime.Now;
        }
    }
}
