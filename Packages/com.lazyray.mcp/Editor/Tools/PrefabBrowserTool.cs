using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LazyRay.Tools
{
    /// <summary>
    /// Browse, inspect and instantiate prefabs.
    /// Actions: list, inspect, instantiate
    /// </summary>
    public class PrefabBrowserTool : IMcpTool
    {
        public string Name => "prefab_browser";
        public string Description =>
            "Browse, inspect, and instantiate prefabs.\n" +
            "Actions:\n" +
            "- list: List prefabs in a folder (path, search filter)\n" +
            "- inspect: Show prefab hierarchy and components\n" +
            "- instantiate: Place a prefab in the scene (with optional position/rotation/parent)";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""description"": ""Action: 'list', 'inspect', 'instantiate'"" },
                ""path"": { ""type"": ""string"", ""description"": ""Folder path for list, or asset path for inspect/instantiate"" },
                ""search"": { ""type"": ""string"", ""description"": ""Filter prefab names (for list action)"" },
                ""position"": { ""type"": ""string"", ""description"": ""Position as [x,y,z] JSON array (for instantiate)"" },
                ""rotation"": { ""type"": ""string"", ""description"": ""Rotation as [x,y,z] euler angles (for instantiate)"" },
                ""parent"": { ""type"": ""string"", ""description"": ""Parent GameObject path (for instantiate)"" },
                ""name"": { ""type"": ""string"", ""description"": ""Override name for instantiated object"" },
                ""max_results"": { ""type"": ""integer"", ""description"": ""Max results for list (default 50)"" }
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
                    "list" => DoList(input),
                    "inspect" => DoInspect(input),
                    "instantiate" => DoInstantiate(input),
                    _ => $"ERROR: Unknown action '{action}'. Use: list, inspect, instantiate"
                };
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        // ─── List Prefabs ──────────────────────────────────────────────

        private string DoList(JObject input)
        {
            string folder = input.Value<string>("path") ?? "Assets";
            string search = input.Value<string>("search") ?? "";
            int maxResults = Mathf.Clamp(input.Value<int?>("max_results") ?? 50, 1, 200);

            // Find all prefab GUIDs
            string[] guids;
            if (string.IsNullOrEmpty(search))
                guids = AssetDatabase.FindAssets("t:Prefab", new[] { folder });
            else
                guids = AssetDatabase.FindAssets($"{search} t:Prefab", new[] { folder });

            if (guids.Length == 0)
                return $"No prefabs found in '{folder}'{(string.IsNullOrEmpty(search) ? "" : $" matching '{search}'")}";

            var sb = new StringBuilder();
            sb.AppendLine($"Prefabs in {folder}{(string.IsNullOrEmpty(search) ? "" : $" matching '{search}'")} ({guids.Length} found):\n");

            int shown = 0;
            foreach (var guid in guids)
            {
                if (shown >= maxResults) break;
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null) continue;

                // Quick stats
                var comps = prefab.GetComponentsInChildren<Component>(true);
                var transforms = prefab.GetComponentsInChildren<Transform>(true);
                var renderers = prefab.GetComponentsInChildren<Renderer>(true);

                // Top-level component list
                var topComps = prefab.GetComponents<Component>()
                    .Where(c => c != null && !(c is Transform))
                    .Select(c => c.GetType().Name);

                string compStr = topComps.Any() ? $" [{string.Join(", ", topComps)}]" : "";

                sb.AppendLine($"  {prefab.name}{compStr}");
                sb.AppendLine($"    Path: {assetPath}");
                sb.AppendLine($"    Objects: {transforms.Length} | Renderers: {renderers.Length} | Components: {comps.Length}");
                shown++;
            }

            if (guids.Length > maxResults)
                sb.AppendLine($"\n  ... +{guids.Length - maxResults} more (use max_results to see more)");

            return sb.ToString();
        }

        // ─── Inspect Prefab ────────────────────────────────────────────

        private string DoInspect(JObject input)
        {
            string path = input.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return "ERROR: 'path' required for inspect (e.g. 'Assets/Prefabs/Player.prefab')";

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                return $"ERROR: Prefab not found at '{path}'";

            var sb = new StringBuilder();
            sb.AppendLine($"═══ Prefab: {prefab.name} ═══");
            sb.AppendLine($"  Path: {path}");

            // Prefab type
            var prefabType = PrefabUtility.GetPrefabAssetType(prefab);
            sb.AppendLine($"  Prefab Type: {prefabType}");

            // Hierarchy
            sb.AppendLine($"\n  ── Hierarchy ──");
            WritePrefabTree(sb, prefab.transform, "    ", 0, 5);

            // Stats
            var allT = prefab.GetComponentsInChildren<Transform>(true);
            var allR = prefab.GetComponentsInChildren<Renderer>(true);
            var allC = prefab.GetComponentsInChildren<Collider>(true);
            var allComps = prefab.GetComponentsInChildren<Component>(true);

            sb.AppendLine($"\n  ── Stats ──");
            sb.AppendLine($"  Total GameObjects: {allT.Length}");
            sb.AppendLine($"  Renderers: {allR.Length}");
            sb.AppendLine($"  Colliders: {allC.Length}");

            // Vert/tri count
            long verts = 0, tris = 0;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true).Where(m => m.sharedMesh != null))
            {
                verts += mf.sharedMesh.vertexCount;
                tris += mf.sharedMesh.triangles.Length / 3;
            }
            foreach (var smr in allR.OfType<SkinnedMeshRenderer>().Where(s => s.sharedMesh != null))
            {
                verts += smr.sharedMesh.vertexCount;
                tris += smr.sharedMesh.triangles.Length / 3;
            }
            if (verts > 0)
            {
                sb.AppendLine($"  Vertices: {verts:N0}");
                sb.AppendLine($"  Triangles: {tris:N0}");
            }

            // Materials
            var mats = allR.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
            if (mats.Count > 0)
            {
                sb.AppendLine($"\n  ── Materials ({mats.Count}) ──");
                foreach (var mat in mats)
                {
                    string matPath = AssetDatabase.GetAssetPath(mat);
                    sb.AppendLine($"    {mat.name} (Shader: {mat.shader?.name ?? "None"}){(string.IsNullOrEmpty(matPath) ? "" : $" @ {matPath}")}");
                }
            }

            // Component type breakdown
            var typeGroups = allComps.Where(c => c != null)
                .GroupBy(c => c.GetType().Name)
                .OrderByDescending(g => g.Count());
            sb.AppendLine($"\n  ── Components ──");
            foreach (var g in typeGroups)
                sb.AppendLine($"    {g.Key}: {g.Count()}");

            return sb.ToString();
        }

        private void WritePrefabTree(StringBuilder sb, Transform t, string indent, int depth, int maxDepth)
        {
            var comps = t.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform))
                .Select(c => c.GetType().Name);
            string compStr = comps.Any() ? $" [{string.Join(", ", comps)}]" : "";
            string active = t.gameObject.activeSelf ? "" : " (inactive)";
            sb.AppendLine($"{indent}{t.name}{compStr}{active}");

            if (depth >= maxDepth && t.childCount > 0)
            {
                sb.AppendLine($"{indent}  ... ({t.childCount} children)");
                return;
            }

            for (int i = 0; i < t.childCount; i++)
                WritePrefabTree(sb, t.GetChild(i), indent + "  ", depth + 1, maxDepth);
        }

        // ─── Instantiate Prefab ────────────────────────────────────────

        private string DoInstantiate(JObject input)
        {
            string path = input.Value<string>("path");
            if (string.IsNullOrEmpty(path))
                return "ERROR: 'path' required (e.g. 'Assets/Prefabs/Player.prefab')";

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                return $"ERROR: Prefab not found at '{path}'";

            // Parse optional parameters
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;

            string posStr = input.Value<string>("position");
            if (!string.IsNullOrEmpty(posStr))
            {
                try
                {
                    var arr = JArray.Parse(posStr);
                    position = new Vector3(arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>());
                }
                catch { return $"ERROR: Invalid position format. Use [x,y,z]"; }
            }

            string rotStr = input.Value<string>("rotation");
            if (!string.IsNullOrEmpty(rotStr))
            {
                try
                {
                    var arr = JArray.Parse(rotStr);
                    rotation = Quaternion.Euler(arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>());
                }
                catch { return $"ERROR: Invalid rotation format. Use [x,y,z]"; }
            }

            // Instantiate
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
                return $"ERROR: Failed to instantiate prefab '{path}'";

            Undo.RegisterCreatedObjectUndo(instance, $"Instantiate {prefab.name}");

            instance.transform.position = position;
            instance.transform.rotation = rotation;

            // Optional name override
            string name = input.Value<string>("name");
            if (!string.IsNullOrEmpty(name))
                instance.name = name;

            // Optional parent
            string parentPath = input.Value<string>("parent");
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = FindByPath(parentPath);
                if (parent != null)
                {
                    Undo.SetTransformParent(instance.transform, parent.transform, $"Parent {instance.name}");
                    instance.transform.localPosition = position;
                    instance.transform.localRotation = rotation;
                }
                else
                {
                    return $"Instantiated '{instance.name}' at {position} but parent '{parentPath}' not found.";
                }
            }

            // Mark scene dirty
            EditorUtility.SetDirty(instance);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(instance.scene);

            var sb = new StringBuilder();
            sb.AppendLine($"✅ Instantiated '{instance.name}' from {path}");
            sb.AppendLine($"  Position: {instance.transform.position}");
            sb.AppendLine($"  Rotation: {instance.transform.eulerAngles}");
            if (!string.IsNullOrEmpty(parentPath))
                sb.AppendLine($"  Parent: {parentPath}");
            sb.AppendLine($"  Prefab link: Maintained (use Undo to remove)");

            return sb.ToString();
        }

        private GameObject FindByPath(string path)
        {
            var go = GameObject.Find(path);
            if (go != null) return go;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name == path) return root;
                    var found = FindChildByPath(root.transform, path);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private GameObject FindChildByPath(Transform parent, string path)
        {
            string[] parts = path.Split('/');
            if (parts[0] != parent.name) return null;
            Transform current = parent;
            for (int i = 1; i < parts.Length; i++)
            {
                Transform child = null;
                for (int j = 0; j < current.childCount; j++)
                {
                    if (current.GetChild(j).name == parts[i]) { child = current.GetChild(j); break; }
                }
                if (child == null) return null;
                current = child;
            }
            return current.gameObject;
        }
    }
}
