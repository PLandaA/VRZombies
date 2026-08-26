using System;
using System.Collections.Generic;
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
    /// Comprehensive project and scene information tool.
    /// Modes: scene_overview, project_settings, build_info
    /// </summary>
    public class ProjectInfoTool : IMcpTool
    {
        public string Name => "project_info";
        public string Description =>
            "Get project and scene information.\n" +
            "Modes:\n" +
            "- scene_overview: Scene stats (objects, lights, renderers, colliders, bounds)\n" +
            "- project_settings: PlayerSettings, QualitySettings, Physics, Graphics\n" +
            "- build_info: Build target, scenes, defines, packages\n" +
            "- all: Everything above";

        public bool IsDestructive => false;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""mode"": { ""type"": ""string"", ""description"": ""Mode: 'scene_overview', 'project_settings', 'build_info', 'all' (default: 'all')"" },
                ""category"": { ""type"": ""string"", ""description"": ""For project_settings: 'player', 'quality', 'physics', 'graphics', 'all' (default: 'all')"" }
            },
            ""required"": []
        }");

        public string Execute(JObject input)
        {
            string mode = input.Value<string>("mode") ?? "all";
            string category = input.Value<string>("category") ?? "all";

            try
            {
                var sb = new StringBuilder();

                if (mode == "all" || mode == "scene_overview")
                    WriteSceneOverview(sb);
                if (mode == "all" || mode == "project_settings")
                    WriteProjectSettings(sb, category);
                if (mode == "all" || mode == "build_info")
                    WriteBuildInfo(sb);

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        // ─── Scene Overview ────────────────────────────────────────────

        private void WriteSceneOverview(StringBuilder sb)
        {
            sb.AppendLine("═══ SCENE OVERVIEW ═══");

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                var scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;

                sb.AppendLine($"\n  Scene: {scene.name} ({scene.path})");
                sb.AppendLine($"  Is Active: {scene == SceneManager.GetActiveScene()}");
                sb.AppendLine($"  Is Dirty: {scene.isDirty}");

                var roots = scene.GetRootGameObjects();
                var allTransforms = new List<Transform>();
                var allRenderers = new List<Renderer>();
                var allColliders = new List<Collider>();
                var allLights = new List<Light>();
                var allCameras = new List<Camera>();
                var allAudioSources = new List<AudioSource>();
                var allCanvas = new List<Canvas>();
                var componentTypes = new Dictionary<string, int>();
                int inactiveCount = 0;
                int staticCount = 0;
                int missingScripts = 0;
                Bounds sceneBounds = new Bounds();
                bool boundsInit = false;

                foreach (var root in roots)
                {
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        allTransforms.Add(t);
                        if (!t.gameObject.activeInHierarchy) inactiveCount++;
                        if (t.gameObject.isStatic) staticCount++;

                        var comps = t.GetComponents<Component>();
                        foreach (var c in comps)
                        {
                            if (c == null) { missingScripts++; continue; }
                            string typeName = c.GetType().Name;
                            componentTypes[typeName] = componentTypes.GetValueOrDefault(typeName) + 1;
                        }
                    }

                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        allRenderers.Add(r);
                        if (r.enabled && r.gameObject.activeInHierarchy)
                        {
                            if (!boundsInit) { sceneBounds = r.bounds; boundsInit = true; }
                            else sceneBounds.Encapsulate(r.bounds);
                        }
                    }

                    allColliders.AddRange(root.GetComponentsInChildren<Collider>(true));
                    allLights.AddRange(root.GetComponentsInChildren<Light>(true));
                    allCameras.AddRange(root.GetComponentsInChildren<Camera>(true));
                    allAudioSources.AddRange(root.GetComponentsInChildren<AudioSource>(true));
                    allCanvas.AddRange(root.GetComponentsInChildren<Canvas>(true));
                }

                sb.AppendLine($"\n  ── Object Stats ──");
                sb.AppendLine($"  Total GameObjects: {allTransforms.Count}");
                sb.AppendLine($"  Root Objects: {roots.Length}");
                sb.AppendLine($"  Inactive: {inactiveCount}");
                sb.AppendLine($"  Static: {staticCount}");
                if (missingScripts > 0) sb.AppendLine($"  ⚠ Missing Scripts: {missingScripts}");

                sb.AppendLine($"\n  ── Rendering ──");
                sb.AppendLine($"  Renderers: {allRenderers.Count} ({allRenderers.Count(r => r.enabled && r.gameObject.activeInHierarchy)} active)");

                // Renderer type breakdown
                var rendererTypes = allRenderers.GroupBy(r => r.GetType().Name).OrderByDescending(g => g.Count());
                foreach (var g in rendererTypes)
                    sb.AppendLine($"    {g.Key}: {g.Count()}");

                // Triangle/vertex estimation from MeshFilters
                long totalVerts = 0, totalTris = 0;
                var meshFilters = allTransforms.Select(t => t.GetComponent<MeshFilter>()).Where(m => m != null && m.sharedMesh != null);
                foreach (var mf in meshFilters)
                {
                    totalVerts += mf.sharedMesh.vertexCount;
                    totalTris += mf.sharedMesh.triangles.Length / 3;
                }
                var skinnedRenderers = allRenderers.OfType<SkinnedMeshRenderer>().Where(s => s.sharedMesh != null);
                foreach (var smr in skinnedRenderers)
                {
                    totalVerts += smr.sharedMesh.vertexCount;
                    totalTris += smr.sharedMesh.triangles.Length / 3;
                }
                sb.AppendLine($"  Estimated Vertices: {totalVerts:N0}");
                sb.AppendLine($"  Estimated Triangles: {totalTris:N0}");

                // Unique materials
                var uniqueMats = allRenderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToList();
                sb.AppendLine($"  Unique Materials: {uniqueMats.Count}");
                var uniqueShaders = uniqueMats.Select(m => m.shader?.name).Where(s => s != null).Distinct().OrderBy(s => s);
                sb.AppendLine($"  Shaders in use:");
                foreach (var shader in uniqueShaders)
                    sb.AppendLine($"    - {shader}");

                if (boundsInit)
                {
                    sb.AppendLine($"\n  ── Scene Bounds ──");
                    sb.AppendLine($"  Center: ({sceneBounds.center.x:F1}, {sceneBounds.center.y:F1}, {sceneBounds.center.z:F1})");
                    sb.AppendLine($"  Size: ({sceneBounds.size.x:F1}, {sceneBounds.size.y:F1}, {sceneBounds.size.z:F1})");
                }

                sb.AppendLine($"\n  ── Lighting ──");
                sb.AppendLine($"  Lights: {allLights.Count}");
                foreach (var lightType in allLights.GroupBy(l => l.type))
                    sb.AppendLine($"    {lightType.Key}: {lightType.Count()}");
                sb.AppendLine($"  Lightmap Mode: {Lightmapping.giWorkflowMode}");
                sb.AppendLine($"  Ambient Mode: {RenderSettings.ambientMode}");
                sb.AppendLine($"  Ambient Color: {RenderSettings.ambientLight}");
                sb.AppendLine($"  Skybox: {(RenderSettings.skybox != null ? RenderSettings.skybox.name : "None")}");
                sb.AppendLine($"  Fog: {RenderSettings.fog}{(RenderSettings.fog ? $" (color={RenderSettings.fogColor}, density={RenderSettings.fogDensity:F4})" : "")}");

                sb.AppendLine($"\n  ── Physics ──");
                sb.AppendLine($"  Colliders: {allColliders.Count}");
                foreach (var collType in allColliders.GroupBy(c => c.GetType().Name).OrderByDescending(g => g.Count()))
                    sb.AppendLine($"    {collType.Key}: {collType.Count()}");
                var rigidbodies = allTransforms.Select(t => t.GetComponent<Rigidbody>()).Where(r => r != null).ToList();
                sb.AppendLine($"  Rigidbodies: {rigidbodies.Count}");

                sb.AppendLine($"\n  ── Other ──");
                sb.AppendLine($"  Cameras: {allCameras.Count}");
                sb.AppendLine($"  Audio Sources: {allAudioSources.Count}");
                sb.AppendLine($"  Canvases: {allCanvas.Count}");

                // Top component types
                sb.AppendLine($"\n  ── Component Breakdown (top 25) ──");
                foreach (var ct in componentTypes.OrderByDescending(kv => kv.Value).Take(25))
                    sb.AppendLine($"    {ct.Key}: {ct.Value}");
                if (componentTypes.Count > 25)
                    sb.AppendLine($"    ... +{componentTypes.Count - 25} more types");

                // Layers in use
                var layersInUse = allTransforms.Select(t => t.gameObject.layer).Distinct().OrderBy(l => l);
                sb.AppendLine($"\n  ── Layers in Use ──");
                foreach (var layer in layersInUse)
                    sb.AppendLine($"    [{layer}] {LayerMask.LayerToName(layer)}");

                // Tags in use
                var tagsInUse = allTransforms.Select(t => t.gameObject.tag).Where(t => t != "Untagged").Distinct().OrderBy(t => t);
                if (tagsInUse.Any())
                {
                    sb.AppendLine($"\n  ── Tags in Use ──");
                    foreach (var tag in tagsInUse)
                        sb.AppendLine($"    {tag}");
                }
            }
        }

        // ─── Project Settings ──────────────────────────────────────────

        private void WriteProjectSettings(StringBuilder sb, string category)
        {
            sb.AppendLine("\n═══ PROJECT SETTINGS ═══");

            if (category == "all" || category == "player")
            {
                sb.AppendLine("\n  ── Player Settings ──");
                sb.AppendLine($"  Product Name: {PlayerSettings.productName}");
                sb.AppendLine($"  Company Name: {PlayerSettings.companyName}");
                sb.AppendLine($"  Version: {PlayerSettings.bundleVersion}");

                // Android specific
                sb.AppendLine($"\n  ── Android ──");
                sb.AppendLine($"  Package Name: {PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android)}");
                sb.AppendLine($"  Min SDK: {PlayerSettings.Android.minSdkVersion}");
                sb.AppendLine($"  Target SDK: {PlayerSettings.Android.targetSdkVersion}");
                sb.AppendLine($"  Target Architectures: {PlayerSettings.Android.targetArchitectures}");
                sb.AppendLine($"  Install Location: {PlayerSettings.Android.preferredInstallLocation}");
                sb.AppendLine($"  Internet Access: {PlayerSettings.Android.forceInternetPermission}");
                sb.AppendLine($"  Write Permission: {PlayerSettings.Android.forceSDCardPermission}");

                // XR
                sb.AppendLine($"\n  ── Rendering ──");
                sb.AppendLine($"  Color Space: {PlayerSettings.colorSpace}");
                sb.AppendLine($"  GPU Skinning: {PlayerSettings.gpuSkinning}");
                sb.AppendLine($"  Graphics APIs (Android): {string.Join(", ", PlayerSettings.GetGraphicsAPIs(BuildTarget.Android))}");
                sb.AppendLine($"  Auto Graphics API: {PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android)}");
                sb.AppendLine($"  Multithreaded Rendering: {PlayerSettings.GetMobileMTRendering(BuildTargetGroup.Android)}");

                sb.AppendLine($"\n  ── Other ──");
                sb.AppendLine($"  Scripting Backend: {PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android)}");
                sb.AppendLine($"  API Compatibility: {PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Android)}");
                sb.AppendLine($"  Managed Stripping Level: {PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Android)}");
            }

            if (category == "all" || category == "quality")
            {
                sb.AppendLine("\n  ── Quality Settings ──");
                var names = QualitySettings.names;
                sb.AppendLine($"  Current Level: {names[QualitySettings.GetQualityLevel()]} ({QualitySettings.GetQualityLevel()})");
                sb.AppendLine($"  Levels: {string.Join(", ", names)}");
                sb.AppendLine($"  VSync: {QualitySettings.vSyncCount}");
                sb.AppendLine($"  Anti Aliasing: {QualitySettings.antiAliasing}x");
                sb.AppendLine($"  Shadow Quality: {QualitySettings.shadows}");
                sb.AppendLine($"  Shadow Resolution: {QualitySettings.shadowResolution}");
                sb.AppendLine($"  Shadow Distance: {QualitySettings.shadowDistance:F1}");
                sb.AppendLine($"  Texture Quality: {(QualitySettings.globalTextureMipmapLimit == 0 ? "Full Res" : $"1/{Mathf.Pow(2, QualitySettings.globalTextureMipmapLimit)}")}");
                sb.AppendLine($"  Aniso Filtering: {QualitySettings.anisotropicFiltering}");
                sb.AppendLine($"  LOD Bias: {QualitySettings.lodBias:F2}");
                sb.AppendLine($"  Max LOD Level: {QualitySettings.maximumLODLevel}");
                sb.AppendLine($"  Pixel Light Count: {QualitySettings.pixelLightCount}");
                sb.AppendLine($"  Realtime Reflection Probes: {QualitySettings.realtimeReflectionProbes}");
                sb.AppendLine($"  Billboards Face Camera: {QualitySettings.billboardsFaceCameraPosition}");
                sb.AppendLine($"  Skin Weights: {QualitySettings.skinWeights}");

                var rpAsset = QualitySettings.renderPipeline;
                sb.AppendLine($"  Render Pipeline Asset: {(rpAsset != null ? rpAsset.name : "None (Built-in)")}");
            }

            if (category == "all" || category == "physics")
            {
                sb.AppendLine("\n  ── Physics Settings ──");
                sb.AppendLine($"  Gravity: {Physics.gravity}");
                sb.AppendLine($"  Default Solver Iterations: {Physics.defaultSolverIterations}");
                sb.AppendLine($"  Default Solver Velocity Iterations: {Physics.defaultSolverVelocityIterations}");
                sb.AppendLine($"  Default Max Angular Speed: {Physics.defaultMaxAngularSpeed:F1}");
                sb.AppendLine($"  Bounce Threshold: {Physics.bounceThreshold:F2}");
                sb.AppendLine($"  Default Contact Offset: {Physics.defaultContactOffset:F4}");
                sb.AppendLine($"  Sleep Threshold: {Physics.sleepThreshold:F4}");
                sb.AppendLine($"  Auto Simulation: {Physics.simulationMode}");
                sb.AppendLine($"  Queries Hit Triggers: {Physics.queriesHitTriggers}");
                sb.AppendLine($"  Queries Hit Back Faces: {Physics.queriesHitBackfaces}");
            }

            if (category == "all" || category == "graphics")
            {
                sb.AppendLine("\n  ── Graphics Settings ──");
                var currentRP = GraphicsSettings.currentRenderPipeline;
                sb.AppendLine($"  Render Pipeline: {(currentRP != null ? currentRP.name : "Built-in")}");
                if (currentRP != null)
                    sb.AppendLine($"  RP Type: {currentRP.GetType().Name}");
                sb.AppendLine($"  Transparency Sort Mode: {GraphicsSettings.transparencySortMode}");
                sb.AppendLine($"  Lightmap Stretch: {GraphicsSettings.lightsUseLinearIntensity}");
                sb.AppendLine($"  Log Shader Compilation: {GraphicsSettings.logWhenShaderIsCompiled}");
            }
        }

        // ─── Build Info ────────────────────────────────────────────────

        private void WriteBuildInfo(StringBuilder sb)
        {
            sb.AppendLine("\n═══ BUILD INFO ═══");

            sb.AppendLine($"\n  ── Target ──");
            sb.AppendLine($"  Active Build Target: {EditorUserBuildSettings.activeBuildTarget}");
            sb.AppendLine($"  Target Group: {EditorUserBuildSettings.selectedBuildTargetGroup}");
            sb.AppendLine($"  Development Build: {EditorUserBuildSettings.development}");

            sb.AppendLine($"\n  ── Scenes in Build ({EditorBuildSettings.scenes.Length}) ──");
            for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
            {
                var scene = EditorBuildSettings.scenes[i];
                sb.AppendLine($"    [{i}] {scene.path}{(scene.enabled ? "" : " (disabled)")}");
            }

            // Scripting define symbols
            var defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            if (!string.IsNullOrEmpty(defines))
            {
                var defineList = defines.Split(';').OrderBy(d => d);
                sb.AppendLine($"\n  ── Scripting Defines ({defineList.Count()}) ──");
                foreach (var d in defineList)
                    sb.AppendLine($"    {d}");
            }

            // Installed packages (from manifest)
            sb.AppendLine($"\n  ── Packages ──");
            try
            {
                string manifestPath = "Packages/manifest.json";
                if (System.IO.File.Exists(manifestPath))
                {
                    var manifest = JObject.Parse(System.IO.File.ReadAllText(manifestPath));
                    var deps = manifest["dependencies"] as JObject;
                    if (deps != null)
                    {
                        var packages = deps.Properties().OrderBy(p => p.Name).ToList();
                        sb.AppendLine($"  Total: {packages.Count}");
                        foreach (var pkg in packages)
                            sb.AppendLine($"    {pkg.Name}: {pkg.Value}");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  Error reading manifest: {ex.Message}");
            }

            // Unity version and editor info
            sb.AppendLine($"\n  ── Editor ──");
            sb.AppendLine($"  Unity Version: {Application.unityVersion}");
            sb.AppendLine($"  Platform: {Application.platform}");
            sb.AppendLine($"  Data Path: {Application.dataPath}");
            sb.AppendLine($"  System Language: {Application.systemLanguage}");
        }
    }
}
