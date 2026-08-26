using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LazyRay.Tools
{
    /// <summary>
    /// Manage scenes: list, load, unload, create, save, set active.
    /// </summary>
    public class SceneManagementTool : IMcpTool
    {
        public string Name => "scene_management";
        public string Description =>
            "Manage scenes in the editor.\n" +
            "Actions:\n" +
            "- list: Show all loaded scenes and scenes in build settings\n" +
            "- load: Load a scene additive (by path or build index)\n" +
            "- unload: Unload a scene (by name)\n" +
            "- set_active: Set the active scene (by name)\n" +
            "- save: Save current or specific scene\n" +
            "- save_all: Save all open scenes\n" +
            "- create: Create a new empty scene\n" +
            "- new: New scene (clears current)";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""description"": ""Action: list, load, unload, set_active, save, save_all, create, new"" },
                ""path"": { ""type"": ""string"", ""description"": ""Scene path for load/create (e.g. 'Assets/Scenes/MyScene.unity')"" },
                ""name"": { ""type"": ""string"", ""description"": ""Scene name for unload/set_active"" },
                ""additive"": { ""type"": ""boolean"", ""description"": ""Load additive (default: true)"" }
            },
            ""required"": [""action""]
        }");

        public string Execute(JObject input)
        {
            string action = input.Value<string>("action");

            try
            {
                return action switch
                {
                    "list" => DoList(),
                    "load" => DoLoad(input),
                    "unload" => DoUnload(input),
                    "set_active" => DoSetActive(input),
                    "save" => DoSave(input),
                    "save_all" => DoSaveAll(),
                    "create" => DoCreate(input),
                    "new" => DoNew(),
                    _ => $"ERROR: Unknown action '{action}'"
                };
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        private string DoList()
        {
            var sb = new StringBuilder();

            // Loaded scenes
            sb.AppendLine("═══ Loaded Scenes ═══");
            var activeScene = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                string flags = "";
                if (scene == activeScene) flags += " [ACTIVE]";
                if (scene.isDirty) flags += " [DIRTY]";
                if (!scene.isLoaded) flags += " [NOT LOADED]";
                sb.AppendLine($"  [{i}] {scene.name}{flags}");
                sb.AppendLine($"      Path: {scene.path}");
                sb.AppendLine($"      Root Objects: {(scene.isLoaded ? scene.rootCount.ToString() : "?")}");
            }

            // Build settings scenes
            var buildScenes = EditorBuildSettings.scenes;
            if (buildScenes.Length > 0)
            {
                sb.AppendLine($"\n═══ Build Settings ({buildScenes.Length} scenes) ═══");
                for (int i = 0; i < buildScenes.Length; i++)
                {
                    var bs = buildScenes[i];
                    // Check if currently loaded
                    bool loaded = false;
                    for (int s = 0; s < SceneManager.sceneCount; s++)
                    {
                        if (SceneManager.GetSceneAt(s).path == bs.path)
                        {
                            loaded = true;
                            break;
                        }
                    }
                    string status = loaded ? " [LOADED]" : "";
                    if (!bs.enabled) status += " [DISABLED]";
                    sb.AppendLine($"  [{i}] {System.IO.Path.GetFileNameWithoutExtension(bs.path)}{status}");
                    sb.AppendLine($"      {bs.path}");
                }
            }

            return sb.ToString();
        }

        private string DoLoad(JObject input)
        {
            string path = input.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return "ERROR: 'path' required (e.g. 'Assets/Scenes/MyScene.unity')";

            bool additive = input.Value<bool?>("additive") ?? true;

            // Check if already loaded
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.path == path && s.isLoaded)
                    return $"Scene '{s.name}' is already loaded.";
            }

            var mode = additive ? OpenSceneMode.Additive : OpenSceneMode.Single;
            var scene = EditorSceneManager.OpenScene(path, mode);

            return $"✅ Loaded '{scene.name}' ({(additive ? "additive" : "single")})\n" +
                   $"  Path: {scene.path}\n" +
                   $"  Root Objects: {scene.rootCount}";
        }

        private string DoUnload(JObject input)
        {
            string name = input.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                return "ERROR: 'name' required";

            if (SceneManager.sceneCount <= 1)
                return "ERROR: Cannot unload the only loaded scene.";

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name == name)
                {
                    if (scene.isDirty)
                    {
                        EditorSceneManager.SaveScene(scene);
                    }
                    EditorSceneManager.CloseScene(scene, true);
                    return $"✅ Unloaded and removed '{name}'";
                }
            }
            return $"ERROR: Scene '{name}' not found in loaded scenes.";
        }

        private string DoSetActive(JObject input)
        {
            string name = input.Value<string>("name");
            if (string.IsNullOrEmpty(name))
                return "ERROR: 'name' required";

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name == name && scene.isLoaded)
                {
                    SceneManager.SetActiveScene(scene);
                    return $"✅ Active scene set to '{name}'";
                }
            }
            return $"ERROR: Scene '{name}' not found or not loaded.";
        }

        private string DoSave(JObject input)
        {
            string name = input.Value<string>("name");

            if (string.IsNullOrEmpty(name))
            {
                // Save active scene
                var active = SceneManager.GetActiveScene();
                EditorSceneManager.SaveScene(active);
                return $"✅ Saved '{active.name}' → {active.path}";
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.name == name)
                {
                    EditorSceneManager.SaveScene(scene);
                    return $"✅ Saved '{name}' → {scene.path}";
                }
            }
            return $"ERROR: Scene '{name}' not found.";
        }

        private string DoSaveAll()
        {
            EditorSceneManager.SaveOpenScenes();
            return $"✅ Saved all {SceneManager.sceneCount} open scenes.";
        }

        private string DoCreate(JObject input)
        {
            string path = input.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return "ERROR: 'path' required (e.g. 'Assets/Scenes/NewScene.unity')";

            if (!path.EndsWith(".unity"))
                path += ".unity";

            // Ensure directory exists
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            EditorSceneManager.SaveScene(newScene, path);

            return $"✅ Created new scene '{newScene.name}' at {path}";
        }

        private string DoNew()
        {
            // Prompt-safe: save all first
            EditorSceneManager.SaveOpenScenes();
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            return "✅ New scene created (default setup). Previous scenes saved.";
        }
    }
}
