using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LazyRay.Core;
using Newtonsoft.Json.Linq;

namespace LazyRay.Tools
{
    /// <summary>
    /// validate_script (v2, v6.5) — compile-checks a .cs file (or proposed
    /// content) against its REAL Unity assembly WITHOUT touching Unity APIs
    /// at execution time. All CompilationPipeline data comes from
    /// AssemblyInfoCache (built on the main thread after every compile), so
    /// this tool is THREAD-SAFE and runs on the pipe-thread fast path:
    ///   - the external csc process no longer freezes the editor 2-15s
    ///   - validations never return BUSY and are immune to starvation
    ///   - a validation can run WHILE Unity compiles something else
    ///   - two Claude instances can validate concurrently (unique temp dirs)
    ///
    /// Known limitation (unchanged from v1): source generators / analyzers
    /// are not run.
    /// </summary>
    public class ValidateScriptTool : IMcpTool
    {
        public string Name => "validate_script";

        public string Description =>
            "Compile-check a C# script against its real Unity assembly WITHOUT writing it " +
            "or triggering a domain reload. Pass 'content' to validate proposed code before edit_file. " +
            "Returns compiler errors with line numbers. Runs off the main thread — never freezes the editor.";

        public bool IsDestructive => false;

        // v6.5: thread-safe thanks to AssemblyInfoCache — joins the fast path.
        public bool IsThreadSafe => true;

        // v6.5: 60s (was 25s). The fast path is not bound by the 28s
        // main-thread dispatcher; the bridge request timeout is now 90s.
        private const int CSC_TIMEOUT_MS = 60_000;
        private const int MAX_REPORTED = 30;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""path"": { ""type"": ""string"", ""description"": ""Target .cs path relative to project root (determines which assembly/references/defines are used)"" },
                ""content"": { ""type"": ""string"", ""description"": ""Proposed file content to validate instead of what's on disk"" },
                ""include_warnings"": { ""type"": ""boolean"", ""description"": ""Also report warnings (default false)"" }
            },
            ""required"": [""path""]
        }");

