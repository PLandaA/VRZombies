using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace LazyRay.Tools
{
    /// <summary>
    /// v6.5: tracker for files written with edit_file(defer_compile=true).
    /// Thread-safe (edit_file runs on the main thread, but the count is
    /// read from result strings that may be built concurrently in future).
    /// Wiped by domain reloads — harmless: a stray reload (e.g. Unity
    /// auto-refresh on focus) compiles the pending files anyway, and the
    /// subsequent commit_edits becomes a cheap no-op Refresh.
    /// </summary>
    public static class DeferredEdits
    {
        // v6.6: keyed by client (Claude instance PID sent by the bridge),
        // so two Claude instances batching on the SAME project can't drain
        // each other's pending lists. NOTE: separation is for attribution —
        // AssetDatabase.Refresh() still imports every changed file on disk
        // regardless of whose list it was on.
        private static readonly Dictionary<string, List<string>> _pending = new();
        private static readonly object _lock = new object();

        public static void Add(string client, string relPath)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(client, out var list))
                    _pending[client] = list = new List<string>();
                if (!list.Contains(relPath))
                    list.Add(relPath);
            }
        }

        public static string[] Drain(string client)
        {
            lock (_lock)
            {
                if (!_pending.TryGetValue(client, out var list))
                    return System.Array.Empty<string>();
                var arr = list.ToArray();
                _pending.Remove(client);
                return arr;
            }
        }

        public static int Count(string client)
        {
            lock (_lock)
                return _pending.TryGetValue(client, out var l) ? l.Count : 0;
        }

        public static int TotalCount
        {
            get
            {
                lock (_lock)
                {
                    int n = 0;
                    foreach (var l in _pending.Values) n += l.Count;
                    return n;
                }
            }
        }
    }

    /// <summary>
    /// v6.5: commits a batch of deferred edits with a SINGLE
    /// AssetDatabase.Refresh() — one compile + one domain reload for the
    /// whole batch, instead of one per file. In this project a domain
    /// reload costs ~20s, so every file batched beyond the first saves a
    /// full reload.
    /// </summary>
    public class CommitEditsTool : IMcpTool
    {
        public string Name => "commit_edits";

        public string Description =>
            "Commit all pending edit_file(defer_compile=true) writes with a SINGLE import/compile/domain-reload cycle. " +
            "Call this once after the last deferred edit of a batch. " +
            "The bridge waits for the compilation to complete, exactly like a normal .cs edit.";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {},
            ""required"": []
        }");

        public string Execute(JObject input)
        {
            string client = input.Value<string>("__client") ?? "legacy";
            string[] files = DeferredEdits.Drain(client);
            int others = DeferredEdits.TotalCount;

            AssetDatabase.Refresh();

            string othersNote = others > 0
                ? $"\nNote: {others} deferred edit(s) from other Claude instances remain pending on their lists " +
                  "(this Refresh already imported their on-disk changes; their commit_edits will be a cheap no-op)."
                : "";

            if (files.Length == 0)
                return "No deferred edits were pending for this instance. AssetDatabase.Refresh() triggered anyway " +
                       "(no-op if nothing changed on disk)." + othersNote;

            return $"Committed {files.Length} deferred edit(s) with a single Refresh:\n- " +
                   string.Join("\n- ", files) +
                   "\nCompilation will follow for code files." + othersNote;
        }
    }
}
