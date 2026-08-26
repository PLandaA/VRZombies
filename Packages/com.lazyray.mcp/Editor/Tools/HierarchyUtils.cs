using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LazyRay.Tools
{
    /// <summary>
    /// Shared utilities for LazyRay tools.
    /// FindByPath searches ALL loaded scenes (not just active) and finds inactive objects.
    /// </summary>
    public static class HierarchyUtils
    {
        /// <summary>
        /// Find a GameObject by hierarchy path across ALL loaded scenes, including inactive objects.
        /// Supports "SceneName:Path/To/Object" syntax for explicit scene targeting.
        /// </summary>
        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            // Check for scene prefix: "SceneName:ObjectPath"
            string sceneName = null;
            string objectPath = path;
            int colonIdx = path.IndexOf(':');
            if (colonIdx > 0 && colonIdx < path.Length - 1)
            {
                sceneName = path.Substring(0, colonIdx);
                objectPath = path.Substring(colonIdx + 1);
            }

            // Fast path: active objects (only works if no scene prefix)
            if (sceneName == null)
            {
                var go = GameObject.Find(objectPath);
                if (go != null) return go;
            }

            // Search all loaded scenes
            string[] parts = objectPath.Split('/');
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                if (sceneName != null && scene.name != sceneName) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != parts[0]) continue;
                    if (parts.Length == 1) return root;

                    Transform current = root.transform;
                    bool found = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        Transform child = null;
                        for (int c = 0; c < current.childCount; c++)
                        {
                            if (current.GetChild(c).name == parts[i])
                            {
                                child = current.GetChild(c);
                                break;
                            }
                        }
                        if (child == null) { found = false; break; }
                        current = child;
                    }
                    if (found) return current.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Check if Unity is in play mode. Returns warning message or null.
        /// </summary>
        public static string PlayModeGuard(bool allowInPlayMode = false)
        {
            if (!EditorApplication.isPlaying) return null;
            if (allowInPlayMode) return null;
            return "⚠ Unity is in Play Mode. Scene modifications will be lost when you exit Play Mode. " +
                   "Consider stopping Play Mode first, or pass force=true to proceed anyway.";
        }
    }
}
