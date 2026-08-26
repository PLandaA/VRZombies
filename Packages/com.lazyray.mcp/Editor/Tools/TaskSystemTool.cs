using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    /// <summary>
    /// Persistent task/todo tracking system.
    /// Tasks survive between sessions, stored in Assets/LazyRayData/tasks.json.
    /// Categories: bug, optimization, feature, note, cleanup.
    /// Priorities: critical, high, medium, low.
    /// </summary>
    public class TaskSystemTool : IMcpTool
    {
        public string Name => "tasks";
        public string Description =>
            "Persistent task/todo system for project tracking.\n" +
            "Actions:\n" +
            "- list: Show all tasks (filter by status/category/priority)\n" +
            "- add: Add a new task\n" +
            "- update: Update task fields (title, description, category, priority, status)\n" +
            "- complete: Mark task as done\n" +
            "- remove: Delete a task\n" +
            "- clear_done: Remove all completed tasks\n" +
            "- summary: Quick stats overview\n" +
            "- log: Add a session log entry (what was done today)";

        public bool IsDestructive => false;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""description"": ""Action: list, add, update, complete, remove, clear_done, summary, log"" },
                ""id"": { ""type"": ""integer"", ""description"": ""Task ID for update/complete/remove"" },
                ""title"": { ""type"": ""string"", ""description"": ""Task title (for add/update)"" },
                ""description"": { ""type"": ""string"", ""description"": ""Detailed description (for add/update)"" },
                ""category"": { ""type"": ""string"", ""description"": ""Category: bug, optimization, feature, note, cleanup"" },
                ""priority"": { ""type"": ""string"", ""description"": ""Priority: critical, high, medium, low"" },
                ""status"": { ""type"": ""string"", ""description"": ""Filter by status: pending, in_progress, done, all (default: pending+in_progress)"" },
                ""message"": { ""type"": ""string"", ""description"": ""Log message (for log action)"" }
            },
            ""required"": [""action""]
        }");

        private static readonly string TaskFilePath = "Assets/LazyRayData/tasks.json";

        public string Execute(JObject input)
        {
            string action = input.Value<string>("action");

            try
            {
                return action switch
                {
                    "list" => DoList(input),
                    "add" => DoAdd(input),
                    "update" => DoUpdate(input),
                    "complete" => DoComplete(input),
                    "remove" => DoRemove(input),
                    "clear_done" => DoClearDone(),
                    "summary" => DoSummary(),
                    "log" => DoLog(input),
                    _ => $"ERROR: Unknown action '{action}'"
                };
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        // ─── Data Model ────────────────────────────────────────────────

        private class TaskData
        {
            public int next_id = 1;
            public List<TaskItem> tasks = new();
            public List<LogEntry> session_log = new();
        }

        private class TaskItem
        {
            public int id;
            public string title;
            public string description;
            public string category; // bug, optimization, feature, note, cleanup
            public string priority; // critical, high, medium, low
            public string status;   // pending, in_progress, done
            public string created;
            public string completed;
            public string scene;    // which scene it relates to
        }

        private class LogEntry
        {
            public string date;
            public string message;
        }

        // ─── File I/O ──────────────────────────────────────────────────

        private TaskData LoadTasks()
        {
            if (!File.Exists(TaskFilePath))
                return new TaskData();

            try
            {
                string json = File.ReadAllText(TaskFilePath);
                return JsonConvert.DeserializeObject<TaskData>(json) ?? new TaskData();
            }
            catch
            {
                return new TaskData();
            }
        }

        private void SaveTasks(TaskData data)
        {
            string dir = Path.GetDirectoryName(TaskFilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            File.WriteAllText(TaskFilePath, json);
        }

        // ─── Actions ───────────────────────────────────────────────────

        private string DoList(JObject input)
        {
            var data = LoadTasks();
            if (data.tasks.Count == 0) return "No tasks yet. Use action='add' to create one.";

            string statusFilter = input.Value<string>("status") ?? "active";
            string catFilter = input.Value<string>("category");
            string prioFilter = input.Value<string>("priority");

            var filtered = data.tasks.AsEnumerable();

            if (statusFilter == "active")
                filtered = filtered.Where(t => t.status != "done");
            else if (statusFilter != "all")
                filtered = filtered.Where(t => t.status == statusFilter);

            if (!string.IsNullOrEmpty(catFilter))
                filtered = filtered.Where(t => t.category == catFilter);
            if (!string.IsNullOrEmpty(prioFilter))
                filtered = filtered.Where(t => t.priority == prioFilter);

            var tasks = filtered.ToList();
            if (tasks.Count == 0) return "No tasks match the filter.";

            // Sort: critical first, then high, then by status (in_progress before pending)
            var priorityOrder = new Dictionary<string, int>
            {
                {"critical", 0}, {"high", 1}, {"medium", 2}, {"low", 3}
            };
            var statusOrder = new Dictionary<string, int>
            {
                {"in_progress", 0}, {"pending", 1}, {"done", 2}
            };

            tasks = tasks
                .OrderBy(t => statusOrder.GetValueOrDefault(t.status, 9))
                .ThenBy(t => priorityOrder.GetValueOrDefault(t.priority, 9))
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"═══ Tasks ({tasks.Count}) ═══\n");

            foreach (var t in tasks)
            {
                string icon = t.status switch
                {
                    "done" => "✅",
                    "in_progress" => "🔄",
                    _ => t.priority switch
                    {
                        "critical" => "🔴",
                        "high" => "🟠",
                        "medium" => "🟡",
                        _ => "⚪"
                    }
                };

                sb.AppendLine($"  {icon} [{t.id}] {t.title}");
                sb.AppendLine($"     {t.category} | {t.priority} | {t.status} | {t.created}");
                if (!string.IsNullOrEmpty(t.description))
                    sb.AppendLine($"     {t.description}");
                if (!string.IsNullOrEmpty(t.scene))
                    sb.AppendLine($"     Scene: {t.scene}");
                if (t.status == "done" && !string.IsNullOrEmpty(t.completed))
                    sb.AppendLine($"     Completed: {t.completed}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string DoAdd(JObject input)
        {
            string title = input.Value<string>("title");
            if (string.IsNullOrEmpty(title)) return "ERROR: 'title' required";

            var data = LoadTasks();
            var task = new TaskItem
            {
                id = data.next_id++,
                title = title,
                description = input.Value<string>("description") ?? "",
                category = input.Value<string>("category") ?? "feature",
                priority = input.Value<string>("priority") ?? "medium",
                status = "pending",
                created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
            };

            data.tasks.Add(task);
            SaveTasks(data);

            string icon = task.priority switch
            {
                "critical" => "🔴",
                "high" => "🟠",
                "medium" => "🟡",
                _ => "⚪"
            };

            return $"{icon} Task #{task.id} added: {task.title}\n  Category: {task.category} | Priority: {task.priority}";
        }

        private string DoUpdate(JObject input)
        {
            int? id = input.Value<int?>("id");
            if (id == null) return "ERROR: 'id' required";

            var data = LoadTasks();
            var task = data.tasks.FirstOrDefault(t => t.id == id.Value);
            if (task == null) return $"ERROR: Task #{id} not found";

            if (input["title"] != null) task.title = input.Value<string>("title");
            if (input["description"] != null) task.description = input.Value<string>("description");
            if (input["category"] != null) task.category = input.Value<string>("category");
            if (input["priority"] != null) task.priority = input.Value<string>("priority");
            if (input["status"] != null) task.status = input.Value<string>("status");

            if (task.status == "done" && string.IsNullOrEmpty(task.completed))
                task.completed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            SaveTasks(data);
            return $"✅ Task #{id} updated: {task.title} ({task.status})";
        }

        private string DoComplete(JObject input)
        {
            int? id = input.Value<int?>("id");
            if (id == null) return "ERROR: 'id' required";

            var data = LoadTasks();
            var task = data.tasks.FirstOrDefault(t => t.id == id.Value);
            if (task == null) return $"ERROR: Task #{id} not found";

            task.status = "done";
            task.completed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            SaveTasks(data);
            return $"✅ Task #{id} completed: {task.title}";
        }

        private string DoRemove(JObject input)
        {
            int? id = input.Value<int?>("id");
            if (id == null) return "ERROR: 'id' required";

            var data = LoadTasks();
            int removed = data.tasks.RemoveAll(t => t.id == id.Value);
            if (removed == 0) return $"ERROR: Task #{id} not found";

            SaveTasks(data);
            return $"🗑 Task #{id} removed";
        }

        private string DoClearDone()
        {
            var data = LoadTasks();
            int count = data.tasks.RemoveAll(t => t.status == "done");
            SaveTasks(data);
            return $"🗑 Cleared {count} completed tasks";
        }

        private string DoSummary()
        {
            var data = LoadTasks();
            if (data.tasks.Count == 0 && data.session_log.Count == 0)
                return "No tasks or logs yet.";

            var sb = new StringBuilder();
            sb.AppendLine("═══ Project Summary ═══\n");

            // Task stats
            var byStatus = data.tasks.GroupBy(t => t.status);
            int pending = data.tasks.Count(t => t.status == "pending");
            int inProgress = data.tasks.Count(t => t.status == "in_progress");
            int done = data.tasks.Count(t => t.status == "done");
            int critical = data.tasks.Count(t => t.priority == "critical" && t.status != "done");

            sb.AppendLine($"  Tasks: {data.tasks.Count} total");
            sb.AppendLine($"    🔄 In Progress: {inProgress}");
            sb.AppendLine($"    ⏳ Pending: {pending}");
            sb.AppendLine($"    ✅ Done: {done}");
            if (critical > 0) sb.AppendLine($"    🔴 Critical (open): {critical}");

            // By category
            var byCat = data.tasks.Where(t => t.status != "done")
                .GroupBy(t => t.category)
                .OrderByDescending(g => g.Count());
            if (byCat.Any())
            {
                sb.AppendLine($"\n  Open by category:");
                foreach (var g in byCat)
                    sb.AppendLine($"    {g.Key}: {g.Count()}");
            }

            // Recent logs
            if (data.session_log.Count > 0)
            {
                sb.AppendLine($"\n  ── Recent Session Log ──");
                foreach (var log in data.session_log.TakeLast(10))
                    sb.AppendLine($"    [{log.date}] {log.message}");
            }

            return sb.ToString();
        }

        private string DoLog(JObject input)
        {
            string message = input.Value<string>("message");
            if (string.IsNullOrEmpty(message)) return "ERROR: 'message' required";

            var data = LoadTasks();
            data.session_log.Add(new LogEntry
            {
                date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                message = message
            });
            SaveTasks(data);
            return $"📝 Logged: {message}";
        }
    }
}
