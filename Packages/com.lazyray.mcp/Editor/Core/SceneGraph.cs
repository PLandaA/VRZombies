using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LazyRay.Core
{
    /// <summary>
    /// v7.3: static scene-graph extraction by parsing .unity YAML directly
    /// from disk — no scene is ever opened, nothing in the editor is
    /// perturbed. Captures GameObjects, their components, and the wiring:
    /// intra-scene references ({fileID}) and asset references ({guid}),
    /// with GameEvent ScriptableObjects as first-class citizens because in
    /// this architecture they ARE the design flow.
    ///
    /// Unity's scene YAML is machine-generated and extremely regular, so a
    /// line-based parse is deliberate: no YAML library, no surprises.
    /// </summary>
    public static class SceneGraph
    {
        // ─── model ─────────────────────────────────────────────────────

        public sealed class Doc
        {
            public int ClassId;
            public long FileId;
            public bool Stripped;          // prefab-internal reference stub
            public string Name;            // GameObjects / prefab instances
            public long GameObjectRef;     // components -> owning GO
            public long ParentRef;         // transforms -> father transform
            public string ScriptGuid;      // MonoBehaviours
            public string SourcePrefabGuid;// PrefabInstance
            public List<Edge> Edges = new List<Edge>();
        }

        public sealed class Edge
        {
            public string Field;           // serialized field name
            public long TargetFileId;      // intra-scene target (0 if asset)
            public string TargetGuid;      // asset target (null if intra)
        }

        public sealed class SceneData
        {
            public string Path;
            public Dictionary<long, Doc> Docs = new Dictionary<long, Doc>();
            public int GameObjectCount;
            public int ComponentCount;
            public int MonoBehaviourCount;
            public int PrefabInstanceCount;
        }

        // Built-in class ids worth naming (subset; unknown ids fall back to u!<id>).
        static readonly Dictionary<int, string> BuiltinNames = new Dictionary<int, string>
        {
            { 1, "GameObject" }, { 4, "Transform" }, { 20, "Camera" }, { 23, "MeshRenderer" },
            { 33, "MeshFilter" }, { 54, "Rigidbody" }, { 64, "MeshCollider" }, { 65, "BoxCollider" },
            { 68, "EdgeCollider2D" }, { 82, "AudioSource" }, { 95, "Animator" }, { 108, "Light" },
            { 111, "Animation" }, { 114, "MonoBehaviour" }, { 135, "SphereCollider" },
            { 136, "CapsuleCollider" }, { 137, "SkinnedMeshRenderer" }, { 198, "ParticleSystem" },
            { 212, "SpriteRenderer" }, { 222, "CanvasRenderer" }, { 223, "Canvas" },
            { 224, "RectTransform" }, { 225, "CanvasGroup" }, { 1001, "PrefabInstance" },
        };

        public static string BuiltinName(int classId)
        {
            string n;
            return BuiltinNames.TryGetValue(classId, out n) ? n : "u!" + classId;
        }

        // Internal Unity fields that are structure, not design wiring.
        static readonly HashSet<string> IgnoredRefFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_GameObject", "m_Script", "m_Father", "m_Children", "m_PrefabInstance",
            "m_CorrespondingSourceObject", "m_PrefabAsset", "m_SourcePrefab", "component",
            "m_Icon", "m_Mesh", "m_Materials", "m_Material", "m_StaticBatchRoot",
            "m_ProbeAnchor", "m_LightProbeVolumeOverride", "m_Sprite", "m_TargetTexture",
            "m_Controller", "m_Avatar", "m_AudioClip", "m_OutputAudioMixerGroup",
        };

        static readonly Regex DocHeader = new Regex(
            @"^--- !u!(\d+) &(-?\d+)( stripped)?\s*$", RegexOptions.Compiled);
        static readonly Regex RefLine = new Regex(
            @"^(\s+)(?:(\w[\w\d_]*):|-)\s*\{fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-fA-F]{32}))?", RegexOptions.Compiled);
        static readonly Regex PlainField = new Regex(
            @"^(\s+)(\w[\w\d_]*):\s*$", RegexOptions.Compiled);

        // ─── parse ─────────────────────────────────────────────────────

        public static SceneData Parse(string scenePath)
        {
            var data = new SceneData();
            data.Path = scenePath;

            Doc cur = null;
            string lastArrayField = null;   // field name owning "- {fileID:}" items
            bool inModifications = false;   // PrefabInstance m_Modifications block
            string pendingPropertyPath = null;

            foreach (string line in File.ReadLines(scenePath))
            {
                var h = DocHeader.Match(line);
                if (h.Success)
                {
                    cur = new Doc();
                    cur.ClassId = int.Parse(h.Groups[1].Value);
                    cur.FileId = long.Parse(h.Groups[2].Value);
                    cur.Stripped = h.Groups[3].Success;
                    data.Docs[cur.FileId] = cur;
                    lastArrayField = null;
                    inModifications = false;
                    pendingPropertyPath = null;

                    if (cur.ClassId == 1) data.GameObjectCount++;
                    else if (cur.ClassId == 1001) data.PrefabInstanceCount++;
                    else { data.ComponentCount++; if (cur.ClassId == 114) data.MonoBehaviourCount++; }
                    continue;
                }
                if (cur == null) continue;

                // Names: GameObject m_Name, and PrefabInstance name override.
                if (cur.ClassId == 1 && line.StartsWith("  m_Name: ", StringComparison.Ordinal))
                {
                    cur.Name = line.Substring(10).Trim();
                    continue;
                }
                if (cur.ClassId == 1001)
                {
                    if (line.Contains("m_Modifications:")) { inModifications = true; continue; }
                    if (inModifications)
                    {
                        string t = line.Trim();
                        if (t.StartsWith("propertyPath: ", StringComparison.Ordinal))
                            pendingPropertyPath = t.Substring(14).Trim();
                        else if (t.StartsWith("value: ", StringComparison.Ordinal) && pendingPropertyPath == "m_Name")
                        {
                            cur.Name = t.Substring(7).Trim();
                            pendingPropertyPath = null;
                        }
                    }
                }

                // Track a bare "fieldName:" line so following "- {fileID:}" array
                // items get attributed to it.
                var pf = PlainField.Match(line);
                if (pf.Success) { lastArrayField = pf.Groups[2].Value; }

                var r = RefLine.Match(line);
                if (!r.Success) continue;

                string field = r.Groups[2].Success ? r.Groups[2].Value : lastArrayField;
                long fid = long.Parse(r.Groups[3].Value);
                string guid = r.Groups[4].Success ? r.Groups[4].Value.ToLowerInvariant() : null;

                // Structure fields captured into the model, not as edges.
                if (field == "m_GameObject") { cur.GameObjectRef = fid; continue; }
                if (field == "m_Father") { cur.ParentRef = fid; continue; }
                if (field == "m_Script") { cur.ScriptGuid = guid; continue; }
                if (field == "m_SourcePrefab") { cur.SourcePrefabGuid = guid; continue; }

                if (field == null || IgnoredRefFields.Contains(field)) continue;
                if (fid == 0 && guid == null) continue; // null reference

                var e = new Edge();
                e.Field = field;
                e.TargetFileId = guid == null ? fid : 0;
                e.TargetGuid = guid;
                cur.Edges.Add(e);
            }
            return data;
        }

        // ─── helpers over a parsed scene ───────────────────────────────

        /// <summary>Owning GameObject name for any doc (component or GO).</summary>
        public static string OwnerName(SceneData s, Doc d)
        {
            if (d.ClassId == 1) return d.Name ?? "(GameObject)";
            if (d.ClassId == 1001) return (d.Name ?? "(PrefabInstance)") + " [prefab]";
            Doc go;
            if (d.GameObjectRef != 0 && s.Docs.TryGetValue(d.GameObjectRef, out go))
                return go.Name ?? "(GameObject)";
            if (d.Stripped) return "(inside prefab)";
            return "(?)";
        }

        /// <summary>All .unity files under Assets, optionally filtered by fragment.</summary>
        public static List<string> FindScenes(string projectRoot, string filter)
        {
            var list = new List<string>();
            string assets = Path.Combine(projectRoot, "Assets");
            if (!Directory.Exists(assets)) return list;
            foreach (string f in Directory.GetFiles(assets, "*.unity", SearchOption.AllDirectories))
            {
                string rel = f.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
                if (AssetDiff.IsUnityIgnored(rel)) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    rel.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(rel);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        /// <summary>
        /// Script guid of a .asset ScriptableObject instance (first
        /// m_Script line), so callers can classify it (e.g. GameEvent).
        /// Returns null when unreadable.
        /// </summary>
        public static string AssetScriptGuid(string assetFullPath)
        {
            try
            {
                foreach (string line in File.ReadLines(assetFullPath))
                {
                    int i = line.IndexOf("m_Script: {fileID:", StringComparison.Ordinal);
                    if (i < 0) continue;
                    var m = Regex.Match(line, @"guid:\s*([0-9a-fA-F]{32})");
                    return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
                }
            }
            catch (Exception) { }
            return null;
        }
    }
}