        public string Execute(JObject input)
        {
            string relPath = input.Value<string>("path");
            string content = input.Value<string>("content");
            bool includeWarnings = input.Value<bool?>("include_warnings") ?? false;

            if (string.IsNullOrEmpty(relPath))
                return "ERROR: 'path' is required.";
            if (!relPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return "ERROR: validate_script only supports .cs files.";

            var snap = AssemblyInfoCache.Current;
            if (snap == null || snap.assemblies.Length == 0)
                return "ERROR: Assembly cache not ready yet (Unity may still be importing or compiling). " +
                       "Retry in a few seconds — the cache rebuilds automatically after every compile.";

            string projectRoot = LazyRayPaths.ProjectRoot;
            string fullPath = Path.GetFullPath(Path.Combine(projectRoot, relPath));

            if (content == null && !File.Exists(fullPath))
                return $"ERROR: File not found: {relPath}. Pass 'content' to validate a file that doesn't exist yet.";

            var sw = Stopwatch.StartNew();

            // ── 1. Resolve the assembly this script belongs to (cache) ──
            CachedAssembly asm = AssemblyInfoCache.Resolve(AssemblyInfoCache.Normalize(fullPath));
            if (asm == null)
                return $"ERROR: Could not resolve which assembly '{relPath}' belongs to. " +
                       "Is the path under Assets/ (or an editable package) and covered by an asmdef or Assembly-CSharp?";

            // ── 2. Locate Unity's bundled compiler (cached paths) ────────
            if (!FindCompiler(snap, out string exe, out string argPrefix, out string cscError))
                return $"ERROR: {cscError}";

            // ── 3. Unique temp dir per call: concurrent validations from
            //       multiple Claude instances must never collide ───────────
            string tempDir = Path.Combine(Path.GetTempPath(), "LazyRayValidate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string tempCs = Path.Combine(tempDir, Path.GetFileName(fullPath));
            string tempDll = Path.Combine(tempDir, "lazyray_validate.dll");

            try
            {
                if (content != null)
                    File.WriteAllText(tempCs, content);

                string normalizedTarget = AssemblyInfoCache.Normalize(fullPath);
                bool targetIsInAssembly = false;
                var sources = new List<string>();

                foreach (var abs in asm.sourceFilesAbs)
                {
                    if (AssemblyInfoCache.Normalize(abs) == normalizedTarget)
                    {
                        targetIsInAssembly = true;
                        sources.Add(content != null ? tempCs : abs);
                    }
                    else
                    {
                        sources.Add(abs);
                    }
                }

                // New file not yet part of the compilation: append it.
                if (!targetIsInAssembly)
                    sources.Add(content != null ? tempCs : fullPath);

                // ── 4. References: precompiled + other project assemblies ─
                var refs = new List<string>();
                refs.AddRange(asm.compiledRefs);
                foreach (var dep in asm.depOutputPaths)
                {
                    if (File.Exists(dep))
                        refs.Add(Path.GetFullPath(dep));
                }

                // ── 5. Write the response file ─────────────────────────
                string rsp = Path.Combine(tempDir, "validate.rsp");
                WriteResponseFile(rsp, tempDll, sources, refs, asm);

                // ── 6. Run csc ─────────────────────────────────────────
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"{argPrefix}/noconfig @\"{rsp}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                var stdout = new StringBuilder();
                using var proc = Process.Start(psi);
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit(CSC_TIMEOUT_MS))
                {
                    try { proc.Kill(); } catch { }
                    return $"ERROR: Validation compile exceeded {CSC_TIMEOUT_MS / 1000}s " +
                           $"(assembly '{asm.name}', {sources.Count} sources). The assembly may be too large for inline validation.";
                }
                proc.WaitForExit(); // flush async output

                sw.Stop();

                // ── 7. Parse diagnostics ───────────────────────────────
                string outText;
                lock (stdout) outText = stdout.ToString();
                return FormatResult(outText, proc.ExitCode, tempCs, fullPath, relPath,
                                    asm, sources.Count, refs.Count, sw.Elapsed.TotalSeconds, includeWarnings);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ─── Compiler discovery (from cached paths — no Unity APIs) ─────

        private static bool FindCompiler(AssemblyInfoCache.Snapshot snap,
            out string exe, out string argPrefix, out string error)
        {
            exe = null; argPrefix = ""; error = null;
            string contents = snap.applicationContentsPath;

            // Unity 6 (CoreCLR era): csc.dll run through the bundled dotnet host.
            string cscDll = Path.Combine(contents, "DotNetSdkRoslyn", "csc.dll");
            string dotnet = Path.Combine(contents, "NetCoreRuntime",
                snap.isWindowsEditor ? "dotnet.exe" : "dotnet");

            if (File.Exists(cscDll) && File.Exists(dotnet))
            {
                exe = dotnet;
                argPrefix = $"\"{cscDll}\" ";
                return true;
            }

            // Older layout: standalone csc.exe.
            string cscExe = Path.Combine(contents, "Tools", "Roslyn", "csc.exe");
            if (File.Exists(cscExe))
            {
                exe = cscExe;
                return true;
            }

            error = $"Unity's bundled Roslyn not found. Probed:\n  {cscDll}\n  {cscExe}\n" +
                    "Check the Unity install layout and update ValidateScriptTool.FindCompiler().";
            return false;
        }

        // ─── Response file ─────────────────────────────────────────────

        private static void WriteResponseFile(string rspPath, string outDll,
            List<string> sources, List<string> refs, CachedAssembly asm)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-target:library");
            sb.AppendLine("-nologo");
            sb.AppendLine($"-out:\"{outDll}\"");
            sb.AppendLine($"-langversion:{asm.langVersion}");

            if (asm.allowUnsafe)
                sb.AppendLine("-unsafe");

            if (asm.defines != null && asm.defines.Length > 0)
                sb.AppendLine($"-define:{string.Join(";", asm.defines)}");

            foreach (var r in refs.Distinct(StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"-r:\"{r}\"");

            foreach (var s in sources)
                sb.AppendLine($"\"{s}\"");

            File.WriteAllText(rspPath, sb.ToString());
        }

        // ─── Diagnostics parsing ───────────────────────────────────────

        private static readonly Regex DiagnosticRx = new Regex(
            @"^(?<file>.+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<sev>error|warning)\s+(?<code>CS\d+):\s+(?<msg>.*)$",
            RegexOptions.Compiled);

        private static string FormatResult(string output, int exitCode,
            string tempCs, string fullPath, string relPath, CachedAssembly asm,
            int sourceCount, int refCount, double seconds, bool includeWarnings)
        {
            string tempNorm = AssemblyInfoCache.Normalize(tempCs);
            string targetNorm = AssemblyInfoCache.Normalize(fullPath);

            var errors = new List<string>();
            var warnings = new List<string>();
            int targetErrors = 0;

            foreach (var raw in output.Split('\n'))
            {
                var m = DiagnosticRx.Match(raw.TrimEnd('\r'));
                if (!m.Success) continue;

                string file = m.Groups["file"].Value;
                string fileNorm = AssemblyInfoCache.Normalize(Path.IsPathRooted(file) ? file : Path.GetFullPath(file));
                bool isTarget = fileNorm == tempNorm || fileNorm == targetNorm;

                // Show the real project path instead of the temp copy.
                string displayFile = isTarget ? relPath : file;
                string line = $"{displayFile}({m.Groups["line"].Value},{m.Groups["col"].Value}): " +
                              $"{m.Groups["code"].Value}: {m.Groups["msg"].Value}";

                if (m.Groups["sev"].Value == "error")
                {
                    if (isTarget) targetErrors++;
                    errors.Add(line);
                }
                else if (includeWarnings && isTarget)
                {
                    warnings.Add(line);
                }
            }

            if (exitCode == 0 && errors.Count == 0)
            {
                string warnBlock = warnings.Count > 0
                    ? $"\n⚠️ {warnings.Count} warning(s) in target file:\n" + string.Join("\n", warnings.Take(MAX_REPORTED))
                    : "";
                return $"✅ VALID — compiles clean against '{asm.name}' " +
                       $"({sourceCount} sources, {refCount} refs, {seconds:F1}s). Safe to edit_file.{warnBlock}";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"❌ INVALID — {errors.Count} error(s) compiling '{asm.name}' ({seconds:F1}s).");
            if (targetErrors == 0 && errors.Count > 0)
                sb.AppendLine("Note: no errors are IN the target file itself — they may be caused by it " +
                              "(e.g. removed/renamed members used elsewhere in the assembly), or pre-existing.");
            foreach (var e in errors.Take(MAX_REPORTED))
                sb.AppendLine(e);
            if (errors.Count > MAX_REPORTED)
                sb.AppendLine($"... and {errors.Count - MAX_REPORTED} more.");
            sb.Append("Do NOT edit_file until this validates clean.");
            return sb.ToString();
        }
    }
}
