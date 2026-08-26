using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LazyRay.Tools
{
    public class ScriptReadTool : IMcpTool
    {
        public string Name => "read_file";
        public string Description =>
            "Read the contents of a file in the project. " +
            "Returns the file content with line numbers. " +
            "Use 'line_start' and 'line_end' to read a specific range. " +
            "Path is relative to project root (e.g., 'Assets/Scripts/Player.cs').";

        public bool IsDestructive => false;

        // v6.4: pure file I/O — safe to run on the pipe thread (fast path).
        public bool IsThreadSafe => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""path"": { ""type"": ""string"", ""description"": ""File path relative to project root"" },
                ""line_start"": { ""type"": ""integer"", ""description"": ""First line to read (1-indexed, optional)"" },
                ""line_end"": { ""type"": ""integer"", ""description"": ""Last line to read (1-indexed, optional)"" },
                ""full"": { ""type"": ""boolean"", ""description"": ""Force full output for files over 400 lines (default false)"" }
            },
            ""required"": [""path""]
        }");

        public string Execute(JObject input)
        {
            string relativePath = input.Value<string>("path");
            int? lineStart = input.Value<int?>("line_start");
            int? lineEnd = input.Value<int?>("line_end");

            string projectRoot = LazyRay.Core.LazyRayPaths.ProjectRoot; // v6.4: cached — no main-thread API
            string fullPath = Path.Combine(projectRoot, relativePath);

            string normalizedFull = Path.GetFullPath(fullPath);
            string normalizedRoot = Path.GetFullPath(projectRoot);
            if (!normalizedFull.StartsWith(normalizedRoot))
                return "ERROR: Cannot read files outside the project directory.";

            if (!File.Exists(fullPath))
                return $"ERROR: File not found: {relativePath}";

            var allLines = File.ReadAllLines(fullPath);
            int total = allLines.Length;

            // v6.7 token diet: a rangeless read of a huge file returns the
            // head + a notice instead of dumping thousands of lines into the
            // conversation. Pass a range or full=true for the rest.
            const int AUTO_CAP = 400;
            bool full = input.Value<bool?>("full") ?? false;
            bool capped = false;
            if (!lineStart.HasValue && !lineEnd.HasValue && !full && total > AUTO_CAP)
            {
                lineEnd = 120;
                capped = true;
            }

            int start = Mathf.Clamp((lineStart ?? 1) - 1, 0, total);
            int end = Mathf.Clamp((lineEnd ?? total) - 1, start, total - 1);
            var selectedLines = allLines.Skip(start).Take(end - start + 1).ToArray();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"File: {relativePath} ({total} lines total)");
            if (capped)
                sb.AppendLine($"[Auto-capped: showing lines 1-{end + 1} of {total}. Use line_start/line_end for a range, or full=true for everything.]");
            else if (lineStart.HasValue || lineEnd.HasValue)
                sb.AppendLine($"Showing lines {start + 1}-{end + 1}");
            sb.AppendLine("---");

            for (int i = 0; i < selectedLines.Length; i++)
                sb.AppendLine($"{start + i + 1,5} | {selectedLines[i]}");

            return sb.ToString();
        }
    }
}
