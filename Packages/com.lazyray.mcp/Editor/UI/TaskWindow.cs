using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace LazyRay.UI
{
    public class TaskWindow : EditorWindow
    {
        // ─── Data Model (matches TaskSystemTool) ───────────────────────
        [Serializable] private class TaskData { public int next_id = 1; public List<TaskItem> tasks = new(); public List<SessionLog> session_logs = new(); }
        [Serializable] private class TaskItem
        {
            public int id; public string title; public string description; public string category;
            public string priority; public string status; public string[] tags;
            public string created_at; public string updated_at; public string completed_at; public string scene_context;
        }
        [Serializable] private class SessionLog { public string timestamp; public string message; public string scene; }

        private static readonly string TaskFilePath = "Assets/LazyRayData/tasks.json";

        // ─── UI State ──────────────────────────────────────────────────
        private TaskData _data;
        private Vector2 _scrollPos;
        private bool _showAddForm;
        private string _newTitle = "";
        private string _newDescription = "";
        private int _newCategoryIdx;
        private int _newPriorityIdx = 2; // medium
        private int _filterStatusIdx; // 0=active, 1=all, 2=open, 3=in_progress, 4=done
        private int _filterCategoryIdx; // 0=all
        private string _searchText = "";
        private DateTime _lastLoad;

        private static readonly string[] Categories = { "bug", "optimization", "feature", "note", "cleanup" };
        private static readonly string[] CatIcons = { "🐛", "⚡", "✨", "📝", "🧹" };
        private static readonly string[] Priorities = { "critical", "high", "medium", "low" };
        private static readonly string[] PriColors = { "#FF4444", "#FF8800", "#FFCC00", "#44CC44" };
        private static readonly string[] StatusFilters = { "Active", "All", "Pending", "In Progress", "Done" };
        private static readonly string[] CategoryFilters = { "All", "🐛 Bug", "⚡ Optimization", "✨ Feature", "📝 Note", "🧹 Cleanup" };

        [MenuItem("Tools/LazyRay/Task Manager")]
        public static void ShowWindow()
        {
            var w = GetWindow<TaskWindow>("LazyRay Tasks");
            w.minSize = new Vector2(400, 300);
        }

        private void OnEnable() => LoadData();
        private void OnFocus() => LoadData();

        private void LoadData()
        {
            if (!File.Exists(TaskFilePath)) { _data = new TaskData(); return; }
            try { _data = JsonConvert.DeserializeObject<TaskData>(File.ReadAllText(TaskFilePath)) ?? new TaskData(); }
            catch { _data = new TaskData(); }
            _lastLoad = DateTime.Now;
        }

        private void SaveData()
        {
            var dir = Path.GetDirectoryName(TaskFilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(TaskFilePath, JsonConvert.SerializeObject(_data, Formatting.Indented));
        }

        // ─── GUI ───────────────────────────────────────────────────────

        private void OnGUI()
        {
            // Auto-refresh every 5s
            if ((DateTime.Now - _lastLoad).TotalSeconds > 5) LoadData();

            DrawToolbar();
            DrawFilters();

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

            if (_showAddForm) DrawAddForm();
            DrawTaskList();

            EditorGUILayout.EndScrollView();

            DrawStatusBar();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("＋ Add Task", EditorStyles.toolbarButton, GUILayout.Width(80)))
                _showAddForm = !_showAddForm;

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("↻ Refresh", EditorStyles.toolbarButton, GUILayout.Width(65)))
                LoadData();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilters()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Status:", GUILayout.Width(45));
            _filterStatusIdx = EditorGUILayout.Popup(_filterStatusIdx, StatusFilters, GUILayout.Width(90));
            EditorGUILayout.LabelField("Category:", GUILayout.Width(60));
            _filterCategoryIdx = EditorGUILayout.Popup(_filterCategoryIdx, CategoryFilters, GUILayout.Width(110));
            _searchText = EditorGUILayout.TextField(_searchText);
            if (!string.IsNullOrEmpty(_searchText) && GUILayout.Button("✕", GUILayout.Width(22)))
                _searchText = "";
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(2);
        }

        private void DrawAddForm()
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("New Task", EditorStyles.boldLabel);

            _newTitle = EditorGUILayout.TextField("Title", _newTitle);
            EditorGUILayout.LabelField("Description");
            _newDescription = EditorGUILayout.TextArea(_newDescription, GUILayout.Height(40));

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Category", GUILayout.Width(60));
            _newCategoryIdx = EditorGUILayout.Popup(_newCategoryIdx, Categories, GUILayout.Width(100));
            EditorGUILayout.LabelField("Priority", GUILayout.Width(50));
            _newPriorityIdx = EditorGUILayout.Popup(_newPriorityIdx, Priorities, GUILayout.Width(80));

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Cancel", GUILayout.Width(55)))
            {
                _showAddForm = false;
                _newTitle = ""; _newDescription = "";
            }

            GUI.enabled = !string.IsNullOrEmpty(_newTitle);
            if (GUILayout.Button("Add", GUILayout.Width(45)))
            {
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                _data.tasks.Add(new TaskItem
                {
                    id = _data.next_id++,
                    title = _newTitle,
                    description = _newDescription,
                    category = Categories[_newCategoryIdx],
                    priority = Priorities[_newPriorityIdx],
                    status = "pending",
                    created_at = now,
                    updated_at = now,
                    scene_context = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                });
                SaveData();
                _newTitle = ""; _newDescription = "";
                _showAddForm = false;
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            GUILayout.Space(4);
        }

        private void DrawTaskList()
        {
            if (_data == null || _data.tasks.Count == 0)
            {
                EditorGUILayout.HelpBox("No tasks yet. Click '＋ Add Task' or ask Claude to add one.", MessageType.Info);
                return;
            }

            var tasks = FilterTasks();

            if (tasks.Count == 0)
            {
                EditorGUILayout.HelpBox("No tasks match current filters.", MessageType.Info);
                return;
            }

            // Sort: pending/in_progress first, then by priority
            tasks = tasks.OrderBy(t => t.status == "done" ? 1 : 0)
                         .ThenByDescending(t => PriorityWeight(t.priority))
                         .ToList();

            foreach (var task in tasks)
                DrawTaskItem(task);
        }

        private void DrawTaskItem(TaskItem task)
        {
            bool isDone = task.status == "done";
            var bgColor = GUI.backgroundColor;

            if (isDone) GUI.backgroundColor = new Color(0.7f, 0.7f, 0.7f, 0.5f);
            else if (task.priority == "critical") GUI.backgroundColor = new Color(1f, 0.85f, 0.85f);
            else if (task.priority == "high") GUI.backgroundColor = new Color(1f, 0.92f, 0.8f);

            EditorGUILayout.BeginVertical("box");
            GUI.backgroundColor = bgColor;

            // Header row
            EditorGUILayout.BeginHorizontal();

            // Checkbox
            bool newDone = EditorGUILayout.Toggle(isDone, GUILayout.Width(18));
            if (newDone != isDone)
            {
                task.status = newDone ? "done" : "pending";
                task.updated_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                if (newDone) task.completed_at = task.updated_at;
                SaveData();
            }

            // Priority dot + category icon
            string priIcon = task.priority switch { "critical" => "🔴", "high" => "🟠", "medium" => "🟡", "low" => "🟢", _ => "⚪" };
            int catIdx = Array.IndexOf(Categories, task.category);
            string catIcon = catIdx >= 0 ? CatIcons[catIdx] : "•";

            var titleStyle = new GUIStyle(EditorStyles.label) { richText = true, fontStyle = isDone ? FontStyle.Italic : FontStyle.Bold };
            string titleText = isDone ? $"<color=#888888><s>{task.title}</s></color>" : task.title;
            EditorGUILayout.LabelField($"{priIcon}{catIcon} #{task.id} {titleText}", titleStyle);

            // Status dropdown
            string[] statuses = { "pending", "in_progress", "done" };
            int currentStatus = Array.IndexOf(statuses, task.status);
            if (currentStatus < 0) currentStatus = 0;
            int newStatus = EditorGUILayout.Popup(currentStatus, statuses, GUILayout.Width(85));
            if (newStatus != currentStatus)
            {
                task.status = statuses[newStatus];
                task.updated_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                if (task.status == "done") task.completed_at = task.updated_at;
                SaveData();
            }

            // Delete button
            if (GUILayout.Button("✕", GUILayout.Width(22)))
            {
                if (EditorUtility.DisplayDialog("Delete Task", $"Delete task #{task.id}: {task.title}?", "Delete", "Cancel"))
                {
                    _data.tasks.Remove(task);
                    SaveData();
                    GUIUtility.ExitGUI();
                }
            }

            EditorGUILayout.EndHorizontal();

            // Description (if any)
            if (!string.IsNullOrEmpty(task.description))
            {
                var descStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { padding = new RectOffset(22, 4, 0, 2) };
                if (isDone) descStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                EditorGUILayout.LabelField(task.description, descStyle);
            }

            // Meta line
            var metaParts = new List<string>();
            metaParts.Add(task.category);
            metaParts.Add(task.priority);
            if (!string.IsNullOrEmpty(task.scene_context)) metaParts.Add($"scene: {task.scene_context}");
            metaParts.Add(task.created_at);

            var metaStyle = new GUIStyle(EditorStyles.miniLabel) { padding = new RectOffset(22, 4, 0, 2), normal = { textColor = new Color(0.5f, 0.5f, 0.5f) } };
            EditorGUILayout.LabelField(string.Join(" · ", metaParts), metaStyle);

            EditorGUILayout.EndVertical();
            GUILayout.Space(1);
        }

        private void DrawStatusBar()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            int open = _data?.tasks.Count(t => t.status == "pending") ?? 0;
            int prog = _data?.tasks.Count(t => t.status == "in_progress") ?? 0;
            int done = _data?.tasks.Count(t => t.status == "done") ?? 0;

            GUILayout.Label($"📋 {open} pending  🔧 {prog} in progress  ✅ {done} done", EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            if (done > 0 && GUILayout.Button("Clear Done", EditorStyles.toolbarButton, GUILayout.Width(75)))
            {
                _data.tasks.RemoveAll(t => t.status == "done");
                SaveData();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ─── Helpers ───────────────────────────────────────────────────

        private List<TaskItem> FilterTasks()
        {
            var tasks = _data.tasks.AsEnumerable();

            // Status filter
            switch (_filterStatusIdx)
            {
                case 0: tasks = tasks.Where(t => t.status != "done"); break; // Active
                case 1: break; // All
                case 2: tasks = tasks.Where(t => t.status == "pending"); break;
                case 3: tasks = tasks.Where(t => t.status == "in_progress"); break;
                case 4: tasks = tasks.Where(t => t.status == "done"); break;
            }

            // Category filter
            if (_filterCategoryIdx > 0)
            {
                string cat = Categories[_filterCategoryIdx - 1];
                tasks = tasks.Where(t => t.category == cat);
            }

            // Search
            if (!string.IsNullOrEmpty(_searchText))
            {
                string search = _searchText.ToLower();
                tasks = tasks.Where(t =>
                    (t.title?.ToLower().Contains(search) ?? false) ||
                    (t.description?.ToLower().Contains(search) ?? false));
            }

            return tasks.ToList();
        }

        private int PriorityWeight(string p) => p switch { "critical" => 4, "high" => 3, "medium" => 2, "low" => 1, _ => 0 };
    }
}
