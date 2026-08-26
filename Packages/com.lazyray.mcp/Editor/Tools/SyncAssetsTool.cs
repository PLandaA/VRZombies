using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LazyRay.Core;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    /// <summary>
    /// v7.1: cross-project folder sync. Copies changed + missing files from
    /// the SOURCE project to the TARGET project — ALWAYS with their .meta
    /// (GUIDs are sacred: without them prefab/scene references break).
    ///
    /// Safety model:
    /// - dry_run defaults to TRUE: the first call always shows the plan.
    /// - Never deletes anything on the target (only-in-target files stay).
    /// - Restricted to folders under Assets/ (ProjectSettings/Packages are
    ///   project_setup's territory, not a blind copy's).
    /// - pull (other→this): ONE AssetDatabase.Refresh() at the end (batch
    ///   pattern — never per file). Copied code ⇒ compile + domain reload,
    ///   flagged with a [COMPILE] marker the bridge waits on.
    /// - push (this→other): files land on disk; the other editor imports
    ///   them on its next focus/refresh. Not undoable — file-level copy.
    /// - Large pulls: the final Refresh can exceed the 28s dispatcher cap
    ///   and surface as STILL_RUNNING. The copy already happened — verify
    ///   with compare_assets instead of retrying.
    /// Runs on the main thread on purpose: the Refresh needs it, and a copy
    /// racing an in-progress import is exactly what we do not want.
    /// </summary>
    public class SyncAssetsTool : IMcpTool
    {
        private const int DEFAULT_CAP = 30;
        private const int VERBOSE_CAP = 500;

        public string Name => "sync_assets";

        public string Description =>
            "Copy a folder's changed/missing files between this project and another, ALWAYS with their .meta " +
            "(preserves GUIDs — mandatory for prefab references to survive). direction: 'push' (this→other) or " +
            "'pull' (other→this). DRY-RUN BY DEFAULT: the call shows the plan; pass dry_run:false to actually copy. " +
            "Never deletes target files. Folder must be under Assets/. pull ends with ONE AssetDatabase.Refresh(); " +
            "if code was copied, a compile+domain-reload follows ([COMPILE] marker). Large pulls may report " +
            "STILL_RUNNING while Unity imports — the copy is done; verify with compare_assets, do not retry.";

        public bool IsDestructive => true;

        public bool IsThreadSafe => false; // final Refresh is main-thread-only

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""folder"": { ""type"": ""string"", ""description"": ""Folder under Assets/, relative to project root (whole-folder mode)"" },
                ""files"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""v7.2: explicit file list under Assets/ (mutually exclusive with folder; .meta pairs travel automatically)"" },
                ""direction"": { ""type"": ""string"", ""description"": ""'push' = this→other, 'pull' = other→this"" },
                ""other_project"": { ""type"": ""string"", ""description"": ""Name/path fragment of open instance, or absolute path (closed OK)"" },
                ""dry_run"": { ""type"": ""boolean"", ""description"": ""Default TRUE — plan only. Set false to copy"" },
                ""verbose"": { ""type"": ""boolean"", ""description"": ""Full file lists (default capped)"" }
            },
            ""required"": [""direction""]
        }");

        public string Execute(JObject input)
        {
            string folder = input.Value<string>("folder");
            var filesArr = input["files"] as JArray;
            bool filesMode = filesArr != null && filesArr.Count > 0;

            if (!filesMode && string.IsNullOrWhiteSpace(folder))
                return "ERROR: provide 'folder' (whole-folder sync) or 'files' (explicit file list).";
            if (filesMode && !string.IsNullOrWhiteSpace(folder))
                return "ERROR: 'folder' and 'files' are mutually exclusive — pass one or the other.";

            // v7.2: explicit file list. Normalize, validate, and auto-pair
            // each file with its .meta (GUIDs always travel).
            var fileList = new List<string>();
            if (filesMode)
            {
                foreach (JToken t in filesArr)
                {
                    string rel = (t.ToString() ?? "").Replace('\\', '/').Trim().TrimStart('/');
                    if (rel.Length == 0) continue;
                    if (!rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                        return "ERROR: files must live under Assets/ — offending entry: " + rel;
                    if (rel.Contains(".."))
                        return "ERROR: path traversal not allowed: " + rel;
                    if (!fileList.Contains(rel)) fileList.Add(rel);
                    string meta = rel + ".meta";
                    if (!rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && !fileList.Contains(meta))
                        fileList.Add(meta);
                }
                if (fileList.Count == 0) return "ERROR: 'files' contained no usable paths.";
            }
            else
            {
                folder = folder.Replace('\\', '/').Trim().TrimEnd('/');

                if (!folder.Equals("Assets", StringComparison.OrdinalIgnoreCase) &&
                    !folder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    return "ERROR: sync_assets only operates under Assets/. For packages/defines use project_setup.";
            }

            string direction = (input.Value<string>("direction") ?? "").ToLowerInvariant();
            if (direction != "push" && direction != "pull")
                return "ERROR: 'direction' must be 'push' (this→other) or 'pull' (other→this).";

            bool dryRun = input.Value<bool?>("dry_run") ?? true; // SAFE DEFAULT
            bool verbose = input.Value<bool?>("verbose") ?? false;
            int cap = verbose ? VERBOSE_CAP : DEFAULT_CAP;

            string ownRoot = Path.GetDirectoryName(Application.dataPath);

            if (!filesMode)
            {
                string ownFolderFull = Path.GetFullPath(Path.Combine(ownRoot, folder));
                if (!ownFolderFull.StartsWith(Path.GetFullPath(ownRoot), StringComparison.OrdinalIgnoreCase))
                    return "ERROR: folder escapes the project root.";
            }

            string otherRoot, otherName;
            string err = AssetDiff.ResolveOtherProject(input.Value<string>("other_project"), ownRoot, out otherRoot, out otherName);
            if (err != null) return err;

            string ownName = Path.GetFileName(Path.GetFullPath(ownRoot).TrimEnd(Path.DirectorySeparatorChar, '/'));

            string srcRoot, dstRoot, srcName, dstName;
            if (direction == "push")
            {
                srcRoot = ownRoot; srcName = ownName + " (this)";
                dstRoot = otherRoot; dstName = otherName;
            }
            else
            {
                srcRoot = otherRoot; srcName = otherName;
                dstRoot = ownRoot; dstName = ownName + " (this)";
            }

            Dictionary<string, AssetDiff.FileEntry> src, dst;
            string scopeLabel;
            if (filesMode)
            {
                src = AssetDiff.ScanList(srcRoot, fileList);
                dst = AssetDiff.ScanList(dstRoot, fileList);
                scopeLabel = "files (" + fileList.Count + " listed incl. .meta)";

                // Every explicitly listed NON-meta file must exist in the
                // source — a typo silently syncing nothing helps nobody.
                var absent = new List<string>();
                foreach (string rel in fileList)
                {
                    if (rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue; // metas are best-effort pairs
                    if (!src.ContainsKey(rel)) absent.Add(rel);
                }
                if (absent.Count > 0)
                    return "ERROR: listed file(s) not found in source (" + srcName + "):\n  " + string.Join("\n  ", absent.ToArray());
            }
            else
            {
                if (!Directory.Exists(Path.Combine(srcRoot, folder)))
                    return "ERROR: source folder not found: " + folder + " in " + srcName;

                src = AssetDiff.Scan(srcRoot, folder);
                dst = AssetDiff.Scan(dstRoot, folder);
                scopeLabel = folder;
            }
            var d = AssetDiff.Diff(src, dst); // A = source, B = target

            // Plan: source wins → overwrite differing + create missing-in-target.
            var overwrite = new List<string>();
            var overwriteNewerTarget = new List<string>();
            foreach (var e in d.Different)
            {
                overwrite.Add(e.RelPath);
                if (e.Newer == 1) // target side is newer — about to be clobbered
                    overwriteNewerTarget.Add(e.RelPath + " (target newer by " + AssetDiff.HumanizeDelta(e.Delta) + ")");
            }
            var create = d.OnlyA;
            int untouched = d.OnlyB.Count;

            var plan = new List<string>();
            plan.AddRange(overwrite);
            plan.AddRange(create);

            long bytes = 0;
            bool codeCopied = false;
            var missingMeta = new List<string>();
            foreach (string rel in plan)
            {
                AssetDiff.FileEntry fe;
                if (src.TryGetValue(rel, out fe)) bytes += fe.Size;
                if (IsCodeFile(rel)) codeCopied = true;
                if (!rel.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) &&
                    !src.ContainsKey(rel + ".meta"))
                    missingMeta.Add(rel);
            }

            var sb = new StringBuilder();
            sb.AppendLine(dryRun
                ? "sync_assets DRY RUN — no files were touched"
                : "sync_assets EXECUTED");
            sb.AppendLine(srcName + "  →  " + dstName + "  :  " + scopeLabel);

            if (plan.Count == 0)
            {
                sb.AppendLine("✓ Nothing to copy — target already has everything the source has (identical: " +
                              d.IdenticalCount + ").");
                if (untouched > 0)
                    sb.AppendLine(untouched + " file(s) exist only in the target (never deleted).");
                return sb.ToString();
            }

            sb.AppendLine("copy: " + plan.Count + " file(s) (" + overwrite.Count + " overwrite / " +
                          create.Count + " new), " + AssetDiff.HumanizeBytes(bytes) +
                          " | identical: " + d.IdenticalCount +
                          " | only-in-target (untouched): " + untouched);

            if (overwriteNewerTarget.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ " + overwriteNewerTarget.Count + " TARGET file(s) are NEWER than the source and would be OVERWRITTEN:");
                AppendCapped(sb, overwriteNewerTarget, cap);
            }

            if (missingMeta.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("⚠ " + missingMeta.Count + " source file(s) have NO .meta — the target Unity will mint NEW GUIDs for them:");
                AppendCapped(sb, missingMeta, Math.Min(cap, 10));
            }

            sb.AppendLine();
            sb.AppendLine("files:");
            AppendCapped(sb, AssetDiff.FoldMetaPairs(plan), cap);

            if (dryRun)
            {
                if (codeCopied)
                    sb.AppendLine();
                if (codeCopied && direction == "pull")
                    sb.AppendLine("Code files included — the real run will trigger a compile + domain reload here.");
                else if (codeCopied)
                    sb.AppendLine("Code files included — the other editor will compile on its next focus/refresh.");
                sb.AppendLine();
                sb.AppendLine("Run again with dry_run:false to execute.");
                return sb.ToString();
            }

            // ─── Real copy ─────────────────────────────────────────────
            int copied = 0;
            var failures = new List<string>();
            foreach (string rel in plan)
            {
                AssetDiff.FileEntry fe;
                if (!src.TryGetValue(rel, out fe)) continue;
                try
                {
                    string target = Path.Combine(dstRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    string dir = Path.GetDirectoryName(target);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.Copy(fe.FullPath, target, true);
                    copied++;
                }
                catch (Exception ex)
                {
                    failures.Add(rel + " — " + ex.Message);
                }
            }

            sb.AppendLine();
            sb.AppendLine("Copied " + copied + "/" + plan.Count + " file(s).");
            if (failures.Count > 0)
            {
                sb.AppendLine("⚠ " + failures.Count + " FAILED:");
                AppendCapped(sb, failures, cap);
            }

            if (direction == "pull")
            {
                // Batch pattern: exactly ONE Refresh for the whole sync.
                AssetDatabase.Refresh();
                sb.AppendLine("AssetDatabase.Refresh() triggered (single batch refresh).");
                if (codeCopied)
                    sb.AppendLine("[COMPILE] Code files copied — Unity will compile + domain reload now.");
            }
            else
            {
                sb.AppendLine("Files written into " + otherName + " on disk. That editor imports them on its next focus/refresh" +
                              (codeCopied ? " (code copied ⇒ it will compile + reload over there)." : "."));
            }

            sb.AppendLine("Verify with compare_assets (should report 0 different / 0 only-in-source).");
            return sb.ToString();
        }

        private static bool IsCodeFile(string rel)
        {
            string ext = Path.GetExtension(rel).ToLowerInvariant();
            return ext == ".cs" || ext == ".asmdef" || ext == ".asmref";
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
