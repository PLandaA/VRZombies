using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace LazyRay.Core
{
    /// <summary>
    /// v6.5: Snapshot of everything validate_script needs from Unity's
    /// CompilationPipeline, captured on the MAIN THREAD and read lock-free
    /// from the pipe thread. This is what lets validate_script join the
    /// fast path: with the cache warm, a validation never touches a
    /// main-thread API — the external csc process no longer freezes the
    /// editor for 2-15s per validation, never returns BUSY, and can run
    /// WHILE Unity compiles something else.
    ///
    /// Rebuild triggers (all main thread):
    ///   - Server start (covers domain reloads, which wipe statics anyway)
    ///   - CompilationPipeline.compilationFinished (covers compiles that
    ///     don't reload the domain)
    /// Readers grab the volatile snapshot reference once and work on an
    /// immutable object — no locks, no torn reads.
    /// </summary>
    public class CachedAssembly
    {
        public string name;
        public string[] sourceFilesAbs;
        public HashSet<string> sourceSet;      // normalized absolute paths
        public string[] compiledRefs;
        public string[] depOutputPaths;
        public string[] defines;
        public bool allowUnsafe;
        public string langVersion;
        public string asmdefDirNorm;           // normalized asmdef dir, null for Assembly-CSharp*
    }

    public static class AssemblyInfoCache
    {
        public class Snapshot
        {
            public CachedAssembly[] assemblies;
            public Dictionary<string, CachedAssembly> bySource;
            public string applicationContentsPath;
            public bool isWindowsEditor;
            public DateTime builtAt;
        }

        private static volatile Snapshot _current;
        private static bool _subscribed;

        public static Snapshot Current => _current;
        public static bool IsReady => _current != null && _current.assemblies.Length > 0;

        public static string Normalize(string p) => p.Replace('\\', '/').ToLowerInvariant();

        /// <summary>Main thread only.</summary>
        public static void Rebuild()
        {
            try
            {
                string projectRoot = LazyRayPaths.ProjectRoot;
                var asms = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                var list = new List<CachedAssembly>(asms.Length);
                var bySource = new Dictionary<string, CachedAssembly>(StringComparer.Ordinal);

                foreach (var a in asms)
                {
                    var ca = new CachedAssembly
                    {
                        name = a.name,
                        compiledRefs = a.compiledAssemblyReferences?.ToArray() ?? Array.Empty<string>(),
                        depOutputPaths = a.assemblyReferences?
                            .Select(d => d.outputPath)
                            .Where(p => !string.IsNullOrEmpty(p))
                            .ToArray() ?? Array.Empty<string>(),
                        defines = a.defines?.ToArray() ?? Array.Empty<string>(),
                        allowUnsafe = a.compilerOptions != null && a.compilerOptions.AllowUnsafeCode,
                        langVersion = ProbeLangVersion(a),
                        sourceSet = new HashSet<string>(StringComparer.Ordinal),
                    };

                    var abs = new string[a.sourceFiles.Length];
                    for (int i = 0; i < a.sourceFiles.Length; i++)
                    {
                        abs[i] = Path.GetFullPath(Path.Combine(projectRoot, a.sourceFiles[i]));
                        string norm = Normalize(abs[i]);
                        ca.sourceSet.Add(norm);
                        bySource[norm] = ca;
                    }
                    ca.sourceFilesAbs = abs;

                    try
                    {
                        string asmdef = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(a.name);
                        if (!string.IsNullOrEmpty(asmdef))
                        {
                            string dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(projectRoot, asmdef)));
                            ca.asmdefDirNorm = Normalize(dir);
                        }
                    }
                    catch { }

                    list.Add(ca);
                }

                _current = new Snapshot
                {
                    assemblies = list.ToArray(),
                    bySource = bySource,
                    applicationContentsPath = EditorApplication.applicationContentsPath,
                    isWindowsEditor = Application.platform == RuntimePlatform.WindowsEditor,
                    builtAt = DateTime.Now,
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LazyRay] AssemblyInfoCache rebuild failed (validate_script degraded until next compile): {ex.Message}");
            }
        }

        /// <summary>Main thread only. Idempotent.</summary>
        public static void SubscribeRebuilds()
        {
            if (_subscribed) return;
            _subscribed = true;
            CompilationPipeline.compilationFinished += _ =>
            {
                try { Rebuild(); } catch { }
            };
        }

        /// <summary>
        /// Thread-safe path → assembly resolution replacing
        /// CompilationPipeline.GetAssemblyNameFromScriptPath:
        ///   1. exact source-file match (covers every existing file);
        ///   2. deepest asmdef-directory prefix (Unity's nearest-ancestor rule,
        ///      covers NEW files under asmdef trees);
        ///   3. Assembly-CSharp family rules (Editor / Plugins folders).
        /// </summary>
        public static CachedAssembly Resolve(string fullPathNorm)
        {
            var snap = _current;
            if (snap == null) return null;

            if (snap.bySource.TryGetValue(fullPathNorm, out var exact))
                return exact;

            CachedAssembly best = null;
            int bestLen = -1;
            foreach (var ca in snap.assemblies)
            {
                if (ca.asmdefDirNorm == null) continue;
                if (fullPathNorm.StartsWith(ca.asmdefDirNorm + "/", StringComparison.Ordinal) &&
                    ca.asmdefDirNorm.Length > bestLen)
                {
                    best = ca;
                    bestLen = ca.asmdefDirNorm.Length;
                }
            }
            if (best != null) return best;

            bool isEditor = fullPathNorm.Contains("/editor/");
            bool isFirstpass = fullPathNorm.Contains("/plugins/") || fullPathNorm.Contains("/standard assets/");
            string wanted = isFirstpass
                ? (isEditor ? "assembly-csharp-editor-firstpass" : "assembly-csharp-firstpass")
                : (isEditor ? "assembly-csharp-editor" : "assembly-csharp");

            return snap.assemblies.FirstOrDefault(a => a.name.ToLowerInvariant() == wanted)
                ?? snap.assemblies.FirstOrDefault(a => a.name.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase));
        }

        private static string ProbeLangVersion(Assembly a)
        {
            try
            {
                var prop = a.compilerOptions?.GetType().GetProperty("LanguageVersion");
                if (prop?.GetValue(a.compilerOptions) is string v && !string.IsNullOrEmpty(v))
                    return v;
            }
            catch { }
            return "9.0";
        }
    }
}
