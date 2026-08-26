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
    /// Deep inspector for GameObjects and Assets.
    /// Returns complete serialized property data with nested structures,
    /// array contents, asset references, material/shader properties, etc.
    /// </summary>
    public class InspectorTool : IMcpTool
    {
        public string Name => "inspect";
        public string Description =>
            "Deep inspect a GameObject or Asset. Returns all serialized properties.\n" +
            "Modes:\n" +
            "- gameobject: Inspect by hierarchy path (e.g. 'Canvas/Panel')\n" +
            "- asset: Inspect by asset path (e.g. 'Assets/Materials/Wood.mat')\n" +
            "- component: Inspect a specific component on a GameObject\n" +
            "Options:\n" +
            "- max_depth: How deep to recurse into nested properties (default 3)\n" +
            "- filter: Only show properties matching this string";

        public bool IsDestructive => false;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""mode"": { ""type"": ""string"", ""description"": ""Mode: 'gameobject' (default), 'asset', 'component'"" },
                ""path"": { ""type"": ""string"", ""description"": ""GameObject hierarchy path or asset path"" },
                ""component"": { ""type"": ""string"", ""description"": ""Component type name to inspect (for 'component' mode)"" },
                ""max_depth"": { ""type"": ""integer"", ""description"": ""Max depth for nested properties (default 3, max 6)"" },
                ""filter"": { ""type"": ""string"", ""description"": ""Filter properties by name (case-insensitive)"" }
            },
            ""required"": [""path""]
        }");

        public string Execute(JObject input)
        {
            string mode = input.Value<string>("mode") ?? "gameobject";
            string path = input.Value<string>("path");
            string component = input.Value<string>("component");
            int maxDepth = Mathf.Clamp(input.Value<int?>("max_depth") ?? 3, 1, 6);
            string filter = input.Value<string>("filter");

            if (string.IsNullOrEmpty(path))
                return "ERROR: 'path' is required.";

            try
            {
                return mode switch
                {
                    "gameobject" => InspectGameObject(path, maxDepth, filter),
                    "asset" => InspectAsset(path, maxDepth, filter),
                    "component" => InspectComponent(path, component, maxDepth, filter),
                    _ => $"ERROR: Unknown mode '{mode}'. Use: gameobject, asset, component"
                };
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        // ─── GameObject Inspector ──────────────────────────────────────

        private string InspectGameObject(string path, int maxDepth, string filter)
        {
            var go = FindByPath(path);
            if (go == null)
                return $"ERROR: GameObject '{path}' not found.";

            var sb = new StringBuilder();
            sb.AppendLine($"═══ GameObject: {go.name} ═══");
            sb.AppendLine($"  Active: {go.activeSelf} (in hierarchy: {go.activeInHierarchy})");
            sb.AppendLine($"  Layer: {LayerMask.LayerToName(go.layer)} ({go.layer})");
            sb.AppendLine($"  Tag: {go.tag}");
            sb.AppendLine($"  Static: {go.isStatic}");
            sb.AppendLine($"  Scene: {go.scene.name}");

            var t = go.transform;
            sb.AppendLine($"  Transform:");
            sb.AppendLine($"    Local Position: {Format(t.localPosition)}");
            sb.AppendLine($"    Local Rotation: {Format(t.localEulerAngles)}");
            sb.AppendLine($"    Local Scale: {Format(t.localScale)}");
            sb.AppendLine($"    World Position: {Format(t.position)}");
            sb.AppendLine($"    World Rotation: {Format(t.eulerAngles)}");
            sb.AppendLine($"    Lossy Scale: {Format(t.lossyScale)}");
            sb.AppendLine($"  Children: {t.childCount}");
            for (int i = 0; i < Mathf.Min(t.childCount, 20); i++)
                sb.AppendLine($"    [{i}] {t.GetChild(i).name}{(t.GetChild(i).gameObject.activeSelf ? "" : " (inactive)")}");
            if (t.childCount > 20) sb.AppendLine($"    ... +{t.childCount - 20} more");

            // All components
            var components = go.GetComponents<Component>();
            sb.AppendLine($"\n  Components ({components.Length}):");

            foreach (var comp in components)
            {
                if (comp == null)
                {
                    sb.AppendLine("\n  ╔═ [Missing Script] ═╗");
                    continue;
                }

                if (comp is Transform) continue; // Already shown above

                sb.AppendLine($"\n  ╔═ [{comp.GetType().Name}] ═╗");

                // Enabled state for Behaviours
                if (comp is Behaviour b)
                    sb.AppendLine($"  ║ Enabled: {b.enabled}");
                else if (comp is Renderer r)
                    sb.AppendLine($"  ║ Enabled: {r.enabled}");
                else if (comp is Collider c)
                    sb.AppendLine($"  ║ Enabled: {c.enabled}");

                WriteSerializedProperties(sb, comp, maxDepth, filter, "  ║ ");

                // Special: Renderer materials
                if (comp is Renderer renderer)
                    WriteMaterialInfo(sb, renderer.sharedMaterials, "  ║ ");
            }

            return sb.ToString();
        }

        // ─── Component Inspector ───────────────────────────────────────

        private string InspectComponent(string path, string componentType, int maxDepth, string filter)
        {
            if (string.IsNullOrEmpty(componentType))
                return "ERROR: 'component' is required for component mode.";

            var go = FindByPath(path);
            if (go == null)
                return $"ERROR: GameObject '{path}' not found.";

            var comp = go.GetComponents<Component>()
                .FirstOrDefault(c => c != null && c.GetType().Name == componentType);

            if (comp == null)
                return $"ERROR: Component '{componentType}' not found on '{path}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"═══ {componentType} on {go.name} ═══");

            if (comp is Behaviour b) sb.AppendLine($"  Enabled: {b.enabled}");

            WriteSerializedProperties(sb, comp, maxDepth, filter, "  ");

            if (comp is Renderer renderer)
                WriteMaterialInfo(sb, renderer.sharedMaterials, "  ");

            return sb.ToString();
        }

        // ─── Asset Inspector ───────────────────────────────────────────

        private string InspectAsset(string assetPath, int maxDepth, string filter)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null)
                return $"ERROR: Asset not found at '{assetPath}'.";

            var sb = new StringBuilder();
            var type = asset.GetType();
            sb.AppendLine($"═══ Asset: {asset.name} ═══");
            sb.AppendLine($"  Type: {type.FullName}");
            sb.AppendLine($"  Path: {assetPath}");
            sb.AppendLine($"  GUID: {AssetDatabase.AssetPathToGUID(assetPath)}");

            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer != null)
                sb.AppendLine($"  Importer: {importer.GetType().Name}");

            // Subsets
            var subAssets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (subAssets.Length > 1)
            {
                sb.AppendLine($"  Sub-assets ({subAssets.Length - 1}):");
                foreach (var sub in subAssets)
                {
                    if (sub == asset || sub == null) continue;
                    sb.AppendLine($"    - {sub.name} ({sub.GetType().Name})");
                }
            }

            sb.AppendLine();

            // Type-specific inspection
            if (asset is Material mat)
                WriteMaterialDeep(sb, mat, maxDepth, filter, "  ");
            else if (asset is Texture tex)
                WriteTextureInfo(sb, tex, "  ");
            else if (asset is Mesh mesh)
                WriteMeshInfo(sb, mesh, "  ");
            else if (asset is GameObject prefab)
                WritePrefabInfo(sb, prefab, maxDepth, "  ");
            else if (type.Name == "AnimatorController")
                WriteAnimatorInfo(sb, asset, "  ");
            else
            {
                // Generic: dump all serialized properties
                WriteSerializedProperties(sb, asset, maxDepth, filter, "  ");
            }

            return sb.ToString();
        }

        // ─── Serialized Property Writer ────────────────────────────────

        private void WriteSerializedProperties(StringBuilder sb, UnityEngine.Object obj, int maxDepth, string filter, string indent)
        {
            var so = new SerializedObject(obj);
            var prop = so.GetIterator();

            if (!prop.NextVisible(true))
            {
                sb.AppendLine($"{indent}(no visible properties)");
                so.Dispose();
                return;
            }

            int count = 0;
            do
            {
                if (prop.name == "m_Script") continue;
                if (!string.IsNullOrEmpty(filter) &&
                    prop.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    prop.displayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                WriteProperty(sb, prop, indent, 0, maxDepth);
                count++;

                if (count > 100)
                {
                    sb.AppendLine($"{indent}... (truncated at 100 properties, use 'filter' to narrow)");
                    break;
                }
            } while (prop.NextVisible(false));

            so.Dispose();
        }

        private void WriteProperty(StringBuilder sb, SerializedProperty prop, string indent, int depth, int maxDepth)
        {
            string name = prop.displayName;
            string rawName = prop.name;

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    sb.AppendLine($"{indent}{name}: {prop.intValue}");
                    break;
                case SerializedPropertyType.Boolean:
                    sb.AppendLine($"{indent}{name}: {prop.boolValue}");
                    break;
                case SerializedPropertyType.Float:
                    sb.AppendLine($"{indent}{name}: {prop.floatValue:F4}");
                    break;
                case SerializedPropertyType.String:
                    sb.AppendLine($"{indent}{name}: \"{prop.stringValue}\"");
                    break;
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    sb.AppendLine($"{indent}{name}: RGBA({c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3})");
                    break;
                case SerializedPropertyType.ObjectReference:
                    if (prop.objectReferenceValue != null)
                    {
                        var obj = prop.objectReferenceValue;
                        string assetPath = AssetDatabase.GetAssetPath(obj);
                        sb.AppendLine($"{indent}{name}: {obj.name} ({obj.GetType().Name}){(string.IsNullOrEmpty(assetPath) ? "" : $" @ {assetPath}")}");
                    }
                    else
                        sb.AppendLine($"{indent}{name}: None");
                    break;
                case SerializedPropertyType.Enum:
                    sb.AppendLine($"{indent}{name}: {(prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length ? prop.enumDisplayNames[prop.enumValueIndex] : prop.intValue.ToString())}");
                    break;
                case SerializedPropertyType.Vector2:
                    sb.AppendLine($"{indent}{name}: {Format(prop.vector2Value)}");
                    break;
                case SerializedPropertyType.Vector3:
                    sb.AppendLine($"{indent}{name}: {Format(prop.vector3Value)}");
                    break;
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    sb.AppendLine($"{indent}{name}: ({v4.x:F3}, {v4.y:F3}, {v4.z:F3}, {v4.w:F3})");
                    break;
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    sb.AppendLine($"{indent}{name}: (x:{r.x:F2}, y:{r.y:F2}, w:{r.width:F2}, h:{r.height:F2})");
                    break;
                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    sb.AppendLine($"{indent}{name}: center={Format(b.center)} size={Format(b.size)}");
                    break;
                case SerializedPropertyType.LayerMask:
                    sb.AppendLine($"{indent}{name}: {prop.intValue} (LayerMask)");
                    break;
                case SerializedPropertyType.ArraySize:
                    sb.AppendLine($"{indent}{name}: {prop.intValue}");
                    break;
                case SerializedPropertyType.AnimationCurve:
                    var curve = prop.animationCurveValue;
                    sb.AppendLine($"{indent}{name}: AnimationCurve ({curve.keys.Length} keys)");
                    break;
                case SerializedPropertyType.Gradient:
                    sb.AppendLine($"{indent}{name}: (Gradient)");
                    break;
                case SerializedPropertyType.Generic:
                    // Nested structure or array
                    if (prop.isArray)
                    {
                        sb.AppendLine($"{indent}{name}: [{prop.arraySize} elements]");
                        if (depth < maxDepth && prop.arraySize > 0)
                        {
                            int show = Mathf.Min(prop.arraySize, 10);
                            for (int i = 0; i < show; i++)
                            {
                                var element = prop.GetArrayElementAtIndex(i);
                                WriteProperty(sb, element, indent + "  ", depth + 1, maxDepth);
                            }
                            if (prop.arraySize > show)
                                sb.AppendLine($"{indent}  ... +{prop.arraySize - show} more");
                        }
                    }
                    else if (depth < maxDepth)
                    {
                        sb.AppendLine($"{indent}{name}:");
                        var child = prop.Copy();
                        var end = prop.Copy();
                        end.Next(false); // skip to next sibling
                        if (child.Next(true))
                        {
                            int childCount = 0;
                            do
                            {
                                if (SerializedProperty.EqualContents(child, end)) break;
                                WriteProperty(sb, child, indent + "  ", depth + 1, maxDepth);
                                childCount++;
                                if (childCount > 20)
                                {
                                    sb.AppendLine($"{indent}  ... (truncated)");
                                    break;
                                }
                            } while (child.Next(false));
                        }
                    }
                    else
                    {
                        sb.AppendLine($"{indent}{name}: {(prop.isArray ? $"[{prop.arraySize}]" : "(nested)")}");
                    }
                    break;
                default:
                    sb.AppendLine($"{indent}{name}: ({prop.propertyType})");
                    break;
            }
        }

        // ─── Material Deep Inspect ─────────────────────────────────────

        private void WriteMaterialDeep(StringBuilder sb, Material mat, int maxDepth, string filter, string indent)
        {
            sb.AppendLine($"{indent}Shader: {mat.shader?.name ?? "None"}");
            sb.AppendLine($"{indent}Render Queue: {mat.renderQueue}");
            sb.AppendLine($"{indent}Pass Count: {mat.passCount}");
            sb.AppendLine($"{indent}Enable Instancing: {mat.enableInstancing}");

            var keywords = mat.shaderKeywords;
            if (keywords.Length > 0)
            {
                sb.AppendLine($"{indent}Keywords ({keywords.Length}):");
                foreach (var kw in keywords)
                    sb.AppendLine($"{indent}  - {kw}");
            }

            // Shader properties
            if (mat.shader == null) return;
            int propCount = mat.shader.GetPropertyCount();
            sb.AppendLine($"\n{indent}Shader Properties ({propCount}):");

            for (int i = 0; i < propCount; i++)
            {
                string propName = mat.shader.GetPropertyName(i);
                string desc = mat.shader.GetPropertyDescription(i);
                var propType = mat.shader.GetPropertyType(i);

                if (!string.IsNullOrEmpty(filter) &&
                    propName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    desc.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string value = propType switch
                {
                    UnityEngine.Rendering.ShaderPropertyType.Color => FormatColor(mat.GetColor(propName)),
                    UnityEngine.Rendering.ShaderPropertyType.Vector => Format(mat.GetVector(propName)),
                    UnityEngine.Rendering.ShaderPropertyType.Float => mat.GetFloat(propName).ToString("F4"),
                    UnityEngine.Rendering.ShaderPropertyType.Range => $"{mat.GetFloat(propName):F4} (range: {mat.shader.GetPropertyRangeLimits(i).x:F2}..{mat.shader.GetPropertyRangeLimits(i).y:F2})",
                    UnityEngine.Rendering.ShaderPropertyType.Texture => FormatTexture(mat.GetTexture(propName)),
                    UnityEngine.Rendering.ShaderPropertyType.Int => mat.GetInteger(propName).ToString(),
                    _ => $"({propType})"
                };

                sb.AppendLine($"{indent}  {desc} ({propName}): {value}");

                // For textures, show scale/offset
                if (propType == UnityEngine.Rendering.ShaderPropertyType.Texture)
                {
                    var scale = mat.GetTextureScale(propName);
                    var offset = mat.GetTextureOffset(propName);
                    if (scale != Vector2.one || offset != Vector2.zero)
                        sb.AppendLine($"{indent}    Tiling: {Format(scale)} Offset: {Format(offset)}");
                }
            }
        }

        private void WriteMaterialInfo(StringBuilder sb, Material[] materials, string indent)
        {
            if (materials == null || materials.Length == 0) return;
            sb.AppendLine($"{indent}Materials ({materials.Length}):");
            for (int i = 0; i < materials.Length; i++)
            {
                var mat = materials[i];
                if (mat == null)
                {
                    sb.AppendLine($"{indent}  [{i}] None");
                    continue;
                }
                string path = AssetDatabase.GetAssetPath(mat);
                sb.AppendLine($"{indent}  [{i}] {mat.name} (Shader: {mat.shader?.name ?? "None"}){(string.IsNullOrEmpty(path) ? "" : $" @ {path}")}");
            }
        }

        // ─── Texture Info ──────────────────────────────────────────────

        private void WriteTextureInfo(StringBuilder sb, Texture tex, string indent)
        {
            sb.AppendLine($"{indent}Dimensions: {tex.width}x{tex.height}");
            sb.AppendLine($"{indent}Filter Mode: {tex.filterMode}");
            sb.AppendLine($"{indent}Wrap Mode: {tex.wrapMode}");
            sb.AppendLine($"{indent}Aniso Level: {tex.anisoLevel}");

            if (tex is Texture2D tex2d)
            {
                sb.AppendLine($"{indent}Format: {tex2d.format}");
                sb.AppendLine($"{indent}Mipmap Count: {tex2d.mipmapCount}");
                sb.AppendLine($"{indent}Is Readable: {tex2d.isReadable}");
            }
            else if (tex is RenderTexture rt)
            {
                sb.AppendLine($"{indent}Format: {rt.format}");
                sb.AppendLine($"{indent}Depth: {rt.depth}");
                sb.AppendLine($"{indent}Anti-Aliasing: {rt.antiAliasing}");
            }
            else if (tex is Cubemap cube)
            {
                sb.AppendLine($"{indent}Format: {cube.format}");
                sb.AppendLine($"{indent}Mipmap Count: {cube.mipmapCount}");
            }

            // Importer settings
            var path = AssetDatabase.GetAssetPath(tex);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                sb.AppendLine($"\n{indent}Import Settings:");
                sb.AppendLine($"{indent}  Texture Type: {importer.textureType}");
                sb.AppendLine($"{indent}  sRGB: {importer.sRGBTexture}");
                sb.AppendLine($"{indent}  Alpha Source: {importer.alphaSource}");
                sb.AppendLine($"{indent}  Max Size: {importer.maxTextureSize}");
                sb.AppendLine($"{indent}  Compression: {importer.textureCompression}");
                sb.AppendLine($"{indent}  Generate Mipmaps: {importer.mipmapEnabled}");
                sb.AppendLine($"{indent}  Read/Write: {importer.isReadable}");
            }
        }

        // ─── Mesh Info ─────────────────────────────────────────────────

        private void WriteMeshInfo(StringBuilder sb, Mesh mesh, string indent)
        {
            sb.AppendLine($"{indent}Vertices: {mesh.vertexCount:N0}");
            sb.AppendLine($"{indent}Triangles: {mesh.triangles.Length / 3:N0}");
            sb.AppendLine($"{indent}Sub Meshes: {mesh.subMeshCount}");
            sb.AppendLine($"{indent}Bounds: center={Format(mesh.bounds.center)} size={Format(mesh.bounds.size)}");
            sb.AppendLine($"{indent}Is Readable: {mesh.isReadable}");

            // Vertex attributes
            var attrs = new List<string>();
            if (mesh.normals?.Length > 0) attrs.Add("normals");
            if (mesh.tangents?.Length > 0) attrs.Add("tangents");
            if (mesh.uv?.Length > 0) attrs.Add("uv");
            if (mesh.uv2?.Length > 0) attrs.Add("uv2");
            if (mesh.colors?.Length > 0) attrs.Add("colors");
            if (mesh.boneWeights?.Length > 0) attrs.Add("boneWeights");
            sb.AppendLine($"{indent}Attributes: {string.Join(", ", attrs)}");

            // Blend shapes
            if (mesh.blendShapeCount > 0)
            {
                sb.AppendLine($"{indent}Blend Shapes ({mesh.blendShapeCount}):");
                for (int i = 0; i < Mathf.Min(mesh.blendShapeCount, 20); i++)
                    sb.AppendLine($"{indent}  [{i}] {mesh.GetBlendShapeName(i)}");
            }

            // Sub mesh details
            if (mesh.subMeshCount > 1)
            {
                sb.AppendLine($"{indent}Sub Mesh Details:");
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    var desc = mesh.GetSubMesh(i);
                    sb.AppendLine($"{indent}  [{i}] topology={desc.topology} indices={desc.indexCount} verts={desc.vertexCount}");
                }
            }
        }

        // ─── Prefab Info ───────────────────────────────────────────────

        private void WritePrefabInfo(StringBuilder sb, GameObject prefab, int maxDepth, string indent)
        {
            sb.AppendLine($"{indent}Prefab Hierarchy:");
            WritePrefabHierarchy(sb, prefab.transform, indent + "  ", 0, maxDepth);

            // Count stats
            var allTransforms = prefab.GetComponentsInChildren<Transform>(true);
            var allRenderers = prefab.GetComponentsInChildren<Renderer>(true);
            var allColliders = prefab.GetComponentsInChildren<Collider>(true);
            sb.AppendLine($"\n{indent}Stats:");
            sb.AppendLine($"{indent}  Total Objects: {allTransforms.Length}");
            sb.AppendLine($"{indent}  Renderers: {allRenderers.Length}");
            sb.AppendLine($"{indent}  Colliders: {allColliders.Length}");

            // Component type summary
            var allComps = prefab.GetComponentsInChildren<Component>(true);
            var typeCounts = allComps.Where(c => c != null)
                .GroupBy(c => c.GetType().Name)
                .OrderByDescending(g => g.Count())
                .Take(15);
            sb.AppendLine($"{indent}  Component Types:");
            foreach (var g in typeCounts)
                sb.AppendLine($"{indent}    {g.Key}: {g.Count()}");
        }

        private void WritePrefabHierarchy(StringBuilder sb, Transform t, string indent, int depth, int maxDepth)
        {
            var comps = t.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform))
                .Select(c => c.GetType().Name);
            string compStr = comps.Any() ? $" [{string.Join(", ", comps)}]" : "";
            sb.AppendLine($"{indent}{t.name}{compStr}{(t.gameObject.activeSelf ? "" : " (inactive)")}");

            if (depth >= maxDepth && t.childCount > 0)
            {
                sb.AppendLine($"{indent}  ... ({t.childCount} children)");
                return;
            }

            for (int i = 0; i < t.childCount; i++)
                WritePrefabHierarchy(sb, t.GetChild(i), indent + "  ", depth + 1, maxDepth);
        }

        // ─── Animator Info ─────────────────────────────────────────────

        private void WriteAnimatorInfo(StringBuilder sb, UnityEngine.Object asset, string indent)
        {
            // Use SerializedObject for generic inspection since AnimatorController
            // might not be directly accessible
            WriteSerializedProperties(sb, asset, 3, null, indent);
        }

        // ─── Helpers ───────────────────────────────────────────────────

        private GameObject FindByPath(string path)
        {
            // Fast path
            var go = GameObject.Find(path);
            if (go != null) return go;

            // Recursive search for inactive objects
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
                    if (current.GetChild(j).name == parts[i])
                    {
                        child = current.GetChild(j);
                        break;
                    }
                }
                if (child == null) return null;
                current = child;
            }
            return current.gameObject;
        }

        private string Format(Vector2 v) => $"({v.x:F3}, {v.y:F3})";
        private string Format(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
        private string Format(Vector4 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3}, {v.w:F3})";
        private string FormatColor(Color c) => $"RGBA({c.r:F3}, {c.g:F3}, {c.b:F3}, {c.a:F3})";

        private string FormatTexture(Texture tex)
        {
            if (tex == null) return "None";
            string path = AssetDatabase.GetAssetPath(tex);
            return $"{tex.name} ({tex.width}x{tex.height}){(string.IsNullOrEmpty(path) ? "" : $" @ {path}")}";
        }
    }
}
