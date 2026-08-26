using System.IO;
using UnityEngine;

namespace LazyRay.Core
{
    /// <summary>
    /// v6.4: Project paths cached on the main thread at server start, so
    /// thread-safe tools (read_file, search_files, view_folder_structure)
    /// can run on the pipe thread without touching Application.dataPath —
    /// a main-thread-only API and the ONLY Unity API those tools needed.
    /// </summary>
    public static class LazyRayPaths
    {
        private static string _projectRoot;

        /// <summary>Call once from the main thread (server start).</summary>
        public static void Initialize()
        {
            if (_projectRoot != null) return;
            try { _projectRoot = Path.GetDirectoryName(Application.dataPath); }
            catch { /* not on main thread or too early — lazy path below */ }
        }

        public static string ProjectRoot
        {
            get
            {
                if (_projectRoot == null)
                {
                    // Lazy fallback: works if we happen to be on the main
                    // thread; throws a clear error otherwise.
                    try { _projectRoot = Path.GetDirectoryName(Application.dataPath); }
                    catch (System.Exception ex)
                    {
                        throw new System.InvalidOperationException(
                            "LazyRayPaths not initialized — call Initialize() from the main thread first.", ex);
                    }
                }
                return _projectRoot;
            }
        }
    }
}
