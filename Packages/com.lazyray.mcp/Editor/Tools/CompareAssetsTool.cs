using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LazyRay.Core;
using Newtonsoft.Json.Linq;

namespace LazyRay.Tools
{
    /// <summary>
    /// v7.1: cross-project folder diff (size gate + MD5, .meta included).
    /// IsThreadSafe: pure file I/O via AssetDiff — runs on the pipe fast
    /// path, keeps working during compiles/imports, never returns BUSY.
    /// The other project resolves from the instance registry
    /// (%LOCALAPPDATA%\LazyRay\instances) or from an explicit path, so it
    /// also works against a CLOSED project.
    /// </summary>
    public class CompareAssetsTool : IMcpTool
    {
        private const int DEFAULT_CAP = 25;   // per category — token diet
        private const int VERBOSE_CAP = 500;
        private const int MAX_FILES = 25000;  // protects the 90s fast-path budget

        public string Name => "compare_assets";

        public string Description =>
            "Compare a folder between THIS project and another (auto-discovered from the LazyRay instance registry, " +
            "or other_project = name fragment / absolute path — absolute paths work for closed projects). " +
            "Equality = size + MD5, .meta files included; mtime is only reported as a 'which side is newer' hint. " +
            "Reports: identical (count only), different, only-in-A, only-in-B. Lists are capped at " + "25/category — " +
            "pass verbose:true for full lists. TIP: scope to a system folder; comparing all of Assets on large " +
            "projects can exceed the request budget.";

        public bool IsDestructive => false;

        // v7.1: pure file I/O — pipe-thread fast path.
        public bool IsThreadSafe => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""folder"": { ""type"": ""string"", ""description"": ""Folder relative to project root, e.g. 'Assets/2. Scripts/HandPoses'"" },
                ""other_project"": { ""type"": ""string"", ""description"": ""Name/path fragment of an open instance, or absolute path (closed projects OK). Default: the only other open instance"" },
                ""verbose"": { ""type"": ""boolean"", ""description"": ""Full lists instead of 25/category cap (default false)"" }
            },
            ""required"": [""folder""]
        }");

        public string Execute(JObject input)
        {
            string folder = input.Value<string>("folder");
            if (string.IsNullOrWhiteSpace(folder))
                return "ERROR: 'folder' is required (e.g. 'Assets/2. Scripts/HandPoses').";
            folder = folder.Replace('\\', '/').Trim().TrimEnd('/');

            string ownRoot = LazyRayPaths.ProjectRoot; // cached on server start — pipe-thread safe

            // Traversal guard: the folder must stay inside the project.
            string ownFolderFull = Path.GetFullPath(Path.Combine(ownRoot, folder));
            if (!ownFolderFull.StartsWith(Path.GetFullPath(ownRoot), StringComparison.OrdinalIgnoreCase))
                return "ERROR: folder escapes the project root.";
            if (!Directory.Exists(ownFolderFull))
                return "ERROR: folder not found in this project: " + folder;

            string otherRoot, otherName;
            string err = AssetDiff.ResolveOtherProject(input.Value<string>("other_project"), ownRoot, out otherRoot, out otherName);
            if (err != null) return err;

            bool verbose = input.Value<bool?>("verbose") ?? false;
            int cap = verbose ? VERBOSE_CAP : DEFAULT_CAP;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var a = AssetDiff.Scan(ownRoot, folder);
            var b = AssetDiff.Scan(otherRoot, folder);

            if (a.Count + b.Count > MAX_FILES)
                return "ERROR: " + (a.Count + b.Count) + " files under '" + folder +
                       "' — too large for one comparison. Scope to a subfolder (system-level, not all of Assets).";

            bool otherHasFolder = Directory.Exists(Path.Combine(otherRoot, folder));
            var d = AssetDiff.Diff(a, b);
            sw.Stop();

            string ownName = Path.GetFileName(Path.GetFullPath(ownRoot).TrimEnd(Path.DirectorySeparatorChar, '/'));

            var sb = new StringBuilder();
            sb.AppendLine("compare_assets: " + folder);
            sb.AppendLine("A = " + ownName + " (this)  |  B = " + otherName + " (" + otherRoot + ")");
            if (!otherHasFolder)
                sb.AppendLine("⚠ folder does not exist in B — everything reports as only-in-A.");
            sb.AppendLine("identical: " + d.IdenticalCount +
                          " | different: " + d.Different.Count +
                          " | only-A: " + d.OnlyA.Count +
                          " | only-B: " + d.OnlyB.Count +
                          "   (" + (d.ScannedA + d.ScannedB) + " files scanned, " + sw.ElapsedMilliseconds + " ms)");

            if (d.Different.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("DIFFERENT (" + d.Different.Count + "):");
                AppendCapped(sb, FormatDifferent(d.Different), cap);
            }
            if (d.OnlyA.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("ONLY IN A (" + d.OnlyA.Count + "):");
                AppendCapped(sb, AssetDiff.FoldMetaPairs(d.OnlyA), cap);
            }
            if (d.OnlyB.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("ONLY IN B (" + d.OnlyB.Count + "):");
                AppendCapped(sb, AssetDiff.FoldMetaPairs(d.OnlyB), cap);
            }

            if (d.Different.Count == 0 && d.OnlyA.Count == 0 && d.OnlyB.Count == 0)
                sb.AppendLine("✓ Folders are identical.");

            return sb.ToString();
        }

        /// <summary>
        /// meta pairing for the different list:
        /// - base + meta both differ  → "X [+meta]"
        /// - only the meta differs    → loud warning (GUID/import drift is
        ///   exactly how prefab references silently break across projects).
        /// </summary>
        private static List<string> FormatDifferent(List<AssetDiff.DiffEntry> diff)
        {
            var byPath = new Dictionary<string, AssetDiff.DiffEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in diff) byPath[e.RelPath] = e;

            var lines = new List<string>();
            foreach (var e in diff)
            {
                if (e.RelPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    string basePath = e.RelPath.Substring(0, e.RelPath.Length - 5);
                    if (byPath.ContainsKey(basePath)) continue; // folded into base line
                    lines.Add(e.RelPath + NewerHint(e) + "  ⚠ meta-only diff (GUID/import settings!)");
                    continue;
                }
                bool metaToo = byPath.ContainsKey(e.RelPath + ".meta");
                lines.Add(e.RelPath + (metaToo ? " [+meta]" : "") + NewerHint(e));
            }
            return lines;
        }

        private static string NewerHint(AssetDiff.DiffEntry e)
        {
            if (e.Newer == 0) return " — same mtime";
            string side = e.Newer < 0 ? "A" : "B";
            return " — " + side + " newer (Δ " + AssetDiff.HumanizeDelta(e.Delta) + ")";
        }

        private static void AppendCapped(StringBuilder sb, List<string> lines, int cap)
        {
            int shown = Math.Min(lines.Count, cap);
            for (int i = 0; i < shown; i++)
                sb.AppendLine("  " + lines[i]);
            if (lines.Count > cap)
                sb.AppendLine("  (+" + (lines.Count - cap) + " more — pass verbose:true)");
        }
    }
}
