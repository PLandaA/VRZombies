using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LazyRay.Tools
{
    public class HierarchyViewTool : IMcpTool
    {
        public string Name => "view_hierarchy";
        public string Description =>
            "View the scene hierarchy. Shows GameObjects and their components. " +
            "Use 'path' for a specific GameObject (e.g. 'Canvas/Panel'). " +
            "Use 'detailed'=true to see component properties.";

        public bool IsDestructive => false;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""path"": { ""type"": ""string"", ""description"": ""Path to specific GameObject (optional)"" },
                ""max_depth"": { ""type"": ""integer"", ""description"": ""Max depth (1-10, default 5)"" },
                ""detailed"": { ""type"": ""boolean"", ""description"": ""Show serialized properties (default false)"" },
                ""scene_index"": { ""type"": ""integer"", ""description"": ""Scene index (default 0)"" }
            },
            ""required"": []
        }");

        public string Execute(JObject input)
        {
            string path = input.Value<string>("path");
            int maxDepth = Mathf.Clamp(input.Value<int?>("max_depth") ?? 5, 1, 10);
            bool detailed = input.Value<bool?>("detailed") ?? false;
            int sceneIndex = input.Value<int?>("scene_index") ?? 0;

            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(path))
            {
                var go = HierarchyUtils.FindByPath(path);
                if (go == null) return $"ERROR: GameObject not found at path: {path}";

                sb.AppendLine($"GameObject: {path}");
                sb.AppendLine($"   Active: {go.activeSelf} | Layer: {LayerMask.LayerToName(go.layer)} | Tag: {go.tag}");
                sb.AppendLine($"   Position: {go.transform.localPosition}");
                sb.AppendLine($"   Rotation: {go.transform.localEulerAngles}");
                sb.AppendLine($"   Scale: {go.transform.localScale}");
                DescribeComponents(sb, go, "   ", detailed);

                if (go.transform.childCount > 0)
                {
                    sb.AppendLine($"\n   Children ({go.transform.childCount}):");
                    BuildTree(sb, go.transform, "   ", maxDepth, 0, detailed);
                }
                return sb.ToString();
            }

            Scene scene = sceneIndex < SceneManager.sceneCount
                ? SceneManager.GetSceneAt(sceneIndex) : SceneManager.GetActiveScene();

            sb.AppendLine($"Scene: {scene.name} ({scene.path})");
            sb.AppendLine($"   Root objects: {scene.rootCount}\n");

            foreach (var root in scene.GetRootGameObjects().OrderBy(go => go.transform.GetSiblingIndex()))
            {
                string tag = root.activeSelf ? "" : " [INACTIVE]";
                int children = CountChildren(root.transform);
                sb.AppendLine($"|- {root.name}{tag} [{GetComponentSummary(root)}] ({children} children)");
                BuildTree(sb, root.transform, "|  ", maxDepth, 0, detailed);
            }
            return sb.ToString();
        }

        private void BuildTree(StringBuilder sb, Transform parent, string indent, int maxDepth, int depth, bool detailed)
        {
            if (depth >= maxDepth)
            {
                if (parent.childCount > 0) sb.AppendLine($"{indent}+-- ... ({parent.childCount} more)");
                return;
            }
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                bool last = i == parent.childCount - 1;
                string conn = last ? "+-- " : "|- ";
                string next = indent + (last ? "    " : "|  ");
                string tag = child.gameObject.activeSelf ? "" : " [INACTIVE]";
                sb.AppendLine($"{indent}{conn}{child.name}{tag} [{GetComponentSummary(child.gameObject)}]");
                if (detailed) DescribeComponents(sb, child.gameObject, next, true);
                BuildTree(sb, child, next, maxDepth, depth + 1, detailed);
            }
        }

        private void DescribeComponents(StringBuilder sb, GameObject go, string indent, bool detailed)
        {
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is Transform) continue;
                sb.AppendLine($"{indent}[{comp.GetType().Name}]");
                if (detailed)
                {
                    var so = new SerializedObject(comp);
                    var prop = so.GetIterator();
                    if (prop.NextVisible(true))
                    {
                        int shown = 0;
                        do
                        {
                            if (prop.name == "m_Script") continue;
                            sb.AppendLine($"{indent}   {prop.displayName}: {GetPropValue(prop)}");
                            if (++shown > 15) { sb.AppendLine($"{indent}   ... (more)"); break; }
                        } while (prop.NextVisible(false));
                    }
                    so.Dispose();
                }
            }
        }

        private string GetPropValue(SerializedProperty prop) => prop.propertyType switch
        {
            SerializedPropertyType.Integer => prop.intValue.ToString(),
            SerializedPropertyType.Boolean => prop.boolValue.ToString(),
            SerializedPropertyType.Float => prop.floatValue.ToString("F3"),
            SerializedPropertyType.String => $"\"{(prop.stringValue.Length > 60 ? prop.stringValue.Substring(0, 60) + "..." : prop.stringValue)}\"",
            SerializedPropertyType.ObjectReference => prop.objectReferenceValue != null ? $"{prop.objectReferenceValue.name} ({prop.objectReferenceValue.GetType().Name})" : "None",
            SerializedPropertyType.Enum => prop.enumDisplayNames.Length > prop.enumValueIndex && prop.enumValueIndex >= 0 ? prop.enumDisplayNames[prop.enumValueIndex] : prop.enumValueIndex.ToString(),
            SerializedPropertyType.Vector2 => prop.vector2Value.ToString(),
            SerializedPropertyType.Vector3 => prop.vector3Value.ToString(),
            SerializedPropertyType.Color => prop.colorValue.ToString(),
            _ => $"({prop.propertyType})"
        };

        private string GetComponentSummary(GameObject go) =>
            string.Join(", ", go.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name));

        private int CountChildren(Transform t)
        {
            int count = t.childCount;
            for (int i = 0; i < t.childCount; i++) count += CountChildren(t.GetChild(i));
            return count;
        }

        /// <summary>
        /// Find a GameObject by hierarchy path, including inactive objects.
        /// </summary>
        private GameObject FindByPath(string path)
        {
            // Try fast path first (only finds active)
            var go = GameObject.Find(path);
            if (go != null) return go;

            // Search including inactive objects
            string[] parts = path.Split('/');
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;
                if (parts.Length == 1) return root;

                Transform current = root.transform;
                bool found = true;
                for (int i = 1; i < parts.Length; i++)
                {
                    Transform child = null;
                    for (int j = 0; j < current.childCount; j++)
                    {
                        if (current.GetChild(j).name == parts[i])
                        {
                            child = current.GetChild(j);
                            break;
                        }
                    }
                    if (child == null) { found = false; break; }
                    current = child;
                }
                if (found) return current.gameObject;
            }
            return null;
        }
    }
}
