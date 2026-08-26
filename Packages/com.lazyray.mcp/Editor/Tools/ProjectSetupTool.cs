using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LazyRay.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    /// <summary>
    /// v7.1: project architecture spec. A declarative file
    /// (Assets/LazyRayData/LazyRayProjectSpec.json) states what this
    /// project family REQUIRES: packages (exact versions or git URLs, e.g.
    /// UniTask via github), standard folders, scripting define symbols.
    /// The same spec file is meant to be synced across sibling projects
    /// (sync_assets carries it like any other asset) so every project can
    /// be checked against one source of truth.
    ///
    /// Actions:
    /// - init:           snapshot the CURRENT Packages/manifest.json direct
    ///                   dependencies into a fresh spec (starting point to curate).
    /// - check:          compare manifest + folders + defines against the spec,
    ///                   report deviations compactly.
    /// - apply_packages: add MISSING spec packages to Packages/manifest.json.
    ///                   Never touches versions of already-present packages.
    ///                   By default Unity resolves on its next focus/refresh;
    ///                   resolve:true forces Client.Resolve() NOW (long
    ///                   import/reload possible — flagged with [COMPILE]).
    ///
    /// Spec semantics: packages is name → exact version or git URL; the
    /// value "*" (or "") means "must be present, any version". folders must
    /// exist. defines are required on the ACTIVE build target.
    /// </summary>
    public class ProjectSetupTool : IMcpTool
    {
        private const string SPEC_REL_PATH = "Assets/LazyRayData/LazyRayProjectSpec.json";
        private const string MANIFEST_REL_PATH = "Packages/manifest.json";

        public string Name => "project_setup";

        public string Description =>
            "Project architecture spec (" + SPEC_REL_PATH + "): required packages (exact version or git URL; '*' = " +
            "any version), standard folders, ESSENTIAL FILES (must exist and match the sibling project byte-for-byte), "+
            "scripting defines (active build target). Actions: 'init' snapshots the " +
            "current manifest into a new spec (curate it afterwards; overwrite:true to replace); 'check' reports " +
            "deviations (missing/mismatched packages, missing folders/defines); 'apply_packages' adds MISSING spec " +
            "packages to Packages/manifest.json — Unity resolves on next focus, or pass resolve:true to force " +
            "Client.Resolve() now (possible long import/reload, [COMPILE] marker). Sync the spec file itself across " +
            "projects with sync_assets.";

        public bool IsDestructive => true;

        public bool IsThreadSafe => false; // reads PlayerSettings, may trigger UPM resolve

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""description"": ""'init' | 'check' | 'apply_packages'"" },
                ""resolve"": { ""type"": ""boolean"", ""description"": ""apply_packages: force UPM Client.Resolve() now (default false — Unity resolves on next focus)"" },
                ""overwrite"": { ""type"": ""boolean"", ""description"": ""init: overwrite an existing spec (default false)"" }
            },
            ""required"": [""action""]
        }");

        public string Execute(JObject input)
        {
            string action = (input.Value<string>("action") ?? "").ToLowerInvariant();
            string projectRoot = Path.GetDirectoryName(Application.dataPath);

            try
            {
                if (action == "init") return DoInit(projectRoot, input.Value<bool?>("overwrite") ?? false);
                if (action == "check") return DoCheck(projectRoot);
                if (action == "apply_packages") return DoApplyPackages(projectRoot, input.Value<bool?>("resolve") ?? false);
                return "ERROR: Unknown action '" + action + "'. Use: init, check, apply_packages.";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        // ─── init ──────────────────────────────────────────────────────

        private string DoInit(string projectRoot, bool overwrite)
        {
            string specPath = Path.Combine(projectRoot, SPEC_REL_PATH);
            if (File.Exists(specPath) && !overwrite)
                return "ERROR: spec already exists at " + SPEC_REL_PATH + ". Pass overwrite:true to replace it.";

            JObject deps = ReadManifestDependencies(projectRoot);
            if (deps == null)
                return "ERROR: could not read " + MANIFEST_REL_PATH + ".";

            var spec = new JObject();
            spec["spec_version"] = 2;
            spec["_doc"] = "packages: name -> exact version or git URL ('*' = any version, presence only). " +
                           "folders: must exist. files: essential single files — must exist AND match the sibling " +
                           "project byte-for-byte (evolving system code like the GameEvents core). " +
                           "defines: required scripting define symbols on the active build target. " +
                           "Curate this snapshot: keep only what EVERY sibling project must have.";
            spec["packages"] = deps.DeepClone();
            var folders = new JArray();
            folders.Add("Assets/LazyRayData");
            spec["folders"] = folders;
            spec["files"] = new JArray();
            spec["defines"] = new JArray();

            string dir = Path.GetDirectoryName(specPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(specPath, spec.ToString(Formatting.Indented));

            return "Spec created: " + SPEC_REL_PATH + " with " + ((JObject)spec["packages"]).Count +
                   " package(s) snapshotted from the manifest.\n" +
                   "Curate it now: remove project-specific packages, keep the shared architecture, add required " +
                   "defines/folders. Then sync it to siblings with sync_assets and run project_setup check there.\n" +
                   "(File appears in the Project window after the next refresh — none was forced.)";
        }

        // ─── check ─────────────────────────────────────────────────────

        private string DoCheck(string projectRoot)
        {
            JObject spec = ReadSpec(projectRoot);
            if (spec == null)
                return "ERROR: no spec at " + SPEC_REL_PATH + ". Run project_setup init first (or sync one from a sibling project).";

            JObject deps = ReadManifestDependencies(projectRoot);
            if (deps == null)
                return "ERROR: could not read " + MANIFEST_REL_PATH + ".";

            var sb = new StringBuilder();
            string projName = Path.GetFileName(Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, '/'));
            sb.AppendLine("project_setup check — " + projName);
            int deviations = 0;

            // Packages
            var specPackages = spec["packages"] as JObject;
            if (specPackages != null && specPackages.Count > 0)
            {
                var missing = new List<string>();
                var mismatched = new List<string>();
                int ok = 0;
                foreach (var prop in specPackages.Properties())
                {
                    string wanted = prop.Value == null ? "" : prop.Value.ToString();
                    JToken actualTok;
                    if (!deps.TryGetValue(prop.Name, out actualTok))
                    {
                        missing.Add(prop.Name + (IsAnyVersion(wanted) ? "" : "  (spec: " + wanted + ")"));
                        continue;
                    }
                    string actual = actualTok.ToString();
                    if (!IsAnyVersion(wanted) && !string.Equals(wanted, actual, StringComparison.Ordinal))
                        mismatched.Add(prop.Name + "  spec=" + wanted + "  manifest=" + actual);
                    else
                        ok++;
                }
                sb.AppendLine("packages: " + ok + "/" + specPackages.Count + " ok");
                foreach (string m in missing) { sb.AppendLine("  MISSING   " + m); deviations++; }
                foreach (string m in mismatched) { sb.AppendLine("  VERSION   " + m); deviations++; }
                int extra = deps.Count - (specPackages.Count - missing.Count);
                if (extra > 0)
                    sb.AppendLine("  (manifest has " + extra + " package(s) not in the spec — informational, not a deviation)");
            }
            else
            {
                sb.AppendLine("packages: (spec declares none)");
            }

            // Folders
            var specFolders = spec["folders"] as JArray;
            if (specFolders != null && specFolders.Count > 0)
            {
                int ok = 0;
                var missing = new List<string>();
                foreach (JToken t in specFolders)
                {
                    string rel = t.ToString();
                    if (Directory.Exists(Path.Combine(projectRoot, rel))) ok++;
                    else missing.Add(rel);
                }
                sb.AppendLine("folders: " + ok + "/" + specFolders.Count + " ok");
                foreach (string m in missing) { sb.AppendLine("  MISSING   " + m); deviations++; }
            }

            // v7.2: Essential files — must exist here, and when a sibling
            // project is reachable (registry or same content on disk) they
            // must also MATCH it byte-for-byte. Evolving system files (e.g.
            // the GameEvents core) drift silently otherwise.
            var specFiles = spec["files"] as JArray;
            if (specFiles != null && specFiles.Count > 0)
            {
                string otherRoot = null, otherName = null;
                string resolveErr = AssetDiff.ResolveOtherProject(null, projectRoot, out otherRoot, out otherName);
                bool haveSibling = resolveErr == null && otherRoot != null;

                int ok = 0;
                var problems = new List<string>();
                foreach (JToken t in specFiles)
                {
                    string rel = t.ToString().Replace('\\', '/').Trim().TrimStart('/');
                    string local = Path.Combine(projectRoot, rel);
                    if (!File.Exists(local)) { problems.Add("MISSING      " + rel); continue; }

                    if (haveSibling)
                    {
                        string sib = Path.Combine(otherRoot, rel);
                        if (File.Exists(sib) && !AssetDiff.FilesIdentical(local, sib))
                        {
                            DateTime lm = File.GetLastWriteTimeUtc(local);
                            DateTime sm = File.GetLastWriteTimeUtc(sib);
                            TimeSpan delta = lm > sm ? lm - sm : sm - lm;
                            string hint;
                            if (delta.TotalSeconds < 2) hint = "";
                            else if (sm > lm) hint = "  (" + otherName + " newer by " + AssetDiff.HumanizeDelta(delta) + " — sync_assets pull with files:[...])";
                            else hint = "  (this project newer by " + AssetDiff.HumanizeDelta(delta) + " — push it to " + otherName + ")";
                            problems.Add("OUT-OF-SYNC  " + rel + hint);
                            continue;
                        }
                    }
                    ok++;
                }
                sb.AppendLine("files: " + ok + "/" + specFiles.Count + " ok" +
                              (haveSibling ? " (compared vs " + otherName + ")" : " (existence only — no sibling project reachable)"));
                foreach (string p in problems) { sb.AppendLine("  " + p); deviations++; }
            }

            // Defines (active build target)
            var specDefines = spec["defines"] as JArray;
            if (specDefines != null && specDefines.Count > 0)
            {
                var current = ReadActiveDefines();
                if (current == null)
                {
                    sb.AppendLine("defines: could not read (unsupported build target group) — skipped");
                }
                else
                {
                    int ok = 0;
                    var missing = new List<string>();
                    foreach (JToken t in specDefines)
                    {
                        string def = t.ToString();
                        if (current.Contains(def)) ok++;
                        else missing.Add(def);
                    }
                    sb.AppendLine("defines: " + ok + "/" + specDefines.Count + " ok (active build target)");
                    foreach (string m in missing) { sb.AppendLine("  MISSING   " + m); deviations++; }
                }
            }

            sb.AppendLine(deviations == 0
                ? "✓ Project complies with the spec."
                : "✗ " + deviations + " deviation(s). Fix packages with project_setup apply_packages; folders/defines by hand or execute_code.");
            return sb.ToString();
        }

        // ─── apply_packages ────────────────────────────────────────────

        private string DoApplyPackages(string projectRoot, bool resolveNow)
        {
            JObject spec = ReadSpec(projectRoot);
            if (spec == null)
                return "ERROR: no spec at " + SPEC_REL_PATH + ". Run project_setup init first.";

            var specPackages = spec["packages"] as JObject;
            if (specPackages == null || specPackages.Count == 0)
                return "Spec declares no packages — nothing to apply.";

            string manifestPath = Path.Combine(projectRoot, MANIFEST_REL_PATH);
            if (!File.Exists(manifestPath))
                return "ERROR: " + MANIFEST_REL_PATH + " not found.";

            // Parse the WHOLE manifest and only touch dependencies —
            // scopedRegistries and friends must survive untouched.
            JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
            var deps = manifest["dependencies"] as JObject;
            if (deps == null)
            {
                deps = new JObject();
                manifest["dependencies"] = deps;
            }

            var added = new List<string>();
            var skippedAnyVersion = new List<string>();
            foreach (var prop in specPackages.Properties())
            {
                if (deps.ContainsKey(prop.Name)) continue; // present — versions are never changed here
                string wanted = prop.Value == null ? "" : prop.Value.ToString();
                if (IsAnyVersion(wanted))
                {
                    skippedAnyVersion.Add(prop.Name);
                    continue; // can't add without a concrete version/URL
                }
                deps.Add(prop.Name, wanted);
                added.Add(prop.Name + " → " + wanted);
            }

            var sb = new StringBuilder();
            if (added.Count == 0)
            {
                sb.AppendLine("No missing packages to add — manifest already satisfies the spec.");
            }
            else
            {
                File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented));
                sb.AppendLine("Added " + added.Count + " package(s) to " + MANIFEST_REL_PATH + ":");
                foreach (string a in added) sb.AppendLine("  + " + a);
            }

            if (skippedAnyVersion.Count > 0)
                sb.AppendLine("⚠ Skipped (spec says '*' / any version — give them a concrete version or git URL to auto-add): " +
                              string.Join(", ", skippedAnyVersion.ToArray()));

            if (added.Count > 0)
            {
                if (resolveNow)
                {
                    UnityEditor.PackageManager.Client.Resolve();
                    sb.AppendLine("[COMPILE] Client.Resolve() requested — Unity will resolve, import and possibly " +
                                  "domain-reload now. This can take a while on this project; expect the editor to be busy.");
                }
                else
                {
                    sb.AppendLine("Unity will resolve the manifest on its next focus/refresh (or re-run with resolve:true " +
                                  "to force it now — long import/reload possible).");
                }
            }

            return sb.ToString();
        }

        // ─── Helpers ───────────────────────────────────────────────────

        private static bool IsAnyVersion(string wanted)
        {
            return string.IsNullOrEmpty(wanted) || wanted == "*";
        }

        private static JObject ReadSpec(string projectRoot)
        {
            string specPath = Path.Combine(projectRoot, SPEC_REL_PATH);
            if (!File.Exists(specPath)) return null;
            return JObject.Parse(File.ReadAllText(specPath));
        }

        private static JObject ReadManifestDependencies(string projectRoot)
        {
            string manifestPath = Path.Combine(projectRoot, MANIFEST_REL_PATH);
            if (!File.Exists(manifestPath)) return null;
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            var deps = manifest["dependencies"] as JObject;
            return deps == null ? new JObject() : deps;
        }

        /// <summary>Defines on the active build target as a set, or null when unreadable.</summary>
        private static HashSet<string> ReadActiveDefines()
        {
            try
            {
                var nbt = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(
                    EditorUserBuildSettings.selectedBuildTargetGroup);
                string raw = PlayerSettings.GetScriptingDefineSymbols(nbt);
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (string s in (raw ?? "").Split(';'))
                {
                    string trimmed = s.Trim();
                    if (trimmed.Length > 0) set.Add(trimmed);
                }
                return set;
            }
            catch
            {
                return null;
            }
        }
    }
}
