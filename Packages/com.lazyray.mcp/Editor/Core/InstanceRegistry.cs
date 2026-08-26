using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LazyRay.Core
{
    /// <summary>
    /// v6.3: Multi-project instance registry.
    ///
    /// Each Unity instance running LazyRay announces itself by writing
    ///   %LOCALAPPDATA%\LazyRay\instances\&lt;pid&gt;.json
    /// containing its project name/path, per-PID pipe name and a heartbeat.
    /// The Node bridge discovers live instances by reading this directory
    /// and routes each tool call to the right pipe.
    ///
    /// Design notes:
    /// - One file per instance: no write contention between editors.
    /// - Heartbeat runs on a System.Threading.Timer (background thread),
    ///   NOT on EditorApplication.update — that callback gets starved in
    ///   large projects (Meta XR polling, AI Toolkit, ADB lookups) and a
    ///   starved heartbeat would make a live editor look dead.
    /// - All Unity API values (dataPath, productName, unityVersion) are
    ///   captured ONCE on the main thread in Register(); the timer thread
    ///   only does file I/O.
    /// - Crash safety: the bridge treats entries as dead when the heartbeat
    ///   is stale (>20s) or the PID no longer exists, so leftover files
    ///   from a crashed editor are ignored and cleaned up by the bridge.
    /// </summary>
    public static class InstanceRegistry
    {
        private const int HEARTBEAT_MS = 5000;

        private static Timer _heartbeatTimer;
        private static string _filePath;
        private static JObject _payload;
        private static readonly object _lock = new object();

        public static string RegistryDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LazyRay", "instances");

        /// <summary>Must be called from the main thread (captures Unity API state).</summary>
        public static void Register(string pipeName)
        {
            try
            {
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                Directory.CreateDirectory(RegistryDir);

                string projectPath;
                try { projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath; }
                catch { projectPath = ""; }

                lock (_lock)
                {
                    _filePath = Path.Combine(RegistryDir, pid + ".json");
                    _payload = new JObject
                    {
                        ["pid"] = pid,
                        ["pipe"] = pipeName,
                        ["project_name"] = Application.productName,
                        ["project_path"] = projectPath,
                        ["unity_version"] = Application.unityVersion,
                    };

                    // v6.6: publish the instance tint so the bridge (and any
                    // other tooling) can show matching colors per project.
                    try
                    {
                        string hex = LazyRaySettings.Instance.InstanceColorHex;
                        if (hex != null) _payload["color"] = hex;
                    }
                    catch { }
                }

                WriteHeartbeat();

                _heartbeatTimer?.Dispose();
                _heartbeatTimer = new Timer(_ => WriteHeartbeat(), null, HEARTBEAT_MS, HEARTBEAT_MS);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LazyRay] Instance registry failed (multi-project routing disabled): {ex.Message}");
            }
        }

        /// <summary>Safe to call from any thread. Only file I/O — no Unity APIs.</summary>
        private static void WriteHeartbeat()
        {
            try
            {
                lock (_lock)
                {
                    if (_filePath == null || _payload == null) return;
                    _payload["heartbeat"] = DateTime.UtcNow.ToString("o");

                    // Write-to-temp + copy so the bridge never reads a half-written file.
                    string tmp = _filePath + ".tmp";
                    File.WriteAllText(tmp, _payload.ToString(Formatting.None));
                    File.Copy(tmp, _filePath, true);
                    try { File.Delete(tmp); } catch { }
                }
            }
            catch { /* best effort — a missed heartbeat is recovered on the next tick */ }
        }

        /// <summary>Called on editor quit. NOT called on domain reload — the
        /// server restarts after the reload and re-registers, and the small
        /// heartbeat gap during the reload stays under the stale threshold.</summary>
        public static void Unregister()
        {
            try
            {
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;
                lock (_lock)
                {
                    if (_filePath != null && File.Exists(_filePath))
                        File.Delete(_filePath);
                    _filePath = null;
                    _payload = null;
                }
            }
            catch { }
        }
    }
}
