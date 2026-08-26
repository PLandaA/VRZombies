using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace LazyRay.Tools
{
    public class FolderStructureTool : IMcpTool
    {
        public string Name => "view_folder_structure";
        public string Description =>
            "View the project's folder and file structure. " +
            "Returns a tree view of directories and files. " +
            "Use 'path' to focus on a specific subdirectory (relative to Assets/). " +
            "Use 'max_depth' to control depth (default 3). " +
            "Use 'include_files' to show files (default true).";

        public bool IsDestructive => false;

        // v6.4: pure file I/O — safe to run on the pipe thread (fast path).
        public bool IsThreadSafe => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""path"": { ""type"": ""string"", ""description"": ""Relative path from project root. Default is 'Assets'. Use '.' for project root."" },
                ""max_depth"": { ""type"": ""integer"", ""description"": ""Maximum depth to scan (1-6, default 3)"" },
                ""include_files"": { ""type"": ""boolean"", ""description"": ""Whether to include files or only directories (default true)"" }
            },
            ""required"": []
        }");

        private readonly LazyRay.Core.LazyRaySettings _settings;

        public FolderStructureTool(LazyRay.Core.LazyRaySettings settings)
        {
            _settings = settings;
        }

        public string Execute(JObject input)
        {
            string relativePath = input.Value<string>("path") ?? "Assets";
            int maxDepth = input.Value<int?>("max_depth") ?? 3;
            bool includeFiles = input.Value<bool?>("include_files") ?? true;
            bool verbose = input.Value<bool?>("verbose") ?? false;

            maxDepth = Mathf.Clamp(maxDepth, 1, Math.Min(6, _settings.maxFolderScanDepth));

            string projectRoot = LazyRay.Core.LazyRayPaths.ProjectRoot; // v6.4: cached — no main-thread API
            string fullPath = Path.Combine(projectRoot, relativePath);

            if (!Directory.Exists(fullPath))
                return $"ERROR: Directory not found: {relativePath}";

            var sb = new StringBuilder();
            sb.AppendLine($"{relativePath}/");

            var excludeSet = new HashSet<string>(_settings.excludedFolders, StringComparer.OrdinalIgnoreCase);
            BuildTree(sb, fullPath, "", maxDepth, 0, includeFiles, excludeSet, verbose);
            return sb.ToString();
        }

        // v6.7 token diet: directories with more than MAX_ENTRIES entries
        // show the first MAX_ENTRIES and summarize the rest, unless verbose.
        private const int MAX_ENTRIES = 25;

        private void BuildTree(StringBuilder sb, string dirPath, string indent, int maxDepth, int currentDepth, bool includeFiles, HashSet<string> excludeSet, bool verbose)
        {
            if (currentDepth >= maxDepth) return;

            try
            {
                var dirs = Directory.GetDirectories(dirPath).Select(d => new DirectoryInfo(d))
                    .Where(d => !d.Name.StartsWith(".") && !excludeSet.Contains(d.Name)).OrderBy(d => d.Name).ToList();

                var files = includeFiles
                    ? Directory.GetFiles(dirPath).Select(f => new FileInfo(f))
                        .Where(f => !f.Name.StartsWith(".") && f.Extension != ".meta").OrderBy(f => f.Name).ToList()
                    : new List<FileInfo>();

                int totalEntries = dirs.Count + files.Count;
                int shown = verbose ? totalEntries : Math.Min(totalEntries, MAX_ENTRIES);
                int index = 0;

                foreach (var dir in dirs)
                {
                    if (index >= shown) break;
                    index++;
                    bool isLast = index == totalEntries;
                    sb.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}{dir.Name}/");
                    BuildTree(sb, dir.FullName, indent + (isLast ? "    " : "│   "), maxDepth, currentDepth + 1, includeFiles, excludeSet, verbose);
                }

                foreach (var file in files)
                {
                    if (index >= shown) break;
                    index++;
                    bool isLast = index == totalEntries;
                    sb.AppendLine($"{indent}{(isLast ? "└── " : "├── ")}{file.Name} ({FormatSize(file.Length)})");
                }

                if (shown < totalEntries)
                {
                    int hiddenDirs = Math.Max(0, dirs.Count - shown);
                    int hiddenFiles = totalEntries - shown - hiddenDirs;
                    sb.AppendLine($"{indent}└── … +{totalEntries - shown} more ({hiddenDirs} dirs, {hiddenFiles} files — verbose:true to expand)");
                }
            }
            catch (UnauthorizedAccessException) { sb.AppendLine($"{indent}└── [Access Denied]"); }
        }

        private string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
        };
    }
}
