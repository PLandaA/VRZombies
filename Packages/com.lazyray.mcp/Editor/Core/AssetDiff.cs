using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace LazyRay.Core
{
    /// <summary>
    /// v7.1: shared diff engine for compare_assets / sync_assets.
    /// Pure file I/O — no Unity APIs, safe on the pipe thread (fast path).
    ///
    /// Comparison contract:
    /// - Files are compared by SIZE first; equal sizes are settled by MD5.
    ///   Modification dates are NEVER used to decide equality (they lie:
    ///   git checkouts, copies and reimports rewrite them) — only as a
    ///   human hint of which side is probably newer.
    /// - .meta files participate as ordinary files: GUID or importer drift
    ///   between projects is exactly what this exists to catch (broken
    ///   prefab references after a careless copy).
    /// - Unity-invisible entries are skipped, mirroring what the editor
    ///   ignores: dot-prefixed files/folders, '~'-suffixed segments, .tmp.
    /// </summary>
    public static class AssetDiff
    {
        public sealed class FileEntry
        {
            public string FullPath;
            public long Size;
            public DateTime MTimeUtc;
        }

        public sealed class DiffEntry
        {
            public string RelPath;
            /// <summary>-1 = A newer, +1 = B newer, 0 = same mtime.</summary>
            public int Newer;
            public TimeSpan Delta;
        }

        public sealed class DiffResult
        {
            public int IdenticalCount;
            public List<DiffEntry> Different = new List<DiffEntry>();
            public List<string> OnlyA = new List<string>();
            public List<string> OnlyB = new List<string>();
            public int ScannedA;
            public int ScannedB;
        }

        // ─── Scan ──────────────────────────────────────────────────────

        /// <summary>
        /// Map of project-root-relative unix paths -> entry for every file
        /// under relFolder. Missing folder returns an empty map (caller
        /// decides whether that is an error or an "only-in-X" situation).
        /// </summary>
        public static Dictionary<string, FileEntry> Scan(string projectRoot, string relFolder)
        {
            var map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            string rootFull = Path.GetFullPath(projectRoot);
            string folderFull = Path.GetFullPath(Path.Combine(projectRoot, relFolder));
            if (!Directory.Exists(folderFull)) return map;

            foreach (string file in Directory.GetFiles(folderFull, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(rootFull.Length)
                    .TrimStart(Path.DirectorySeparatorChar, '/')
                    .Replace('\\', '/');
                if (IsUnityIgnored(rel)) continue;

                var fi = new FileInfo(file);
                var entry = new FileEntry();
                entry.FullPath = file;
                entry.Size = fi.Length;
                entry.MTimeUtc = fi.LastWriteTimeUtc;
                map[rel] = entry;
            }
            return map;
        }

        /// <summary>
        /// v7.2: map for an EXPLICIT list of project-root-relative files.
        /// Only files that exist end up in the map — the caller compares
        /// the map against its input list to detect missing ones.
        /// </summary>
        public static Dictionary<string, FileEntry> ScanList(string projectRoot, IEnumerable<string> relPaths)
        {
            var map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (string relRaw in relPaths)
            {
                string rel = (relRaw ?? "").Replace('\\', '/').Trim().TrimStart('/');
                if (rel.Length == 0 || map.ContainsKey(rel)) continue;
                string full = Path.Combine(projectRoot, rel);
                if (!File.Exists(full)) continue;
                var fi = new FileInfo(full);
                var entry = new FileEntry();
                entry.FullPath = full;
                entry.Size = fi.Length;
                entry.MTimeUtc = fi.LastWriteTimeUtc;
                map[rel] = entry;
            }
            return map;
        }

        /// <summary>
        /// v7.2: size gate + MD5 equality for two individual files.
        /// Both paths must exist.
        /// </summary>
        public static bool FilesIdentical(string fullPathA, string fullPathB)
        {
            var fa = new FileInfo(fullPathA);
            var fb = new FileInfo(fullPathB);
            if (fa.Length != fb.Length) return false;
            return HashesEqual(fullPathA, fullPathB);
        }

        /// <summary>True for paths Unity itself would not import.</summary>
        public static bool IsUnityIgnored(string relUnixPath)
        {
            if (relUnixPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
            string[] segments = relUnixPath.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                string s = segments[i];
                if (s.Length == 0) continue;
                if (s[0] == '.') return true;
                if (s[s.Length - 1] == '~') return true;
            }
            return false;
        }

        // ─── Diff ──────────────────────────────────────────────────────

        public static DiffResult Diff(Dictionary<string, FileEntry> a, Dictionary<string, FileEntry> b)
        {
            var result = new DiffResult();
            result.ScannedA = a.Count;
            result.ScannedB = b.Count;

            var union = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in a.Keys) union.Add(k);
            foreach (string k in b.Keys) union.Add(k);

            foreach (string rel in union)
            {
                FileEntry ea, eb;
                bool inA = a.TryGetValue(rel, out ea);
                bool inB = b.TryGetValue(rel, out eb);

                if (inA && !inB) { result.OnlyA.Add(rel); continue; }
                if (!inA && inB) { result.OnlyB.Add(rel); continue; }

                // Size gate first — MD5 only when sizes match, so identical
                // trees pay the hash but different ones settle instantly.
                bool same = ea.Size == eb.Size && HashesEqual(ea.FullPath, eb.FullPath);
                if (same) { result.IdenticalCount++; continue; }

                var entry = new DiffEntry();
                entry.RelPath = rel;
                // 2s tolerance: near-simultaneous writes (batch copies, git
                // checkouts) must not raise the "target is newer" alarm.
                TimeSpan delta = ea.MTimeUtc > eb.MTimeUtc ? ea.MTimeUtc - eb.MTimeUtc : eb.MTimeUtc - ea.MTimeUtc;
                if (delta.TotalSeconds < 2) { entry.Newer = 0; entry.Delta = TimeSpan.Zero; }
                else { entry.Newer = ea.MTimeUtc > eb.MTimeUtc ? -1 : 1; entry.Delta = delta; }
                result.Different.Add(entry);
            }
            return result;
        }

        private static bool HashesEqual(string pathA, string pathB)
        {
            byte[] ha = Md5(pathA);
            byte[] hb = Md5(pathB);
            if (ha.Length != hb.Length) return false;
            for (int i = 0; i < ha.Length; i++)
                if (ha[i] != hb[i]) return false;
            return true;
        }

        private static byte[] Md5(string path)
        {
            // FileShare.ReadWrite: Unity or the other editor may hold the
            // file open — a diff must never fail on a sharing violation.
            using (var md5 = MD5.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return md5.ComputeHash(fs);
            }
        }

        // ─── Other-project resolution ──────────────────────────────────

        /// <summary>
        /// Locate the "other" project. hint may be:
        /// - null/empty  → the single OTHER registered instance (error if 0 or 2+),
        /// - an absolute directory path → used directly (works for CLOSED projects),
        /// - a name/path fragment → matched against the instance registry.
        /// Returns null on success (root+name filled), or an ERROR string.
        /// Registry reads are pure file I/O — pipe-thread safe.
        /// </summary>
        public static string ResolveOtherProject(string hint, string ownRoot, out string otherRoot, out string otherName)
        {
            otherRoot = null;
            otherName = null;
            string ownFull = NormalizeDir(ownRoot);

            if (!string.IsNullOrEmpty(hint) && Path.IsPathRooted(hint) && Directory.Exists(hint))
            {
                string full = NormalizeDir(hint);
                if (string.Equals(full, ownFull, StringComparison.OrdinalIgnoreCase))
                    return "ERROR: other_project points at THIS project (" + full + ").";
                string invalid = ValidateUnityProject(full);
                if (invalid != null) return invalid;
                otherRoot = full;
                otherName = Path.GetFileName(full);
                return null;
            }

            var candidates = ListOtherRegisteredProjects(ownFull);

            if (!string.IsNullOrEmpty(hint))
            {
                string q = hint.ToLowerInvariant();
                var matches = new List<KeyValuePair<string, string>>();
                foreach (var c in candidates)
                    if (c.Key.ToLowerInvariant().Contains(q) || c.Value.ToLowerInvariant().Contains(q))
                        matches.Add(c);

                if (matches.Count == 1)
                {
                    string invalid = ValidateUnityProject(matches[0].Value);
                    if (invalid != null) return invalid;
                    otherName = matches[0].Key;
                    otherRoot = matches[0].Value;
                    return null;
                }
                if (matches.Count > 1)
                    return "ERROR: ambiguous other_project '" + hint + "'. Matches: " + JoinCandidates(matches) +
                           ". Use a longer fragment or the absolute path.";
                return "ERROR: no registered instance matches '" + hint + "'. " +
                       (candidates.Count > 0
                           ? "Registered (excluding this project): " + JoinCandidates(candidates) + ". "
                           : "No OTHER instance is registered. ") +
                       "For a closed project pass other_project as an absolute path.";
            }

            if (candidates.Count == 1)
            {
                string invalid = ValidateUnityProject(candidates[0].Value);
                if (invalid != null) return invalid;
                otherName = candidates[0].Key;
                otherRoot = candidates[0].Value;
                return null;
            }
            if (candidates.Count == 0)
                return "ERROR: no OTHER Unity instance is registered. Open the other project, " +
                       "or pass other_project as an absolute path (works while it is closed).";
            return "ERROR: multiple other instances are open: " + JoinCandidates(candidates) +
                   ". Pass other_project (name fragment or path).";
        }

        /// <summary>(name, normalized path) of registered instances that are not this project.</summary>
        private static List<KeyValuePair<string, string>> ListOtherRegisteredProjects(string ownFull)
        {
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                string dir = InstanceRegistry.RegistryDir;
                if (!Directory.Exists(dir)) return list;

                foreach (string f in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var j = JObject.Parse(File.ReadAllText(f));
                        string p = j.Value<string>("project_path");
                        if (string.IsNullOrEmpty(p)) continue;
                        string full = NormalizeDir(p);
                        if (string.Equals(full, ownFull, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!Directory.Exists(full)) continue; // crashed entry pointing nowhere

                        bool dup = false;
                        foreach (var c in list)
                            if (string.Equals(c.Value, full, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                        if (dup) continue;

                        string n = j.Value<string>("project_name");
                        if (string.IsNullOrEmpty(n)) n = Path.GetFileName(full);
                        list.Add(new KeyValuePair<string, string>(n, full));
                    }
                    catch { /* half-written heartbeat file — next tick fixes it */ }
                }
            }
            catch { }
            return list;
        }

        public static string ValidateUnityProject(string root)
        {
            if (!Directory.Exists(Path.Combine(root, "Assets")) ||
                !Directory.Exists(Path.Combine(root, "ProjectSettings")))
                return "ERROR: '" + root + "' does not look like a Unity project (missing Assets/ or ProjectSettings/).";
            return null;
        }

        private static string NormalizeDir(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, '/');
        }

        private static string JoinCandidates(List<KeyValuePair<string, string>> c)
        {
            var parts = new List<string>();
            foreach (var kv in c) parts.Add(kv.Key + " (" + kv.Value + ")");
            return string.Join(" | ", parts.ToArray());
        }

        // ─── Shared formatting helpers ─────────────────────────────────

        /// <summary>"X" + "X.meta" in the same list collapse into "X [+meta]".</summary>
        public static List<string> FoldMetaPairs(List<string> relPaths)
        {
            var set = new HashSet<string>(relPaths, StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (string rel in relPaths)
            {
                if (rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    string basePath = rel.Substring(0, rel.Length - 5);
                    if (set.Contains(basePath)) continue; // folded into the base line
                    lines.Add(rel);
                    continue;
                }
                lines.Add(set.Contains(rel + ".meta") ? rel + " [+meta]" : rel);
            }
            return lines;
        }

        public static string HumanizeDelta(TimeSpan t)
        {
            if (t.TotalHours >= 48) return t.TotalDays.ToString("F1") + "d";
            if (t.TotalMinutes >= 120) return t.TotalHours.ToString("F1") + "h";
            if (t.TotalSeconds >= 120) return t.TotalMinutes.ToString("F0") + "m";
            return t.TotalSeconds.ToString("F0") + "s";
        }

        public static string HumanizeBytes(long bytes)
        {
            if (bytes < 1024) return bytes + "B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + "KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + "MB";
        }
    }
}
