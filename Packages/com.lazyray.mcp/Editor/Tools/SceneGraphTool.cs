using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using LazyRay.Core;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    /// <summary>
    /// v7.3: design-flow X-ray. Parses .unity files straight from disk
    /// (scenes are never opened), reconstructs GameObjects + components +
    /// wiring, and reports it in three lenses:
    ///   summary — per-scene inventory and top systems
    ///   graph   — per-GameObject outgoing references (fields → targets)
    ///   events  — GameEvent-centric map: which components across which
    ///             scenes reference each GameEvent SO (the real flow here)
    /// export:true additionally writes the full graph as JSON to
    /// Assets/LazyRayData/SceneGraph.json for later visualization.
    /// </summary>
    public class SceneGraphTool : IMcpTool
    {
        const int DEFAULT_EDGE_CAP = 40;    // per scene, graph mode
        const int VERBOSE_EDGE_CAP = 400;
        const int DEFAULT_EVENT_CAP = 30;
        const int VERBOSE_EVENT_CAP = 300;
        const int TOP_SYSTEMS = 8;

        public string Name => "scene_graph";
        public bool IsDestructive => false;
        public bool IsThreadSafe => false; // AssetDatabase.GUIDToAssetPath = main thread

        public string Description =>
            "Design-flow X-ray: parses ALL .unity scenes from disk (never opens them) and reconstructs GameObjects, " +
            "components and their wiring. mode 'summary' = per-scene inventory + top systems; 'graph' = per-GO outgoing " +
            "references (field → target GO/asset); 'events' = GameEvent-centric map across scenes (who references each " +
            "event SO — the design flow in this architecture). Use scene:'fragment' to scope big projects. " +
            "export:true writes the full graph JSON to Assets/LazyRayData/SceneGraph.json for visualization.";

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""mode"": { ""type"": ""string"", ""description"": ""'summary' (default) | 'graph' | 'events'"" },
                ""scene"": { ""type"": ""string"", ""description"": ""Scene path fragment filter (recommended on big projects)"" },
                ""filter"": { ""type"": ""string"", ""description"": ""Component/system name fragment — only matching scripts contribute edges"" },
                ""export"": { ""type"": ""boolean"", ""description"": ""Also write full JSON graph to Assets/LazyRayData/SceneGraph.json (default false)"" },
                ""verbose"": { ""type"": ""boolean"", ""description"": ""Raise output caps (default false)"" }
            }
        }");

        // guid → display info cache (script names, asset names/types)
        readonly Dictionary<string, string> _scriptNameCache = new Dictionary<string, string>();
        readonly Dictionary<string, string> _assetLabelCache = new Dictionary<string, string>();

        public string Execute(JObject input)
        {
            string mode = (input.Value<string>("mode") ?? "summary").ToLowerInvariant();
            if (mode != "summary" && mode != "graph" && mode != "events")
                return "ERROR: mode must be 'summary', 'graph' or 'events'.";
            string sceneFilter = input.Value<string>("scene");
            string sysFilter = input.Value<string>("filter");
            bool export = input.Value<bool?>("export") ?? false;
            bool verbose = input.Value<bool?>("verbose") ?? false;

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            var scenes = SceneGraph.FindScenes(projectRoot, sceneFilter);
            if (scenes.Count == 0)
                return "No .unity scenes matched" + (string.IsNullOrEmpty(sceneFilter) ? "." : " filter '" + sceneFilter + "'.");

            _scriptNameCache.Clear();
            _assetLabelCache.Clear();

            var parsed = new List<SceneGraph.SceneData>();
            foreach (string rel in scenes)
                parsed.Add(SceneGraph.Parse(Path.Combine(projectRoot, rel)));

            var sb = new StringBuilder();
            sb.AppendLine("scene_graph " + mode + " — " + scenes.Count + " scene(s)" +
                          (string.IsNullOrEmpty(sysFilter) ? "" : ", system filter '" + sysFilter + "'"));

            if (mode == "summary") BuildSummary(sb, parsed, projectRoot);
            else if (mode == "graph") BuildGraph(sb, parsed, projectRoot, sysFilter, verbose ? VERBOSE_EDGE_CAP : DEFAULT_EDGE_CAP);
            else BuildEvents(sb, parsed, projectRoot, sysFilter, verbose ? VERBOSE_EVENT_CAP : DEFAULT_EVENT_CAP);

            if (export)
            {
                string outPath = ExportJson(parsed, projectRoot);
                sb.AppendLine();
                sb.AppendLine("Full graph exported: " + outPath + " (visualize it from there — FlowDashboard covers the runtime view, this is the static one).");
            }
            return sb.ToString();
        }

        // ─── summary ───────────────────────────────────────────────────

        void BuildSummary(StringBuilder sb, List<SceneGraph.SceneData> parsed, string projectRoot)
        {
            foreach (var s in parsed)
            {
                string rel = s.Path.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
                sb.AppendLine();
                sb.AppendLine("━ " + rel);
                sb.AppendLine("  GameObjects: " + s.GameObjectCount +
                              " | components: " + s.ComponentCount +
                              " (MonoBehaviour: " + s.MonoBehaviourCount + ")" +
                              " | prefab instances: " + s.PrefabInstanceCount);

                // Top custom systems by script usage.
                var counts = new Dictionary<string, int>();
                foreach (var d in s.Docs.Values)
                {
                    if (d.ClassId != 114 || d.ScriptGuid == null) continue;
                    string name = ScriptName(d.ScriptGuid);
                    if (name == null) continue;
                    int c; counts.TryGetValue(name, out c); counts[name] = c + 1;
                }
                if (counts.Count > 0)
                {
                    var top = counts.OrderByDescending(kv => kv.Value).Take(TOP_SYSTEMS)
                                    .Select(kv => kv.Key + " ×" + kv.Value);
                    sb.AppendLine("  top systems: " + string.Join(", ", top.ToArray()));
                }
                int wires = s.Docs.Values.Sum(d => d.Edges.Count);
                sb.AppendLine("  wiring: " + wires + " reference(s) — use mode:'graph' or mode:'events' to see them");
            }
        }

        // ─── graph ─────────────────────────────────────────────────────

        void BuildGraph(StringBuilder sb, List<SceneGraph.SceneData> parsed, string projectRoot, string sysFilter, int cap)
        {
            foreach (var s in parsed)
            {
                string rel = s.Path.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
                sb.AppendLine();
                sb.AppendLine("━ " + rel);

                int shown = 0, total = 0;
                // group edges by owning GO for readability
                var byOwner = new Dictionary<string, List<string>>();
                foreach (var d in s.Docs.Values)
                {
                    if (d.Edges.Count == 0) continue;
                    string comp = ComponentLabel(d);
                    if (!string.IsNullOrEmpty(sysFilter) &&
                        comp.IndexOf(sysFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string owner = SceneGraph.OwnerName(s, d);
                    foreach (var e in d.Edges)
                    {
                        total++;
                        string target = TargetLabel(s, e);
                        if (target == null) continue;
                        List<string> lines;
                        if (!byOwner.TryGetValue(owner, out lines)) { lines = new List<string>(); byOwner[owner] = lines; }
                        lines.Add("    ." + e.Field + " → " + target + "   (" + comp + ")");
                    }
                }

                foreach (var kv in byOwner.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (shown >= cap) break;
                    sb.AppendLine("  [GO] " + kv.Key);
                    foreach (string line in kv.Value)
                    {
                        if (shown++ >= cap) break;
                        sb.AppendLine(line);
                    }
                }
                if (total > shown)
                    sb.AppendLine("  … " + (total - shown) + " more edge(s) — verbose:true, or narrow with filter/scene");
                if (total == 0)
                    sb.AppendLine("  (no design wiring found" + (string.IsNullOrEmpty(sysFilter) ? "" : " for that filter") + ")");
            }
        }

        // ─── events ────────────────────────────────────────────────────

        void BuildEvents(StringBuilder sb, List<SceneGraph.SceneData> parsed, string projectRoot, string sysFilter, int cap)
        {
            // eventGuid → list of "scene / GO.Component  (field)"
            var byEvent = new Dictionary<string, List<string>>();
            var eventLabel = new Dictionary<string, string>();

            foreach (var s in parsed)
            {
                string sceneName = Path.GetFileNameWithoutExtension(s.Path);
                foreach (var d in s.Docs.Values)
                {
                    if (d.Edges.Count == 0) continue;
                    string comp = ComponentLabel(d);
                    if (!string.IsNullOrEmpty(sysFilter) &&
                        comp.IndexOf(sysFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    foreach (var e in d.Edges)
                    {
                        if (e.TargetGuid == null) continue;
                        string label = AssetLabel(e.TargetGuid);
                        if (label == null || !label.StartsWith("[SO:GameEvent]", StringComparison.Ordinal)) continue;

                        List<string> refs;
                        if (!byEvent.TryGetValue(e.TargetGuid, out refs)) { refs = new List<string>(); byEvent[e.TargetGuid] = refs; }
                        refs.Add("    " + sceneName + " / " + SceneGraph.OwnerName(s, d) + "." + comp + "   (field: " + e.Field + ")");
                        eventLabel[e.TargetGuid] = label;
                    }
                }
            }

            if (byEvent.Count == 0)
            {
                sb.AppendLine("No GameEvent references found in the matched scenes.");
                return;
            }

            sb.AppendLine("GameEvent wiring — " + byEvent.Count + " event(s) referenced:");
            int shown = 0;
            foreach (var kv in byEvent.OrderByDescending(k => k.Value.Count))
            {
                if (shown++ >= cap) { sb.AppendLine("… more events — verbose:true"); break; }
                sb.AppendLine();
                sb.AppendLine("● " + eventLabel[kv.Key].Substring(15) + "  — " + kv.Value.Count + " reference(s)");
                foreach (string r in kv.Value) sb.AppendLine(r);
            }
        }

        // ─── labels & resolution ───────────────────────────────────────

        string ScriptName(string guid)
        {
            string cached;
            if (_scriptNameCache.TryGetValue(guid, out cached)) return cached;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            string name = string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
            _scriptNameCache[guid] = name;
            return name;
        }

        string ComponentLabel(SceneGraph.Doc d)
        {
            if (d.ClassId == 114 && d.ScriptGuid != null)
                return ScriptName(d.ScriptGuid) ?? "MonoBehaviour(?)";
            return SceneGraph.BuiltinName(d.ClassId);
        }

        string TargetLabel(SceneGraph.SceneData s, SceneGraph.Edge e)
        {
            if (e.TargetGuid != null) return AssetLabel(e.TargetGuid);
            SceneGraph.Doc t;
            if (!s.Docs.TryGetValue(e.TargetFileId, out t)) return "(external/missing #" + e.TargetFileId + ")";
            return "[GO] " + SceneGraph.OwnerName(s, t) + (t.ClassId == 1 ? "" : " (" + ComponentLabel(t) + ")");
        }

        string AssetLabel(string guid)
        {
            string cached;
            if (_assetLabelCache.TryGetValue(guid, out cached)) return cached;

            string path = AssetDatabase.GUIDToAssetPath(guid);
            string label;
            if (string.IsNullOrEmpty(path)) label = "[asset ?" + guid.Substring(0, 8) + "]";
            else if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                string scriptGuid = SceneGraph.AssetScriptGuid(Path.Combine(projectRoot, path));
                string type = scriptGuid != null ? (ScriptName(scriptGuid) ?? "SO") : "SO";
                label = "[SO:" + type + "] " + Path.GetFileNameWithoutExtension(path);
            }
            else if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                label = "[prefab] " + Path.GetFileNameWithoutExtension(path);
            else
                label = "[asset] " + Path.GetFileName(path);

            _assetLabelCache[guid] = label;
            return label;
        }

        // ─── export ────────────────────────────────────────────────────

        string ExportJson(List<SceneGraph.SceneData> parsed, string projectRoot)
        {
            var root = new JObject();
            root["generated"] = DateTime.UtcNow.ToString("o");
            var scenesArr = new JArray();

            foreach (var s in parsed)
            {
                string rel = s.Path.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
                var so = new JObject();
                so["scene"] = rel;
                var nodes = new JArray();
                var edges = new JArray();

                foreach (var d in s.Docs.Values)
                {
                    if (d.ClassId == 1 || d.ClassId == 1001)
                    {
                        var n = new JObject();
                        n["id"] = d.FileId;
                        n["name"] = d.Name ?? "";
                        n["kind"] = d.ClassId == 1 ? "GameObject" : "PrefabInstance";
                        nodes.Add(n);
                    }
                    string comp = ComponentLabel(d);
                    foreach (var e in d.Edges)
                    {
                        var j = new JObject();
                        j["fromGO"] = SceneGraph.OwnerName(s, d);
                        j["fromComponent"] = comp;
                        j["field"] = e.Field;
                        if (e.TargetGuid != null) j["toAsset"] = AssetLabel(e.TargetGuid);
                        else j["toFileId"] = e.TargetFileId;
                        var tl = TargetLabel(s, e);
                        if (tl != null) j["toLabel"] = tl;
                        edges.Add(j);
                    }
                }
                so["nodes"] = nodes;
                so["edges"] = edges;
                scenesArr.Add(so);
            }
            root["scenes"] = scenesArr;

            string dir = Path.Combine(projectRoot, "Assets/LazyRayData");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "SceneGraph.json");
            File.WriteAllText(outPath, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return "Assets/LazyRayData/SceneGraph.json";
        }
    }
}
