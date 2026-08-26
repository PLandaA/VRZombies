using System;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace LazyRay.Core
{
    /// <summary>
    /// Tracks Unity's compilation state so the MCP server can report it.
    /// 
    /// THREAD SAFETY (v2):
    ///   GetStatus() is called from the pipe's background thread.
    ///   SessionState can only be called from the main thread.
    ///   Solution: volatile shadow fields updated on main thread,
    ///   readable from any thread. SessionState used only for
    ///   persistence across domain reloads (read in static ctor = main thread).
    /// </summary>
    [InitializeOnLoad]
    public static class CompilationTracker
    {
        // SessionState keys (persist across domain reloads)
        private const string KEY_COMPILING = "ClaudeEditor_IsCompiling";
        private const string KEY_START_TIME = "ClaudeEditor_CompileStartTime";
        private const string KEY_LAST_DURATION = "ClaudeEditor_LastCompileDuration";
        private const string KEY_LAST_RESULT = "ClaudeEditor_LastCompileResult";
        private const string KEY_COMPILE_COUNT = "ClaudeEditor_CompileCount";
        private const string KEY_DOMAIN_RELOADING = "ClaudeEditor_DomainReloading";

        // Volatile shadow fields - thread-safe reads from any thread
        private static volatile bool _isCompiling;
        private static volatile bool _isDomainReloading;
        private static float _compileStartTime;
        private static float _lastCompileDuration;
        private static int _compileCount;
        private static volatile string _lastCompileResult = "none";

        public static bool IsCompiling => _isCompiling;
        public static bool IsDomainReloading => _isDomainReloading;
        public static float LastCompileDurationSeconds => _lastCompileDuration;
        public static string LastCompileResult => _lastCompileResult;
        public static int CompileCount => _compileCount;

        public static double ElapsedSeconds =>
            _isCompiling ? Math.Max(0, EditorApplication.timeSinceStartup - _compileStartTime) : 0;

        static CompilationTracker()
        {
            _isCompiling = SessionState.GetBool(KEY_COMPILING, false);
            _isDomainReloading = SessionState.GetBool(KEY_DOMAIN_RELOADING, false);
            _compileStartTime = SessionState.GetFloat(KEY_START_TIME, 0f);
            _lastCompileDuration = SessionState.GetFloat(KEY_LAST_DURATION, 0f);
            _lastCompileResult = SessionState.GetString(KEY_LAST_RESULT, "none");
            _compileCount = SessionState.GetInt(KEY_COMPILE_COUNT, 0);

            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;

            if (EditorApplication.isCompiling && !_isCompiling)
            {
                SetCompiling(true);
                SetCompileStartTime((float)EditorApplication.timeSinceStartup);
            }
        }

        private static void SetCompiling(bool value) { _isCompiling = value; SessionState.SetBool(KEY_COMPILING, value); }
        private static void SetCompileStartTime(float value) { _compileStartTime = value; SessionState.SetFloat(KEY_START_TIME, value); }
        private static void SetLastDuration(float value) { _lastCompileDuration = value; SessionState.SetFloat(KEY_LAST_DURATION, value); }
        private static void SetLastResult(string value) { _lastCompileResult = value; SessionState.SetString(KEY_LAST_RESULT, value); }
        private static void SetCompileCount(int value) { _compileCount = value; SessionState.SetInt(KEY_COMPILE_COUNT, value); }
        private static void SetDomainReloading(bool value) { _isDomainReloading = value; SessionState.SetBool(KEY_DOMAIN_RELOADING, value); }

        private static void OnCompilationStarted(object context)
        {
            SetCompiling(true);
            SetCompileStartTime((float)EditorApplication.timeSinceStartup);
            SetCompileCount(_compileCount + 1);
            Debug.Log("[LazyRay] Compilation started...");
        }

        private static void OnCompilationFinished(object context)
        {
            float duration = (float)(EditorApplication.timeSinceStartup - _compileStartTime);
            SetLastDuration(duration);
            SetLastResult("success");
            SetCompiling(false);
            Debug.Log($"[LazyRay] Compilation finished ({duration:F1}s)");
        }

        private static void OnBeforeAssemblyReload()
        {
            SetDomainReloading(true);
        }

        private static void OnAfterAssemblyReload()
        {
            SetDomainReloading(false);
            if (EditorUtility.scriptCompilationFailed)
            {
                SetLastResult("errors");
                Debug.LogWarning("[LazyRay] Compilation completed with errors");
            }
            else
            {
                SetLastResult("success");
            }
            SetCompiling(false);
        }

        /// <summary>
        /// Returns a JSON-friendly status object.
        /// THREAD SAFE - reads only volatile fields, no SessionState.
        /// </summary>
        public static object GetStatus()
        {
            return new
            {
                is_compiling = _isCompiling,
                elapsed_seconds = 0.0,
                last_compile_duration = Math.Round((double)_lastCompileDuration, 1),
                last_compile_result = _lastCompileResult,
                compile_count = _compileCount,
                is_domain_reloading = _isDomainReloading
            };
        }
    }
}
