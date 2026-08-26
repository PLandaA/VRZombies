using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    public class HierarchyEditTool : IMcpTool
    {
        public string Name => "edit_hierarchy";
        public string Description =>
            "Modify the scene hierarchy. Actions:\n" +
            "- create: Create a GameObject (name, parent path, position, rotation, scale, components[])\n" +
            "- delete: Delete by path\n" +
            "- rename: Rename (path + name)\n" +
            "- move: Reparent (path + new_parent)\n" +
            "- set_active: Enable/disable (path + active)\n" +
            "- add_component: Add component (path + component_type)\n" +
            "- remove_component: Remove component (path + component_type)\n" +
            "- set_property: Set serialized property (path + component_type + property_name + property_value)\n" +
            "- set_transform: Set position/rotation/scale (path + position/rotation/scale as [x,y,z])";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""description"": ""Action: create, delete, rename, move, set_active, add_component, remove_component, set_property, set_transform"" },
                ""path"": { ""type"": ""string"", ""description"": ""GameObject path"" },
                ""name"": { ""type"": ""string"", ""description"": ""Name for create/rename"" },
                ""new_parent"": { ""type"": ""string"", ""description"": ""New parent path for move"" },
                ""active"": { ""type"": ""boolean"", ""description"": ""Active state for set_active"" },
                ""component_type"": { ""type"": ""string"", ""description"": ""Component type name (e.g. 'BoxCollider')"" },
                ""property_name"": { ""type"": ""string"", ""description"": ""Property name for set_property"" },
                ""property_value"": { ""type"": ""string"", ""description"": ""Property value for set_property"" },
                ""position"": { ""type"": ""string"", ""description"": ""Position as JSON array [x,y,z]"" },
                ""rotation"": { ""type"": ""string"", ""description"": ""Rotation as JSON array [x,y,z]"" },
                ""scale"": { ""type"": ""string"", ""description"": ""Scale as JSON array [x,y,z]"" },
                ""components"": { ""type"": ""string"", ""description"": ""Components as JSON array for create"" }
            },
            ""required"": [""action""]
        }");

        public string Execute(JObject input)
        {
            string action = input.Value<string>("action");
            bool force = input.Value<bool?>("force") ?? false;

            // Play mode guard for destructive operations
            var warning = HierarchyUtils.PlayModeGuard(force);
            if (warning != null) return warning;

            return action switch
            {
                "create" => DoCreate(input),
                "delete" => DoDelete(input),
                "rename" => DoRename(input),
                "move" => DoMove(input),
                "set_active" => DoSetActive(input),
                "add_component" => DoAddComponent(input),
                "remove_component" => DoRemoveComponent(input),
                "set_property" => DoSetProperty(input),
                "set_transform" => DoSetTransform(input),
                _ => $"ERROR: Unknown action '{action}'"
            };
        }

        private string DoCreate(JObject input)
        {
            string name = input.Value<string>("name") ?? "New GameObject";
            string parentPath = input.Value<string>("path");
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"LazyRay: Create {name}");

            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = FindByPath(parentPath);
                if (parent == null) return $"ERROR: Parent not found: {parentPath}";
                go.transform.SetParent(parent.transform, false);
            }

            ApplyTransformArrays(go.transform, input);

            // Handle components: accept both JArray and JSON string
            var compsToken = input["components"];
            if (compsToken != null)
            {
                JArray comps;
                if (compsToken.Type == JTokenType.Array)
                    comps = (JArray)compsToken;
                else if (compsToken.Type == JTokenType.String)
                    comps = JArray.Parse(compsToken.ToString());
                else
                    comps = null;

                if (comps != null)
                {
                    foreach (var c in comps)
                    {
                        var type = FindComponentType(c.ToString());
                        if (type != null) Undo.AddComponent(go, type);
                    }
                }
            }

            return $"Created GameObject: {GetPath(go.transform)}";
        }

        private string DoDelete(JObject input)
        {
            string path = input.Value<string>("path");
            if (string.IsNullOrEmpty(path)) return "ERROR: 'path' required.";
            var go = FindByPath(path);
            if (go == null) return $"ERROR: Not found: {path}";
            int children = go.transform.childCount;
            Undo.DestroyObjectImmediate(go);
            return $"Deleted: {path}" + (children > 0 ? $" (+{children} children)" : "");
        }

        private string DoRename(JObject input)
        {
            string path = input.Value<string>("path"), name = input.Value<string>("name");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(name)) return "ERROR: 'path' and 'name' required.";
            var go = FindByPath(path);
            if (go == null) return $"ERROR: Not found: {path}";
            Undo.RecordObject(go, $"LazyRay: Rename {go.name}");
            string old = go.name; go.name = name;
            return $"Renamed: {old} -> {name}";
        }

        private string DoMove(JObject input)
        {
            string path = input.Value<string>("path"), newParent = input.Value<string>("new_parent");
            if (string.IsNullOrEmpty(path)) return "ERROR: 'path' required.";
            var go = FindByPath(path);
            if (go == null) return $"ERROR: Not found: {path}";
            Transform parent = null;
            if (!string.IsNullOrEmpty(newParent))
            {
                var p = FindByPath(newParent);
                if (p == null) return $"ERROR: Parent not found: {newParent}";
                parent = p.transform;
            }
            Undo.SetTransformParent(go.transform, parent, $"LazyRay: Move {go.name}");
            return $"Moved {go.name} -> {GetPath(go.transform)}";
        }

        private string DoSetActive(JObject input)
        {
            string path = input.Value<string>("path");
            bool? active = input.Value<bool?>("active");
            if (string.IsNullOrEmpty(path) || !active.HasValue) return "ERROR: 'path' and 'active' required.";
            var go = FindByPath(path);
            if (go == null) return $"ERROR: Not found: {path}";
            Undo.RecordObject(go, $"LazyRay: SetActive {go.name}");
            go.SetActive(active.Value);
            return $"{go.name} active: {active.Value}";
        }

        private string DoAddComponent(JObject input)
        {
            string path = input.Value<string>("path"), comp = input.Value<string>("component_type");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(comp)) return "ERROR: 'path' and 'component_type' required.";
            var go = FindByPath(path);
            if (go == null) return $"ERROR: Not found: {path}";
            var type = FindComponentType(comp);
            if (type == null) return $"ERROR: Type not found: {comp}";
            Undo.AddComponent(go, type);
            return $"Added {comp} to {go.name}";
        }

        private string DoRemoveComponent(JObject input)
        {
            string path = input.Value<string>("path"), comp = input.Value<string>("component_type");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(comp)) return "ERROR: 'path' and 'component_type' required.";
            var go = FindByPath(path);
            if (go == null) return $"ERROR: Not found: {path}";
            var type = FindComponentType(comp);
            if (type == null) return $"ERROR: Type not found: {comp}";
            var c = go.GetComponent(type);
            if (c == null) return $"ERROR: {go.name} has no {comp}";
            Undo.DestroyObjectImmediate(c);
            return $"Removed {comp} from {go.name}";
        }

        private string DoSetProperty(JObject input)
        {
            string path = input.Value<string>("path"), comp = input.Value<string>("component_type");
            string propName = input.Value<string>("property_name"), propValue = input.Value<string>("property_value");
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(comp) || string.IsNullOrEmpty(propName) || propValue == null)
                return "ERROR: 'path', 'component_type', 'property_name', 'property_value' required.";

            var go = FindByPath(path);
            if (go == null) return $"ERROR: Not found: {path}";
            var type = FindComponentType(comp);
            if (type == null) return $"ERROR: Type not found: {comp}";
            var c = go.GetComponent(type);
            if (c == null) return $"ERROR: {go.name} has no {comp}";

            var so = new SerializedObject(c);
            var prop = so.FindProperty(propName);

            // Fallback: search by display name if raw name not found
            if (prop == null)
            {
                var iter = so.GetIterator();
                if (iter.NextVisible(true))
                {
                    do
                    {
                        if (iter.displayName.Equals(propName, StringComparison.OrdinalIgnoreCase) ||
                            iter.name.Equals(propName, StringComparison.OrdinalIgnoreCase))
                        {
                            prop = iter.Copy();
                            break;
                        }
                    } while (iter.NextVisible(false));
                }
            }

            if (prop == null) { so.Dispose(); return $"ERROR: Property '{propName}' not found on {comp}"; }

            Undo.RecordObject(c, $"LazyRay: Set {propName}");
            bool success;
            try { success = TrySetProp(prop, propValue); }
            catch (Exception ex) { so.Dispose(); return $"ERROR: Exception setting '{propName}': {ex.Message}"; }
            if (!success) { so.Dispose(); return $"ERROR: Could not set '{propName}' ({prop.propertyType}) to '{propValue}'. Check console for details."; }
            so.ApplyModifiedProperties(); so.Dispose();
            return $"Set {go.name}.{comp}.{propName} = {propValue}";
        }

        private string DoSetTransform(JObject input)
        {
            string path = input.Value<string>("path");
            if (string.IsNullOrEmpty(path)) return "ERROR: 'path' required.";
            var go = FindByPath(path);
            if (go == null) return $"ERROR: Not found: {path}";
            Undo.RecordObject(go.transform, $"LazyRay: Transform {go.name}");

            var sb = new StringBuilder($"Transform {go.name}:");
            ApplyTransformArrays(go.transform, input, sb);
            return sb.ToString();
        }

        private void ApplyTransformArrays(Transform t, JObject input, StringBuilder sb = null)
        {
            var pos = ParseVec3(input, "position");
            if (pos.HasValue) { t.localPosition = pos.Value; sb?.Append($" pos={pos.Value}"); }
            var rot = ParseVec3(input, "rotation");
            if (rot.HasValue) { t.localEulerAngles = rot.Value; sb?.Append($" rot={rot.Value}"); }
            var scale = ParseVec3(input, "scale");
            if (scale.HasValue) { t.localScale = scale.Value; sb?.Append($" scale={scale.Value}"); }
        }

        private Vector3? ParseVec3(JObject input, string key)
        {
            var token = input[key];
            if (token == null) return null;
            try
            {
                if (token.Type == JTokenType.String)
                {
                    var arr = JArray.Parse(token.ToString());
                    return new Vector3((float)arr[0], (float)arr[1], (float)arr[2]);
                }
                if (token.Type == JTokenType.Array)
                {
                    var arr = (JArray)token;
                    return new Vector3((float)arr[0], (float)arr[1], (float)arr[2]);
                }
            }
            catch { }
            return null;
        }

        private Type FindComponentType(string name)
        {
            var type = Type.GetType($"UnityEngine.{name}, UnityEngine")
                ?? Type.GetType($"UnityEngine.UI.{name}, UnityEngine.UI")
                ?? Type.GetType($"TMPro.{name}, Unity.TextMeshPro");
            if (type != null) return type;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetTypes().FirstOrDefault(t => t.Name == name && typeof(Component).IsAssignableFrom(t));
                if (type != null) return type;
            }
            return null;
        }

        private bool TrySetProp(SerializedProperty prop, string value)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer: prop.intValue = int.Parse(value); return true;
                    case SerializedPropertyType.Boolean: prop.boolValue = bool.Parse(value); return true;
                    case SerializedPropertyType.Float: prop.floatValue = float.Parse(value); return true;
                    case SerializedPropertyType.String: prop.stringValue = value; return true;
                    case SerializedPropertyType.Enum:
                        if (int.TryParse(value, out int idx)) prop.enumValueIndex = idx;
                        else { int i = Array.IndexOf(prop.enumDisplayNames, value); if (i >= 0) prop.enumValueIndex = i; else return false; }
                        return true;
                    case SerializedPropertyType.Vector3:
                        var v = value.Trim('(', ')').Split(',');
                        prop.vector3Value = new Vector3(float.Parse(v[0]), float.Parse(v[1]), float.Parse(v[2]));
                        return true;
                    case SerializedPropertyType.Vector2:
                        var v2 = value.Trim('(', ')').Split(',');
                        prop.vector2Value = new Vector2(float.Parse(v2[0]), float.Parse(v2[1]));
                        return true;
                    case SerializedPropertyType.Color:
                        var vc = value.Trim('(', ')').Split(',');
                        prop.colorValue = new Color(float.Parse(vc[0]), float.Parse(vc[1]), float.Parse(vc[2]), vc.Length > 3 ? float.Parse(vc[3]) : 1f);
                        return true;
                    case SerializedPropertyType.ObjectReference:
                        return TrySetObjectReference(prop, value);
                    case SerializedPropertyType.LayerMask:
                        prop.intValue = int.Parse(value);
                        return true;
                    case SerializedPropertyType.Generic:
                        if (prop.isArray) return TrySetArray(prop, value);
                        return false;
                    default: return false;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Set an ObjectReference field from an asset path, GUID, "null"/"none", or scene object path.
        /// Supports: ScriptableObjects, Sprites, Textures, Materials, AudioClips, VideoClips,
        /// AnimationClips, GameObjects, Prefabs, and any other Unity Object type.
        /// </summary>
        private bool TrySetObjectReference(SerializedProperty prop, string value)
        {
            // Clear reference
            if (string.IsNullOrEmpty(value) || value.ToLower() == "null" || value.ToLower() == "none")
            {
                prop.objectReferenceValue = null;
                return true;
            }

            UnityEngine.Object obj = null;

            // 1. Try as asset path (e.g. "Assets/SO/MyEvent.asset")
            if (value.StartsWith("Assets/") || value.StartsWith("Packages/"))
            {
                obj = AssetDatabase.LoadMainAssetAtPath(value);
                if (obj == null)
                {
                    // For sprites/sub-assets, try loading all at path
                    var allAtPath = AssetDatabase.LoadAllAssetsAtPath(value);
                    if (allAtPath != null && allAtPath.Length > 1)
                        obj = allAtPath[1]; // [0] is main, [1] is often the sprite
                }
                if (obj != null) Debug.Log($"[LazyRay] Resolved '{value}' as asset: {obj.name} ({obj.GetType().Name})");
            }

            // 2. Try as GUID
            if (obj == null && value.Length == 32 && !value.Contains("/") && !value.Contains("\\"))
            {
                string guidPath = AssetDatabase.GUIDToAssetPath(value);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    obj = AssetDatabase.LoadMainAssetAtPath(guidPath);
                    if (obj != null) Debug.Log($"[LazyRay] Resolved GUID '{value}' → {guidPath}: {obj.name}");
                }
            }

            // 3. Try as "AssetPath:SubAssetName" for sub-assets (sprites in atlases, etc.)
            if (obj == null && value.Contains(":"))
            {
                var parts = value.Split(new[] { ':' }, 2);
                if (parts.Length == 2)
                {
                    var allSub = AssetDatabase.LoadAllAssetsAtPath(parts[0]);
                    if (allSub != null)
                    {
                        obj = allSub.FirstOrDefault(a => a != null && a.name == parts[1]);
                        if (obj != null) Debug.Log($"[LazyRay] Resolved sub-asset '{parts[1]}' from '{parts[0]}': {obj.GetType().Name}");
                    }
                }
            }

            // 4. Try as scene object path (for references to GameObjects/Components in scene)
            if (obj == null)
            {
                var go = FindByPath(value);
                if (go != null) { obj = go; Debug.Log($"[LazyRay] Resolved '{value}' as scene object"); }
            }

            // 5. Try searching by asset name
            if (obj == null)
            {
                string[] guids = AssetDatabase.FindAssets($"\"{value}\"");
                foreach (var guid in guids)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    var candidate = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (candidate != null && candidate.name == value)
                    {
                        obj = candidate;
                        Debug.Log($"[LazyRay] Found by name search '{value}' → {assetPath}");
                        break;
                    }
                }
            }

            if (obj == null)
            {
                Debug.LogWarning($"[LazyRay] TrySetObjectReference FAILED: Could not resolve '{value}' to any asset");
                return false;
            }

            prop.objectReferenceValue = obj;
            return true;
        }

        /// <summary>
        /// Manipulate array/list serialized properties.
        /// Value formats:
        ///   "clear" — empty the array
        ///   "+AssetPath" — append element (ObjectReference) or "+value" for primitives
        ///   "-2" — remove element at index 2
        ///   "3=AssetPath" — set element at index 3
        ///   "[path1,path2,path3]" — replace entire array contents
        /// </summary>
        private bool TrySetArray(SerializedProperty prop, string value)
        {
            if (!prop.isArray) return false;

            value = value.Trim();

            // Clear array
            if (value.ToLower() == "clear")
            {
                prop.ClearArray();
                return true;
            }

            // Append: "+value"
            if (value.StartsWith("+"))
            {
                string itemValue = value.Substring(1).Trim();
                int newIdx = prop.arraySize;
                prop.InsertArrayElementAtIndex(newIdx);
                var element = prop.GetArrayElementAtIndex(newIdx);
                return TrySetProp(element, itemValue);
            }

            // Remove at index: "-N"
            if (value.StartsWith("-") && int.TryParse(value.Substring(1).Trim(), out int removeIdx))
            {
                if (removeIdx >= 0 && removeIdx < prop.arraySize)
                {
                    prop.DeleteArrayElementAtIndex(removeIdx);
                    return true;
                }
                return false;
            }

            // Set at index: "N=value"
            if (value.Contains("=") && !value.StartsWith("["))
            {
                var eqParts = value.Split(new[] { '=' }, 2);
                if (int.TryParse(eqParts[0].Trim(), out int setIdx) && setIdx >= 0 && setIdx < prop.arraySize)
                {
                    var element = prop.GetArrayElementAtIndex(setIdx);
                    return TrySetProp(element, eqParts[1].Trim());
                }
                return false;
            }

            // Replace entire array: "[val1,val2,val3]" or "[path1,path2]"
            if (value.StartsWith("[") && value.EndsWith("]"))
            {
                string inner = value.Substring(1, value.Length - 2).Trim();
                if (string.IsNullOrEmpty(inner))
                {
                    prop.ClearArray();
                    return true;
                }

                // Smart split: handle paths with commas (unlikely but safe)
                var items = SmartSplit(inner);

                prop.ClearArray();
                for (int i = 0; i < items.Count; i++)
                {
                    prop.InsertArrayElementAtIndex(i);
                    var element = prop.GetArrayElementAtIndex(i);
                    if (!TrySetProp(element, items[i].Trim()))
                        Debug.LogWarning($"[LazyRay] Failed to set array element [{i}] = '{items[i]}'");
                }
                return true;
            }

            return false;
        }

        private List<string> SmartSplit(string input)
        {
            var result = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '(' || input[i] == '[') depth++;
                else if (input[i] == ')' || input[i] == ']') depth--;
                else if (input[i] == ',' && depth == 0)
                {
                    result.Add(input.Substring(start, i - start));
                    start = i + 1;
                }
            }
            result.Add(input.Substring(start));
            return result;
        }

        private string GetPath(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        /// <summary>
        /// Find a GameObject by hierarchy path, including inactive objects.
        /// GameObject.Find() only returns active objects — this searches all scene roots recursively.
        /// </summary>
        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            // Try fast path first (active objects)
            var go = GameObject.Find(path);
            if (go != null) return go;

            // Slow path: search inactive objects via scene roots
            string[] parts = path.Split('/');
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
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
            return null;
        }
    }
}
