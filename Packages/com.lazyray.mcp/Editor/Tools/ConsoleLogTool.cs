using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    /// <summary>
    /// Early subscription so we capture logs from [InitializeOnLoad] scripts.
    /// Must run before ToolExecutor creates the ConsoleLogTool instance.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsoleLogBootstrap
    {
        static ConsoleLogBootstrap()
        {
            ConsoleLogTool.StartListening();
        }
    }

    public class ConsoleLogTool : IMcpTool
    {
        public string Name => "view_console";
        public string Description =>
            "View recent Unity console log entries. " +
            "Use 'filter' to filter by log type: 'all', 'error', 'warning', 'log'. " +
            "Use 'search' to filter by text content. " +
            "Use 'count' to limit number of entries (default 30). " +
            "Use 'clear' to clear the captured log buffer.";

        public bool IsDestructive => false;

        // v6.5: buffer is lock-protected and fed by logMessageReceivedThreaded,
        // so reading it is safe from the pipe thread (fast path).
        public bool IsThreadSafe => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""filter"": { ""type"": ""string"", ""enum"": [""all"", ""error"", ""warning"", ""log""], ""description"": ""Filter by log type (default 'all')"" },
                ""search"": { ""type"": ""string"", ""description"": ""Text to search for in log messages"" },
                ""count"": { ""type"": ""integer"", ""description"": ""Max entries to return (default 30)"" },
                ""clear"": { ""type"": ""boolean"", ""description"": ""If true, clear the log buffer"" }
            },
            ""required"": []
        }");

        public struct LogEntry
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public DateTime timestamp;
        }

        private static readonly List<LogEntry> _logBuffer = new(500);
        private static readonly object _bufferLock = new object();
        private static bool _isListening;
        private readonly int _maxEntries;

        public ConsoleLogTool(int maxEntries = 500)
        {
            _maxEntries = maxEntries;
            StartListening();
        }

        public static void StartListening()
        {
            if (_isListening) return;
            // v6.5: threaded variant — captures logs from ALL threads (main
            // included) and makes our callback re-entrant, hence the lock.
            Application.logMessageReceivedThreaded += OnLogMessage;
            _isListening = true;
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (condition.StartsWith("[LazyRay]")) return;

            lock (_bufferLock)
            {
                _logBuffer.Add(new LogEntry { message = condition, stackTrace = stackTrace, type = type, timestamp = DateTime.Now });
                if (_logBuffer.Count > 500) _logBuffer.RemoveRange(0, _logBuffer.Count - 500);
            }
        }

        public string Execute(JObject input)
        {
            bool clear = input.Value<bool?>("clear") ?? false;
            if (clear)
            {
                lock (_bufferLock) { int c = _logBuffer.Count; _logBuffer.Clear(); return $"Cleared {c} log entries."; }
            }

            string filter = input.Value<string>("filter") ?? "all";
            string search = input.Value<string>("search");
            int maxCount = Mathf.Clamp(input.Value<int?>("count") ?? 30, 1, _maxEntries);

            // Snapshot under lock, then filter/format on the copy.
            List<LogEntry> snapshot;
            int totalCount;
            lock (_bufferLock) { snapshot = new List<LogEntry>(_logBuffer); totalCount = _logBuffer.Count; }
            IEnumerable<LogEntry> entries = snapshot;

            if (filter != "all")
            {
                LogType targetType = filter switch { "error" => LogType.Error, "warning" => LogType.Warning, _ => LogType.Log };
                entries = entries.Where(e => e.type == targetType || (filter == "error" && (e.type == LogType.Exception || e.type == LogType.Assert)));
            }

            if (!string.IsNullOrEmpty(search))
                entries = entries.Where(e => e.message.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || e.stackTrace.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

            var results = entries.TakeLast(maxCount).ToList();
            if (results.Count == 0) return $"No log entries found (buffer has {totalCount} total entries, filter: {filter}).";

            var sb = new StringBuilder();
            sb.AppendLine($"Console Log ({results.Count} entries, {totalCount} total)");
            sb.AppendLine(new string('-', 60));

            foreach (var entry in results)
            {
                string icon = entry.type switch { LogType.Error or LogType.Exception or LogType.Assert => "[ERR]", LogType.Warning => "[WRN]", _ => "[LOG]" };
                string time = entry.timestamp.ToString("HH:mm:ss");
                sb.AppendLine($"{icon} [{time}] {Truncate(entry.message, 300)}");
                if ((entry.type == LogType.Error || entry.type == LogType.Exception) && !string.IsNullOrEmpty(entry.stackTrace))
                    sb.AppendLine($"   Stack: {Truncate(entry.stackTrace, 200)}");
            }

            return sb.ToString();
        }

        private string Truncate(string msg, int max)
        {
            if (string.IsNullOrEmpty(msg)) return "";
            msg = msg.Replace("\n", " | ").Replace("\r", "");
            return msg.Length <= max ? msg : msg.Substring(0, max) + "...";
        }
    }
}
