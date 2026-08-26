using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    public class ScriptEditTool : IMcpTool
    {
        public string Name => "edit_file";
        public string Description =>
            "Edit or create a file. For editing: provide 'path', 'old_str' (unique string to find), 'new_str' (replacement). " +
            "For creating: set create=true and provide 'path' and 'content'. " +
            "If editing a .cs file, the bridge will automatically wait for Unity compilation to complete. " +
            "BATCH MODE: pass defer_compile=true to write WITHOUT triggering import/compile (returns instantly); " +
            "after the last deferred edit, call commit_edits to pay a SINGLE compile+domain-reload for the whole batch.";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""path"": { ""type"": ""string"", ""description"": ""File path relative to project root"" },
                ""old_str"": { ""type"": ""string"", ""description"": ""String to find and replace (must be unique)"" },
                ""new_str"": { ""type"": ""string"", ""description"": ""Replacement string"" },
                ""create"": { ""type"": ""boolean"", ""description"": ""Set true to create a new file"" },
                ""content"": { ""type"": ""string"", ""description"": ""Full file content (for create mode)"" },
                ""defer_compile"": { ""type"": ""boolean"", ""description"": ""Write without triggering import/compile; commit later with commit_edits (default false)"" }
            },
            ""required"": [""path""]
        }");

        public string Execute(JObject input)
        {
            string relativePath = input.Value<string>("path");
            bool isCreate = input.Value<bool?>("create") ?? false;

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string fullPath = Path.Combine(projectRoot, relativePath);

            string normalizedFull = Path.GetFullPath(fullPath);
            string normalizedRoot = Path.GetFullPath(projectRoot);
            if (!normalizedFull.StartsWith(normalizedRoot))
                return "ERROR: Cannot edit files outside the project directory.";

            return isCreate ? CreateFile(fullPath, relativePath, input) : ReplaceInFile(fullPath, relativePath, input);
        }

        private string CreateFile(string fullPath, string relativePath, JObject input)
        {
            string content = input.Value<string>("content");
            if (string.IsNullOrEmpty(content))
                return "ERROR: 'content' is required when creating a file.";

            if (File.Exists(fullPath))
                return $"ERROR: File already exists: {relativePath}. Use str_replace mode to edit, or delete first.";

            string dir = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(fullPath, content);

            int lineCount = content.Split('\n').Length;

            // v6.5: batch mode — skip the Refresh (and therefore the compile +
            // domain reload). commit_edits pays it once for the whole batch.
            if (input.Value<bool?>("defer_compile") ?? false)
            {
                string client = input.Value<string>("__client") ?? "legacy";
                DeferredEdits.Add(client, relativePath);
                return $"Created file: {relativePath} ({lineCount} lines) [DEFERRED — {DeferredEdits.Count(client)} edit(s) pending, call commit_edits to compile]";
            }

            AssetDatabase.Refresh();
            return $"Created file: {relativePath} ({lineCount} lines)";
        }

        private string ReplaceInFile(string fullPath, string relativePath, JObject input)
        {
            string oldStr = input.Value<string>("old_str");
            string newStr = input.Value<string>("new_str") ?? "";

            if (string.IsNullOrEmpty(oldStr))
                return "ERROR: 'old_str' is required for str_replace mode.";

            if (!File.Exists(fullPath))
                return $"ERROR: File not found: {relativePath}. Use create mode to create a new file.";

            string fileContent = File.ReadAllText(fullPath);

            int count = 0, searchFrom = 0;
            while (true)
            {
                int idx = fileContent.IndexOf(oldStr, searchFrom, System.StringComparison.Ordinal);
                if (idx < 0) break;
                count++;
                searchFrom = idx + oldStr.Length;
            }

            if (count == 0)
                return $"ERROR: The string to replace was not found in {relativePath}.\nSearched for:\n---\n{Truncate(oldStr)}\n---";

            if (count > 1)
                return $"ERROR: The string to replace was found {count} times in {relativePath}. It must be unique.";

            string newContent = fileContent.Replace(oldStr, newStr);
            File.WriteAllText(fullPath, newContent);

            // v6.5: batch mode — see CreateFile.
            bool deferred = input.Value<bool?>("defer_compile") ?? false;
            string client = input.Value<string>("__client") ?? "legacy";
            if (deferred)
                DeferredEdits.Add(client, relativePath);
            else
                AssetDatabase.Refresh();

            string suffix = deferred
                ? $" [DEFERRED — {DeferredEdits.Count(client)} edit(s) pending, call commit_edits to compile]"
                : "";

            int removedLines = oldStr.Split('\n').Length;
            int addedLines = newStr.Split('\n').Length;

            if (string.IsNullOrEmpty(newStr))
                return $"Deleted {removedLines} lines from {relativePath}{suffix}";

            return $"Replaced in {relativePath}: {removedLines} lines -> {addedLines} lines{suffix}";
        }

        private string Truncate(string text, int maxLen = 200)
        {
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen) + "... (truncated)";
        }
    }
}
