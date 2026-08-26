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
    /// v6.8: scene_design — spatial perception and surface-aware placement,
    /// the toolkit of a senior environment designer:
    ///
    /// PERCEPTION (understand the scene WITHOUT screenshots — token-cheap):
    ///   overview        compact spatial digest of a region (or whole scene)
    ///   raycast         physics ray with renderer-bounds fallback
    ///   bounds          world bounds of an object (+ top Y = placeable height)
    ///   query_region    objects inside a sphere, sorted by size
    ///
    /// ACTION (placement that lands where a human would put it):
    ///   place           prefab/object onto the surface under an XZ point
    ///   snap_to_ground  drop an existing object onto whatever is below it
    ///   scatter         N prefab copies inside a radius (spacing, yaw/scale jitter)
    ///   array           grid/line of prefab copies with fixed spacing
    ///
    /// All positions rounded to 2 decimals; every action registers Undo, so
    /// begin/end_undo_group can wrap a whole composition into one Ctrl+Z.
    /// Raycasts fall back to renderer-bounds intersection when physics
    /// colliders are missing (common on decorative meshes).
    /// </summary>
    public class SceneDesignTool : IMcpTool
    {
        public string Name => "scene_design";

        public string Description =>
            "Environment design: perceive the scene spatially (overview/raycast/bounds/query_region — no screenshots needed) " +
            "and place objects on real surfaces (place/snap_to_ground/scatter/array). Works with or without colliders.";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""enum"": [""overview"", ""raycast"", ""bounds"", ""query_region"", ""place"", ""snap_to_ground"", ""scatter"", ""array""], ""description"": ""Operation to perform"" },
                ""target"": { ""type"": ""string"", ""description"": ""GameObject path (bounds/snap_to_ground) or prefab asset path (place/scatter/array)"" },
                ""position"": { ""type"": ""string"", ""description"": ""[x,y,z] JSON array. For place: XZ used, Y is raycast start (optional). For overview/query_region: region center"" },
                ""direction"": { ""type"": ""string"", ""description"": ""[x,y,z] ray direction (raycast; default [0,-1,0])"" },
                ""radius"": { ""type"": ""number"", ""description"": ""Region radius (overview/query_region/scatter). Default 10"" },
                ""count"": { ""type"": ""integer"", ""description"": ""Instances for scatter (max 50) or array"" },
                ""spacing"": { ""type"": ""number"", ""description"": ""Min distance between scatter instances / array cell size. Default 1"" },
                ""columns"": { ""type"": ""integer"", ""description"": ""Array columns (default: single row)"" },
                ""align_to_normal"": { ""type"": ""boolean"", ""description"": ""Tilt placed objects to match surface normal (default false)"" },
                ""random_yaw"": { ""type"": ""boolean"", ""description"": ""Random Y rotation per instance (default false for place, true for scatter)"" },
                ""scale_jitter"": { ""type"": ""number"", ""description"": ""Scatter: uniform scale varies ±this fraction (e.g. 0.2 = ±20%). Default 0"" },
                ""parent"": { ""type"": ""string"", ""description"": ""Parent GameObject path; scatter/array auto-create a container under it"" },
                ""name"": { ""type"": ""string"", ""description"": ""Name for placed instance / container"" },
                ""seed"": { ""type"": ""integer"", ""description"": ""Random seed for reproducible scatter"" },
                ""max_results"": { ""type"": ""integer"", ""description"": ""Cap for overview/query_region output (default 20)"" },
                ""scene"": { ""type"": ""string"", ""description"": ""Scene name (fragment ok): filters perception; targets placement in multi-scene setups"" }
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
                    "overview"       => Overview(input),
                    "raycast"        => RaycastAction(input),
                    "bounds"         => BoundsAction(input),
                    "query_region"   => QueryRegion(input),
                    "place"          => Place(input),
                    "snap_to_ground" => SnapToGround(input),
                    "scatter"        => Scatter(input),
                    "array"          => ArrayPlace(input),
                    _ => $"ERROR: Unknown action '{action}'. Use overview, raycast, bounds, query_region, place, snap_to_ground, scatter, array."
                };
            }
            catch (Exception ex)
            {
                return $"ERROR: scene_design/{action} failed: {ex.Message}";
            }
        }

        // ─── Shared helpers ────────────────────────────────────────────

        private static string F(float v) => v.ToString("0.##");
        private static string V(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

        private static Vector3? ParseVec(JObject input, string key)
        {
            string raw = input.Value<string>(key);
            if (string.IsNullOrEmpty(raw)) return null;
            try
            {
                var arr = JArray.Parse(raw);
                return new Vector3((float)arr[0], (float)arr[1], (float)arr[2]);
            }
            catch { return null; }
        }

        private static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var go = GameObject.Find(path);
            if (go != null) return go;
            // Inactive objects: walk all transforms.
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (GetPath(t) == path || t.name == path) return t.gameObject;
            return null;
        }

        private static string GetPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null) { t = t.parent; sb.Insert(0, t.name + "/"); }
            return sb.ToString();
        }

        private static Bounds? RendererBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return null;
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        /// <summary>
        /// Physics raycast with renderer-bounds fallback. Returns hit info
        /// and whether it came from physics (real normal) or bounds (assumed).
        /// </summary>
        private static bool SmartRaycast(Vector3 origin, Vector3 dir, float maxDist,
            GameObject exclude, out Vector3 point, out Vector3 normal, out GameObject hitObj, out bool viaPhysics)
        {
            point = default; normal = Vector3.up; hitObj = null; viaPhysics = false;
            dir = dir.normalized;

            var hits = Physics.RaycastAll(origin, dir, maxDist);
            RaycastHit best = default; bool found = false; float bestDist = float.MaxValue;
            foreach (var h in hits)
            {
                if (exclude != null && h.collider.transform.IsChildOf(exclude.transform)) continue;
                if (h.distance < bestDist) { best = h; bestDist = h.distance; found = true; }
            }
            if (found)
            {
                point = best.point; normal = best.normal;
                hitObj = best.collider.gameObject; viaPhysics = true;
                return true;
            }

            // Fallback: nearest renderer-bounds intersection (no colliders needed).
            var ray = new Ray(origin, dir);
            float nearest = float.MaxValue;
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (exclude != null && r.transform.IsChildOf(exclude.transform)) continue;
                if (!r.bounds.IntersectRay(ray, out float d) || d > maxDist || d >= nearest) continue;
                nearest = d;
                hitObj = r.gameObject;
            }
            if (hitObj == null) return false;

            point = origin + dir * nearest;
            // Bounds give no true normal; for downward rays the top face is
            // the sane assumption — report the bounds top Y as the point.
            var hb = RendererBounds(hitObj);
            if (hb.HasValue && dir.y < -0.5f)
                point.y = hb.Value.max.y;
            normal = -dir;
            return true;
        }

        /// <summary>Move go so the BASE of its renderer bounds sits at point.</summary>
        private static void PlaceBaseAt(GameObject go, Vector3 point, Vector3 normal, bool alignToNormal)
        {
            if (alignToNormal)
                go.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal) * go.transform.rotation;

            var b = RendererBounds(go);
            float baseOffset = b.HasValue ? go.transform.position.y - b.Value.min.y : 0f;
            go.transform.position = new Vector3(point.x, point.y + baseOffset, point.z);
        }

        private static GameObject LoadPrefab(string assetPath, out string error)
        {
            error = null;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
                error = $"ERROR: Prefab not found at '{assetPath}'. Use prefab_browser(action:'list') to locate it.";
            return prefab;
        }

        private static GameObject Instantiate(GameObject prefab, Transform parent, string name)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (parent != null) instance.transform.SetParent(parent, true);
            if (!string.IsNullOrEmpty(name)) instance.name = name;
            Undo.RegisterCreatedObjectUndo(instance, $"LazyRay: Place {instance.name}");
            return instance;
        }

        // ─── Multi-scene helpers (v6.9) ────────────────────────────────

        private static Scene? ResolveScene(string fragment)
        {
            if (string.IsNullOrEmpty(fragment)) return null;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (sc.isLoaded && sc.name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return sc;
            }
            return null;
        }

        /// <summary>Move a ROOT object to the target scene (parented objects inherit their parent's scene).</summary>
        private static string MoveToScene(GameObject go, JObject input)
        {
            string frag = input.Value<string>("scene");
            if (string.IsNullOrEmpty(frag) || go.transform.parent != null) return "";
            var sc = ResolveScene(frag);
            if (!sc.HasValue) return $" (WARNING: scene '{frag}' not loaded — left in active scene '{SceneManager.GetActiveScene().name}')";
            SceneManager.MoveGameObjectToScene(go, sc.Value);
            return $" [scene: {sc.Value.name}]";
        }

        // ─── Perception ────────────────────────────────────────────────

        private string Overview(JObject input)
        {
            Vector3? center = ParseVec(input, "position");
            float radius = input.Value<float?>("radius") ?? (center.HasValue ? 10f : float.MaxValue);
            int max = Mathf.Clamp(input.Value<int?>("max_results") ?? 20, 1, 60);

            var sceneFilter = ResolveScene(input.Value<string>("scene"));
            var sceneIdx = new Dictionary<string, int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isLoaded) sceneIdx[SceneManager.GetSceneAt(i).name] = sceneIdx.Count + 1;
            bool multiScene = sceneIdx.Count > 1 && !sceneFilter.HasValue;

            var entries = new List<(string name, Bounds b, bool hasCollider, int children, string scene)>();
            foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (sceneFilter.HasValue && r.gameObject.scene != sceneFilter.Value) continue;
                // Report at the top-most "prop" level: the highest ancestor
                // that still has a renderer in its children — approximated by
                // the root-most parent whose name isn't a scene container.
                var t = r.transform;
                while (t.parent != null && t.parent.GetComponentInChildren<Renderer>() != null &&
                       t.parent.GetComponentsInChildren<Renderer>(false).Length <= 40)
                    t = t.parent;

                if (entries.Any(e => e.name == GetPath(t))) continue;
                var b = RendererBounds(t.gameObject);
                if (!b.HasValue) continue;
                if (center.HasValue && Vector3.Distance(b.Value.center, center.Value) > radius) continue;

                entries.Add((GetPath(t), b.Value,
                    t.GetComponentInChildren<Collider>() != null,
                    t.GetComponentsInChildren<Renderer>(false).Length,
                    t.gameObject.scene.name));
            }

            // Ground probe at region center.
            string groundLine = "";
            Vector3 probe = center ?? Vector3.zero;
            if (SmartRaycast(probe + Vector3.up * 100f, Vector3.down, 500f, null,
                out var gp, out _, out var gObj, out bool phys))
                groundLine = $"Ground at {V(probe)}: y={F(gp.y)} on '{gObj.name}'{(phys ? "" : " (bounds)")}\n";

            var sorted = entries.OrderByDescending(e => e.b.size.x * e.b.size.y * e.b.size.z).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"Scene overview{(center.HasValue ? $" @ {V(center.Value)} r={F(radius)}" : "")}{(sceneFilter.HasValue ? $" [scene: {sceneFilter.Value.name}]" : "")}: {entries.Count} object group(s){(entries.Count > max ? $", showing {max} largest" : "")}");
            if (multiScene)
                sb.AppendLine($"Scenes: {string.Join(", ", sceneIdx.Select(kv => $"«{kv.Value}»={kv.Key}"))}");
            sb.Append(groundLine);
            foreach (var (name, b, col, kids, scn) in sorted.Take(max))
                sb.AppendLine($"  {name} @ {V(b.center)} size {F(b.size.x)}x{F(b.size.y)}x{F(b.size.z)} top:{F(b.max.y)}{(col ? "" : " [no-collider]")}{(kids > 1 ? $" ({kids} parts)" : "")}{(multiScene ? $" «{sceneIdx[scn]}»" : "")}");
            if (entries.Count > max)
                sb.AppendLine($"  … +{entries.Count - max} smaller (raise max_results or narrow radius)");
            return sb.ToString();
        }

        private string QueryRegion(JObject input)
        {
            var center = ParseVec(input, "position");
            if (!center.HasValue) return "ERROR: 'position' [x,y,z] is required for query_region.";
            input["radius"] = input.Value<float?>("radius") ?? 5f;
            return Overview(input);
        }

        private string RaycastAction(JObject input)
        {
            var origin = ParseVec(input, "position");
            if (!origin.HasValue) return "ERROR: 'position' [x,y,z] is required for raycast.";
            Vector3 dir = ParseVec(input, "direction") ?? Vector3.down;

            // v6.9: report up to 3 layers so ceilings/roofs don't masquerade
            // as ground — the caller sees every surface the ray crosses.
            var all = Physics.RaycastAll(origin.Value, dir.normalized, 1000f)
                .OrderBy(h => h.distance).Take(3).ToList();
            if (all.Count > 1)
            {
                var sb = new StringBuilder($"{all.Count}+ layers from {V(origin.Value)}:");
                foreach (var h in all)
                    sb.Append($"\n  '{GetPath(h.collider.transform)}' at {V(h.point)} dist {F(h.distance)}");
                return sb.ToString();
            }

            if (!SmartRaycast(origin.Value, dir, 1000f, null, out var p, out var n, out var obj, out bool phys))
                return $"Raycast from {V(origin.Value)} dir {V(dir)}: NO HIT within 1000 units.";

            return $"Hit '{GetPath(obj.transform)}' at {V(p)} normal {V(n)} dist {F(Vector3.Distance(origin.Value, p))}" +
                   (phys ? "" : " [via renderer bounds — no collider]");
        }

        private string BoundsAction(JObject input)
        {
            var go = FindByPath(input.Value<string>("target"));
            if (go == null) return $"ERROR: GameObject '{input.Value<string>("target")}' not found.";
            var b = RendererBounds(go);
            if (!b.HasValue) return $"'{GetPath(go.transform)}' has no renderers (pivot at {V(go.transform.position)}).";
            var v = b.Value;
            return $"'{GetPath(go.transform)}': center {V(v.center)} size {F(v.size.x)}x{F(v.size.y)}x{F(v.size.z)}\n" +
                   $"  min {V(v.min)} max {V(v.max)} | top (placeable) y={F(v.max.y)} | pivot {V(go.transform.position)}";
        }

        // ─── Actions ───────────────────────────────────────────────────

        private string Place(JObject input)
        {
            string target = input.Value<string>("target");
            var pos = ParseVec(input, "position");
            if (string.IsNullOrEmpty(target) || !pos.HasValue)
                return "ERROR: place needs 'target' (prefab path) and 'position' [x,y,z] (Y = ray start; use a high Y to drop from above).";

            var prefab = LoadPrefab(target, out string err);
            if (prefab == null) return err;

            Transform parent = null;
            string parentPath = input.Value<string>("parent");
            if (!string.IsNullOrEmpty(parentPath))
            {
                var p = FindByPath(parentPath);
                if (p == null) return $"ERROR: parent '{parentPath}' not found.";
                parent = p.transform;
            }

            var instance = Instantiate(prefab, parent, input.Value<string>("name"));

            Vector3 rayStart = new Vector3(pos.Value.x, pos.Value.y == 0 ? 100f : pos.Value.y, pos.Value.z);
            bool align = input.Value<bool?>("align_to_normal") ?? false;
            if (input.Value<bool?>("random_yaw") ?? false)
                instance.transform.Rotate(0, UnityEngine.Random.Range(0f, 360f), 0);

            string sceneNote = MoveToScene(instance, input);

            if (SmartRaycast(rayStart, Vector3.down, 500f, instance, out var p2, out var n2, out var surf, out bool phys))
            {
                PlaceBaseAt(instance, p2, n2, align);
                return $"Placed '{instance.name}' on '{surf.name}' at {V(instance.transform.position)}{(phys ? "" : " [bounds fallback]")}{sceneNote}";
            }

            instance.transform.position = new Vector3(pos.Value.x, 0, pos.Value.z);
            return $"Placed '{instance.name}' at {V(instance.transform.position)} — WARNING: no surface found below, resting at y=0.";
        }

        private string SnapToGround(JObject input)
        {
            var go = FindByPath(input.Value<string>("target"));
            if (go == null) return $"ERROR: GameObject '{input.Value<string>("target")}' not found.";

            var b = RendererBounds(go);
            Vector3 start = (b?.center ?? go.transform.position) + Vector3.up * 0.1f;
            if (!SmartRaycast(start, Vector3.down, 500f, go, out var p, out var n, out var surf, out bool phys))
                return $"ERROR: nothing below '{go.name}' within 500 units.";

            Undo.RecordObject(go.transform, $"LazyRay: Snap {go.name}");
            PlaceBaseAt(go, p, n, input.Value<bool?>("align_to_normal") ?? false);
            return $"Snapped '{go.name}' onto '{surf.name}' at {V(go.transform.position)}{(phys ? "" : " [bounds fallback]")}";
        }

        private string Scatter(JObject input)
        {
            string target = input.Value<string>("target");
            var center = ParseVec(input, "position");
            if (string.IsNullOrEmpty(target) || !center.HasValue)
                return "ERROR: scatter needs 'target' (prefab path) and 'position' [x,y,z] (region center).";

            var prefab = LoadPrefab(target, out string err);
            if (prefab == null) return err;

            float radius = input.Value<float?>("radius") ?? 5f;
            int count = Mathf.Clamp(input.Value<int?>("count") ?? 10, 1, 50);
            float spacing = input.Value<float?>("spacing") ?? 1f;
            float jitter = Mathf.Clamp01(input.Value<float?>("scale_jitter") ?? 0f);
            bool randomYaw = input.Value<bool?>("random_yaw") ?? true;
            bool align = input.Value<bool?>("align_to_normal") ?? false;
            int? seed = input.Value<int?>("seed");

            var rng = seed.HasValue ? new System.Random(seed.Value) : new System.Random();

            var container = new GameObject(input.Value<string>("name") ?? $"{prefab.name}_Scatter");
            string parentPath = input.Value<string>("parent");
            if (!string.IsNullOrEmpty(parentPath))
            {
                var p = FindByPath(parentPath);
                if (p != null) container.transform.SetParent(p.transform, true);
            }
            Undo.RegisterCreatedObjectUndo(container, "LazyRay: Scatter");
            string sceneNote = MoveToScene(container, input);

            var placedXZ = new List<Vector2>();
            int placed = 0, noGround = 0;
            for (int i = 0; i < count; i++)
            {
                Vector2 xz = default; bool ok = false;
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    float ang = (float)(rng.NextDouble() * Math.PI * 2);
                    float dist = (float)Math.Sqrt(rng.NextDouble()) * radius;
                    xz = new Vector2(center.Value.x + Mathf.Cos(ang) * dist,
                                     center.Value.z + Mathf.Sin(ang) * dist);
                    if (placedXZ.All(q => Vector2.Distance(q, xz) >= spacing)) { ok = true; break; }
                }
                if (!ok) continue;

                var instance = Instantiate(prefab, container.transform, null);
                if (randomYaw) instance.transform.Rotate(0, (float)(rng.NextDouble() * 360), 0);
                if (jitter > 0)
                    instance.transform.localScale *= 1f + ((float)rng.NextDouble() * 2f - 1f) * jitter;

                Vector3 rayStart = new Vector3(xz.x, center.Value.y + 50f, xz.y);
                if (SmartRaycast(rayStart, Vector3.down, 200f, instance, out var p2, out var n2, out _, out _))
                {
                    PlaceBaseAt(instance, p2, n2, align);
                    placedXZ.Add(xz);
                    placed++;
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                    noGround++;
                }
            }

            return $"Scattered {placed}/{count} '{prefab.name}' in r={F(radius)} around {V(center.Value)} under '{GetPath(container.transform)}'" +
                   (noGround > 0 ? $" ({noGround} skipped: no surface)" : "") +
                   (placed < count - noGround ? " (some skipped: spacing too tight for radius)" : "") +
                   (seed.HasValue ? $" [seed {seed}]" : "") + sceneNote;
        }

        private string ArrayPlace(JObject input)
        {
            string target = input.Value<string>("target");
            var start = ParseVec(input, "position");
            if (string.IsNullOrEmpty(target) || !start.HasValue)
                return "ERROR: array needs 'target' (prefab path) and 'position' [x,y,z] (first cell).";

            var prefab = LoadPrefab(target, out string err);
            if (prefab == null) return err;

            int count = Mathf.Clamp(input.Value<int?>("count") ?? 5, 1, 100);
            int columns = Mathf.Max(1, input.Value<int?>("columns") ?? count);
            float spacing = input.Value<float?>("spacing") ?? 1.5f;
            bool align = input.Value<bool?>("align_to_normal") ?? false;

            var container = new GameObject(input.Value<string>("name") ?? $"{prefab.name}_Array");
            string parentPath = input.Value<string>("parent");
            if (!string.IsNullOrEmpty(parentPath))
            {
                var p = FindByPath(parentPath);
                if (p != null) container.transform.SetParent(p.transform, true);
            }
            Undo.RegisterCreatedObjectUndo(container, "LazyRay: Array");
            string sceneNote = MoveToScene(container, input);

            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                int row = i / columns, col = i % columns;
                var instance = Instantiate(prefab, container.transform, null);
                Vector3 rayStart = new Vector3(start.Value.x + col * spacing, start.Value.y + 50f, start.Value.z + row * spacing);
                if (SmartRaycast(rayStart, Vector3.down, 200f, instance, out var p2, out var n2, out _, out _))
                    PlaceBaseAt(instance, p2, n2, align);
                else
                    instance.transform.position = new Vector3(rayStart.x, start.Value.y, rayStart.z);
                placed++;
            }

            return $"Arrayed {placed} '{prefab.name}' ({Mathf.CeilToInt(count / (float)columns)}x{Mathf.Min(columns, count)}, spacing {F(spacing)}) from {V(start.Value)} under '{GetPath(container.transform)}'" + sceneNote;
        }
    }
}
