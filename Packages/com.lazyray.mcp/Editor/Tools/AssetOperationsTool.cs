using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LazyRay.Tools
{
    /// <summary>
    /// Asset operations: create materials, assign materials/textures to renderers,
    /// modify shader properties, duplicate assets.
    /// </summary>
    public class AssetOperationsTool : IMcpTool
    {
        public string Name => "asset_ops";
        public string Description =>
            "Asset operations: create, modify, and assign assets.\n" +
            "Actions:\n" +
            "- create_material: Create a new material (shader, save path)\n" +
            "- set_shader_property: Set a property on a material (color, float, texture, vector)\n" +
            "- assign_material: Assign material to a renderer on a GameObject\n" +
            "- assign_texture: Assign texture to a material's shader property\n" +
            "- duplicate_asset: Duplicate any asset to a new path\n" +
            "- list_shaders: List commonly available shaders";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""description"": ""Action: create_material, set_shader_property, assign_material, assign_texture, duplicate_asset, list_shaders"" },
                ""path"": { ""type"": ""string"", ""description"": ""Asset path for operations (e.g. 'Assets/Materials/New.mat')"" },
                ""shader"": { ""type"": ""string"", ""description"": ""Shader name (e.g. 'Universal Render Pipeline/Lit')"" },
                ""property_name"": { ""type"": ""string"", ""description"": ""Shader property name (e.g. '_BaseColor', '_Metallic')"" },
                ""property_type"": { ""type"": ""string"", ""description"": ""Property type: color, float, int, vector, texture"" },
                ""property_value"": { ""type"": ""string"", ""description"": ""Value: color as [r,g,b,a], float as number, vector as [x,y,z,w], texture as asset path"" },
                ""gameobject"": { ""type"": ""string"", ""description"": ""GameObject path for assign operations"" },
                ""material_index"": { ""type"": ""integer"", ""description"": ""Material slot index for assign_material (default 0)"" },
                ""destination"": { ""type"": ""string"", ""description"": ""Destination path for duplicate_asset"" }
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
                    "create_material" => DoCreateMaterial(input),
                    "set_shader_property" => DoSetShaderProperty(input),
                    "assign_material" => DoAssignMaterial(input),
                    "assign_texture" => DoAssignTexture(input),
                    "duplicate_asset" => DoDuplicateAsset(input),
                    "list_shaders" => DoListShaders(),
                    _ => $"ERROR: Unknown action '{action}'"
                };
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        // ─── Create Material ───────────────────────────────────────────

        private string DoCreateMaterial(JObject input)
        {
            string path = input.Value<string>("path");
            string shaderName = input.Value<string>("shader") ?? "Universal Render Pipeline/Lit";

            if (string.IsNullOrEmpty(path))
                return "ERROR: 'path' required (e.g. 'Assets/Materials/MyMaterial.mat')";

            if (!path.EndsWith(".mat"))
                path += ".mat";

            var shader = Shader.Find(shaderName);
            if (shader == null)
                return $"ERROR: Shader '{shaderName}' not found. Use list_shaders to see available options.";

            // Ensure directory exists
            string dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string[] parts = dir.Replace('\\', '/').Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            var material = new Material(shader);
            material.name = System.IO.Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();

            return $"✅ Created material '{material.name}'\n" +
                   $"  Path: {path}\n" +
                   $"  Shader: {shaderName}\n" +
                   $"  GUID: {AssetDatabase.AssetPathToGUID(path)}";
        }

        // ─── Set Shader Property ───────────────────────────────────────

        private string DoSetShaderProperty(JObject input)
        {
            string path = input.Value<string>("path");
            string propName = input.Value<string>("property_name");
            string propType = input.Value<string>("property_type");
            string propValue = input.Value<string>("property_value");

            if (string.IsNullOrEmpty(path)) return "ERROR: 'path' required (material asset path)";
            if (string.IsNullOrEmpty(propName)) return "ERROR: 'property_name' required (e.g. '_BaseColor')";
            if (string.IsNullOrEmpty(propType)) return "ERROR: 'property_type' required (color, float, int, vector, texture)";
            if (string.IsNullOrEmpty(propValue)) return "ERROR: 'property_value' required";

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) return $"ERROR: Material not found at '{path}'";

            Undo.RecordObject(mat, $"LazyRay: Set {propName}");

            switch (propType.ToLower())
            {
                case "color":
                    var colorArr = JArray.Parse(propValue);
                    var color = new Color(
                        colorArr[0].Value<float>(),
                        colorArr[1].Value<float>(),
                        colorArr[2].Value<float>(),
                        colorArr.Count > 3 ? colorArr[3].Value<float>() : 1f
                    );
                    mat.SetColor(propName, color);
                    return $"✅ Set {propName} = RGBA({color.r:F3}, {color.g:F3}, {color.b:F3}, {color.a:F3})";

                case "float":
                    float fVal = float.Parse(propValue);
                    mat.SetFloat(propName, fVal);
                    return $"✅ Set {propName} = {fVal:F4}";

                case "int":
                    int iVal = int.Parse(propValue);
                    mat.SetInteger(propName, iVal);
                    return $"✅ Set {propName} = {iVal}";

                case "vector":
                    var vecArr = JArray.Parse(propValue);
                    var vec = new Vector4(
                        vecArr[0].Value<float>(),
                        vecArr.Count > 1 ? vecArr[1].Value<float>() : 0,
                        vecArr.Count > 2 ? vecArr[2].Value<float>() : 0,
                        vecArr.Count > 3 ? vecArr[3].Value<float>() : 0
                    );
                    mat.SetVector(propName, vec);
                    return $"✅ Set {propName} = ({vec.x:F3}, {vec.y:F3}, {vec.z:F3}, {vec.w:F3})";

                case "texture":
                    var tex = AssetDatabase.LoadAssetAtPath<Texture>(propValue);
                    if (tex == null) return $"ERROR: Texture not found at '{propValue}'";
                    mat.SetTexture(propName, tex);
                    return $"✅ Set {propName} = {tex.name} ({tex.width}x{tex.height}) @ {propValue}";

                default:
                    return $"ERROR: Unknown property_type '{propType}'. Use: color, float, int, vector, texture";
            }
        }

        // ─── Assign Material ───────────────────────────────────────────

        private string DoAssignMaterial(JObject input)
        {
            string matPath = input.Value<string>("path");
            string goPath = input.Value<string>("gameobject");
            int matIndex = input.Value<int?>("material_index") ?? 0;

            if (string.IsNullOrEmpty(matPath)) return "ERROR: 'path' required (material asset path)";
            if (string.IsNullOrEmpty(goPath)) return "ERROR: 'gameobject' required (GameObject path)";

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) return $"ERROR: Material not found at '{matPath}'";

            var go = HierarchyUtils.FindByPath(goPath);
            if (go == null) return $"ERROR: GameObject '{goPath}' not found";

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) return $"ERROR: No Renderer on '{goPath}'";

            Undo.RecordObject(renderer, $"LazyRay: Assign material to {go.name}");

            var mats = renderer.sharedMaterials;
            if (matIndex < 0 || matIndex >= mats.Length)
            {
                // Expand array if needed
                var newMats = new Material[matIndex + 1];
                for (int i = 0; i < mats.Length; i++) newMats[i] = mats[i];
                newMats[matIndex] = mat;
                renderer.sharedMaterials = newMats;
            }
            else
            {
                mats[matIndex] = mat;
                renderer.sharedMaterials = mats;
            }

            EditorUtility.SetDirty(renderer);
            return $"✅ Assigned '{mat.name}' to '{go.name}' slot [{matIndex}]";
        }

        // ─── Assign Texture ────────────────────────────────────────────

        private string DoAssignTexture(JObject input)
        {
            string matPath = input.Value<string>("path");
            string propName = input.Value<string>("property_name") ?? "_BaseMap";
            string texPath = input.Value<string>("property_value");

            if (string.IsNullOrEmpty(matPath)) return "ERROR: 'path' required (material asset path)";
            if (string.IsNullOrEmpty(texPath)) return "ERROR: 'property_value' required (texture asset path)";

            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) return $"ERROR: Material not found at '{matPath}'";

            var tex = AssetDatabase.LoadAssetAtPath<Texture>(texPath);
            if (tex == null) return $"ERROR: Texture not found at '{texPath}'";

            Undo.RecordObject(mat, $"LazyRay: Assign texture {tex.name}");
            mat.SetTexture(propName, tex);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();

            return $"✅ Assigned texture '{tex.name}' ({tex.width}x{tex.height}) to '{mat.name}'.{propName}";
        }

        // ─── Duplicate Asset ───────────────────────────────────────────

        private string DoDuplicateAsset(JObject input)
        {
            string src = input.Value<string>("path");
            string dst = input.Value<string>("destination");

            if (string.IsNullOrEmpty(src)) return "ERROR: 'path' required (source asset)";
            if (string.IsNullOrEmpty(dst)) return "ERROR: 'destination' required (new asset path)";

            if (!AssetDatabase.CopyAsset(src, dst))
                return $"ERROR: Failed to copy '{src}' → '{dst}'";

            AssetDatabase.Refresh();
            return $"✅ Duplicated '{src}' → '{dst}'\n" +
                   $"  GUID: {AssetDatabase.AssetPathToGUID(dst)}";
        }

        // ─── List Shaders ──────────────────────────────────────────────

        private string DoListShaders()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ Common Shaders ═══");

            string[] commonShaders = {
                "Universal Render Pipeline/Lit",
                "Universal Render Pipeline/Simple Lit",
                "Universal Render Pipeline/Unlit",
                "Universal Render Pipeline/Complex Lit",
                "Universal Render Pipeline/Baked Lit",
                "Universal Render Pipeline/Particles/Lit",
                "Universal Render Pipeline/Particles/Simple Lit",
                "Universal Render Pipeline/Particles/Unlit",
                "Universal Render Pipeline/Terrain/Lit",
                "Shader Graphs/",
                "Standard",
                "Standard (Specular setup)",
                "Unlit/Color",
                "Unlit/Texture",
                "Unlit/Transparent",
                "Sprites/Default",
                "UI/Default",
            };

            sb.AppendLine("\n  ── URP Shaders ──");
            foreach (var name in commonShaders)
            {
                if (name.EndsWith("/"))
                {
                    sb.AppendLine($"\n  ── {name} (project-specific) ──");
                    continue;
                }
                var shader = Shader.Find(name);
                string status = shader != null ? "✓" : "✗";
                sb.AppendLine($"  {status} {name}");
                if (shader != null)
                {
                    int propCount = shader.GetPropertyCount();
                    sb.AppendLine($"      Properties: {propCount}");
                }
            }

            // List project shader graphs
            var shaderGuids = AssetDatabase.FindAssets("t:Shader", new[] { "Assets" });
            if (shaderGuids.Length > 0)
            {
                sb.AppendLine($"\n  ── Project Shaders ({shaderGuids.Length}) ──");
                foreach (var guid in shaderGuids.Take(20))
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
                    if (shader != null)
                        sb.AppendLine($"  {shader.name} @ {assetPath}");
                }
                if (shaderGuids.Length > 20)
                    sb.AppendLine($"  ... +{shaderGuids.Length - 20} more");
            }

            return sb.ToString();
        }
    }
}
