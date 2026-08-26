using System.Collections.Generic;
using System.Linq;
using LazyRay.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Core
{
    public class ToolDefinition
    {
        public string name;
        public string description;
        public JObject input_schema;
    }

    public class ToolExecutor
    {
        private readonly Dictionary<string, IMcpTool> _tools = new();
        private readonly LazyRaySettings _settings;
        private static int _currentUndoGroup = -1;
        private static string _currentUndoGroupName = null;

        public ToolExecutor(LazyRaySettings settings)
        {
            _settings = settings;
            RegisterDefaultTools();
        }

        private void RegisterDefaultTools()
        {
            Register(new FolderStructureTool(_settings));
            Register(new ScriptReadTool());
            Register(new ScriptEditTool());
            Register(new ValidateScriptTool());
            Register(new SearchFilesTool());
            Register(new DeleteFileTool());
            Register(new HierarchyViewTool());
            Register(new HierarchyEditTool());
            Register(new ConsoleLogTool(_settings.maxConsoleLogEntries));
            Register(new ExecuteCodeTool());
            Register(new ScreenCaptureTool());
            Register(new InspectorTool());
            Register(new ProjectInfoTool());
            Register(new PrefabBrowserTool());
            Register(new SceneNavigationTool());
            Register(new PlayModeControlTool());
            Register(new SceneManagementTool());
            Register(new AssetOperationsTool());
            Register(new TaskSystemTool());
            Register(new CommitEditsTool()); // v6.5: batch-edit commit
            Register(new SetInstanceColorTool()); // v6.6: instance tint
            Register(new SceneDesignTool()); // v6.8: environment design
            Register(new GameDesignTool()); // v6.9: hierarchy architecture
            Register(new CompareAssetsTool()); // v7.1: cross-project diff (fast path)
            Register(new SyncAssetsTool()); // v7.1: cross-project sync (dry-run default)
            Register(new ProjectSetupTool()); // v7.1: architecture spec check/apply
            Register(new SceneGraphTool()); // v7.3: static design-flow graph from scene YAML
        }

        public void Register(IMcpTool tool) => _tools[tool.Name] = tool;

        /// <summary>v6.4: whether a tool opted into the pipe-thread fast path.</summary>
        public bool IsThreadSafeTool(string toolName) =>
            _tools.TryGetValue(toolName, out var t) && t.IsThreadSafe;

        public List<ToolDefinition> GetToolDefinitions()
        {
            return _tools.Values.Select(tool => new ToolDefinition
            {
                name = tool.Name,
                description = tool.Description,
                input_schema = tool.InputSchema
            }).ToList();
        }

        public string ExecuteTool(string toolName, JObject input)
        {
            // Undo group support: virtual tools not in the registry
            if (toolName == "begin_undo_group")
            {
                string groupName = input?.Value<string>("name") ?? "LazyRay Batch";
                Undo.IncrementCurrentGroup();
                _currentUndoGroup = Undo.GetCurrentGroup();
                _currentUndoGroupName = groupName;
                Undo.SetCurrentGroupName(groupName);
                return $"Started undo group: '{groupName}' (id: {_currentUndoGroup})";
            }
            if (toolName == "end_undo_group")
            {
                if (_currentUndoGroup < 0) return "No active undo group";
                Undo.CollapseUndoOperations(_currentUndoGroup);
                string name = _currentUndoGroupName;
                _currentUndoGroup = -1;
                _currentUndoGroupName = null;
                return $"Collapsed undo group: '{name}'. All operations undoable with single Ctrl+Z.";
            }

            if (!_tools.TryGetValue(toolName, out var tool))
                return $"ERROR: Unknown tool '{toolName}'";

            if (tool.IsDestructive)
                Debug.Log($"[LazyRay] Executing: {DescribeToolCall(toolName, input)}");

            try
            {
                string result = tool.Execute(input);
                Debug.Log($"[LazyRay] Tool '{toolName}' executed successfully.");
                return result;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[LazyRay] Tool '{toolName}' failed: {ex}");
                return $"ERROR: Tool execution failed: {ex.Message}";
            }
        }

        private string DescribeToolCall(string toolName, JObject input)
        {
            return toolName switch
            {
                "edit_file" => input.Value<bool?>("create") == true
                    ? $"Create file: {input.Value<string>("path")}"
                    : $"Edit file: {input.Value<string>("path")}",
                "delete_file" => $"Delete file: {input.Value<string>("path")}",
                "edit_hierarchy" => $"Hierarchy {input.Value<string>("action")}: {input.Value<string>("path") ?? input.Value<string>("name") ?? ""}",
                "prefab_browser" => $"Prefab {input.Value<string>("action")}: {input.Value<string>("path") ?? ""}",
                _ => $"{toolName}"
            };
        }
    }
}
