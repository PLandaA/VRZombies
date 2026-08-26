using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    public class DeleteFileTool : IMcpTool
    {
        public string Name => "delete_file";
        public string Description =>
            "Delete a file from the project. Path is relative to project root. " +
            "Cannot delete directories (only files). Will also remove the .meta file if present. " +
            "BATCH MODE: pass defer_compile=true to delete without triggering import/compile; commit with commit_edits.";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""path"": { ""type"": ""string"", ""description"": ""File path relative to project root"" },
                ""defer_compile"": { ""type"": ""boolean"", ""description"": ""Delete without triggering import/compile; commit later with commit_edits (default false)"" }
            },
            ""required"": [""path""]
        }");

        public string Execute(JObject input)
        {
            string relativePath = input.Value<string>("path");
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string fullPath = Path.Combine(projectRoot, relativePath);

            string normalizedFull = Path.GetFullPath(fullPath);
            string normalizedRoot = Path.GetFullPath(projectRoot);
            if (!normalizedFull.StartsWith(normalizedRoot))
                return "ERROR: Cannot delete files outside the project directory.";

            if (!File.Exists(fullPath))
                return $"ERROR: File not found: {relativePath}";

            string fileName = Path.GetFileName(fullPath).ToLower();
            if (fileName is "projectsettings.asset" or "editorsettings.asset" or "qualitysettings.asset")
                return "ERROR: Cannot delete critical Unity project files.";

            File.Delete(fullPath);
            string metaPath = fullPath + ".meta";
            if (File.Exists(metaPath)) File.Delete(metaPath);

            // v6.6: batch mode — same contract as edit_file(defer_compile).
            if (input.Value<bool?>("defer_compile") ?? false)
            {
                string client = input.Value<string>("__client") ?? "legacy";
                DeferredEdits.Add(client, relativePath + " (deleted)");
                return $"Deleted: {relativePath} [DEFERRED — {DeferredEdits.Count(client)} edit(s) pending, call commit_edits to compile]";
            }

            AssetDatabase.Refresh();
            return $"Deleted: {relativePath}";
        }
    }
}
