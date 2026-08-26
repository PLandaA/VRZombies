using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LazyRay.Tools
{
    public class SearchFilesTool : IMcpTool
    {
        public string Name => "search_files";
        public string Description =>
            "Search for files by name (supports * wildcards), content, or extension. Searches within Assets/ by default.";

        public bool IsDestructive => false;

        // v6.4: pure file I/O — safe to run on the pipe thread (fast path).
        public bool IsThreadSafe => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""query"": { ""type"": ""string"", ""description"": ""Filename search query (supports * wildcards)"" },
                ""content_search"": { ""type"": ""string"", ""description"": ""Search inside file contents for this string"" },
                ""extensions"": { ""type"": ""string"", ""description"": ""Comma-separated extensions to filter (e.g. '.cs,.shader')"" },
                ""path"": { ""type"": ""string"", ""description"": ""Directory to search in (default 'Assets')"" },
                ""max_results"": { ""type"": ""integer"", ""description"": ""Max results (default 20)"" }
            },
            ""required"": []
        }");

        public string Execute(JObject input)
        {
            string query = input.Value<string>("query");
            string contentSearch = input.Value<string>("content_search");

            // Handle extensions: accept both comma-separated string and JArray
            string[] extensions = null;
            var extToken = input["extensions"];
            if (extToken != null)
            {
                if (extToken.Type == JTokenType.Array)
                    extensions = extToken.Select(t => t.ToString().Trim()).ToArray();
                else if (extToken.Type == JTokenType.String)
                {
                    string raw = extToken.ToString();
                    if (!string.IsNullOrEmpty(raw))
                        extensions = raw.Split(',').Select(e => e.Trim()).ToArray();
                }
            }
            string relativePath = input.Value<string>("path") ?? "Assets";
            int maxResults = Mathf.Clamp(input.Value<int?>("max_results") ?? 20, 1, 50);

            bool hasNameQuery = !string.IsNullOrWhiteSpace(query);
            bool hasContentSearch = !string.IsNullOrWhiteSpace(contentSearch);
            bool hasExtFilter = extensions != null && extensions.Length > 0;

            if (!hasNameQuery && !hasContentSearch && !hasExtFilter)
                return "ERROR: Provide at least one: 'query', 'content_search', or 'extensions'.";

            string projectRoot = LazyRay.Core.LazyRayPaths.ProjectRoot; // v6.4: cached — no main-thread API
            string searchPath = Path.Combine(projectRoot, relativePath);

            if (!Directory.Exists(searchPath))
                return $"ERROR: Directory not found: {relativePath}";

            var results = new List<(string path, long size, string matchContext)>();

            try
            {
                var allFiles = Directory.GetFiles(searchPath, "*", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".") && !f.EndsWith(".meta"));

                if (hasExtFilter)
                {
                    var extSet = new HashSet<string>(extensions.Select(e => e.StartsWith(".") ? e.ToLower() : "." + e.ToLower()));
                    allFiles = allFiles.Where(f => extSet.Contains(Path.GetExtension(f).ToLower()));
                }

                Regex namePattern = null;
                if (hasNameQuery)
                    namePattern = WildcardToRegex(query);

                foreach (var file in allFiles)
                {
                    if (results.Count >= maxResults && !hasContentSearch) break;

                    if (namePattern != null && !namePattern.IsMatch(Path.GetFileName(file)))
                        continue;

                    var info = new FileInfo(file);
                    string relPath = file.Substring(projectRoot.Length + 1).Replace('\\', '/');
                    string matchCtx = null;

                    if (hasContentSearch)
                    {
                        if (!IsTextFile(file)) continue;
                        try
                        {
                            string content = File.ReadAllText(file);
                            int idx = content.IndexOf(contentSearch, StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) continue;
                            int start = Math.Max(0, idx - 40);
                            int end = Math.Min(content.Length, idx + contentSearch.Length + 40);
                            matchCtx = "..." + content.Substring(start, end - start).Replace("\n", " ").Replace("\r", "") + "...";
                        }
                        catch { continue; }
                    }

                    results.Add((relPath, info.Length, matchCtx));
                    if (results.Count >= maxResults) break;
                }
            }
            catch (Exception ex) { return $"ERROR: Search failed: {ex.Message}"; }

            if (results.Count == 0)
            {
                var desc = new List<string>();
                if (hasNameQuery) desc.Add($"name matching '{query}'");
                if (hasContentSearch) desc.Add($"containing '{contentSearch}'");
                if (hasExtFilter) desc.Add($"with extensions [{string.Join(", ", extensions)}]");
                return $"No files found {string.Join(" and ", desc)} in {relativePath}";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Found {results.Count} file{(results.Count != 1 ? "s" : "")}:");
            foreach (var (path, size, matchCtx) in results)
            {
                sb.AppendLine($"  {path} ({FormatSize(size)})");
                if (matchCtx != null) sb.AppendLine($"     -> {matchCtx}");
            }
            return sb.ToString();
        }

        private Regex WildcardToRegex(string pattern)
        {
            if (!pattern.Contains("*") && !pattern.Contains("?"))
                return new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled);
            string escaped = Regex.Escape(pattern);
            string regexPattern = "^" + escaped.Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        private bool IsTextFile(string path)
        {
            var ext = Path.GetExtension(path).ToLower();
            return ext is ".cs" or ".shader" or ".hlsl" or ".cginc" or ".compute"
                or ".json" or ".xml" or ".yaml" or ".yml" or ".txt" or ".md"
                or ".asmdef" or ".asmref" or ".css" or ".html" or ".js" or ".cfg" or ".ini" or ".csv";
        }

        private string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
        };
    }
}
