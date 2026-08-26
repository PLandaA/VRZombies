using System;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace LazyRay.Core
{
    /// <summary>
    /// LazyRay status indicator in the main Unity editor toolbar (Unity 6.3+ API).
    /// Shows: filled dot = awake, hollow dot = asleep, half dot = reconnecting.
    /// Click opens a dropdown menu with server controls.
    /// </summary>
    [InitializeOnLoad]
    public static class LazyRayToolbar
    {
        private const string ELEMENT_PATH = "LazyRay/Status";

        private static ServerState _lastState = ServerState.Unknown;
        private static double _lastRefreshTime;
        private const double REFRESH_INTERVAL = 0.5;

        private enum ServerState { Unknown, Awake, Asleep, Reconnecting }

        static LazyRayToolbar()
        {
            EditorApplication.update += PeriodicRefresh;
        }

        [MainToolbarElement(ELEMENT_PATH, defaultDockPosition = MainToolbarDockPosition.Right)]
        public static MainToolbarElement CreateStatusButton()
        {
            var state = GetCurrentState();
            var label = state switch
            {
                ServerState.Awake        => "LazyRay \ud83d\udfe2",
                ServerState.Reconnecting => "LazyRay \ud83d\udfe1",
                _                        => "LazyRay \ud83d\udd34"
            };

            // v6.6: instance tint badge so you can tell at a glance which
            // Unity (and which matching Claude instance) this is.
            string badge = TintOverlay.ColorSquareEmoji();
            if (badge != null) label = badge + " " + label;

            var tooltip = GetTooltipForState(state);
            string hex = LazyRaySettings.Instance.InstanceColorHex;
            if (hex != null) tooltip += $"\nInstance tint: {hex}";

            var content = new MainToolbarContent(label, tooltip);
            return new MainToolbarDropdown(content, ShowMenu);
        }

        private static void PeriodicRefresh()
        {
            if (EditorApplication.timeSinceStartup - _lastRefreshTime < REFRESH_INTERVAL)
                return;
            _lastRefreshTime = EditorApplication.timeSinceStartup;

            var current = GetCurrentState();
            if (current != _lastState)
            {
                _lastState = current;
                MainToolbar.Refresh(ELEMENT_PATH);
            }
        }

        private static ServerState GetCurrentState()
        {
            bool compiling = CompilationTracker.IsCompiling;
            if (compiling) return ServerState.Reconnecting;
            return McpHttpServer.IsRunning ? ServerState.Awake : ServerState.Asleep;
        }

        private static string GetTooltipForState(ServerState state)
        {
            string tooltip = state switch
            {
                ServerState.Awake => $"LazyRay Awake (pipe: {McpHttpServer.PipeName})",
                ServerState.Asleep => "LazyRay Asleep (server stopped)",
                ServerState.Reconnecting => "LazyRay Reconnecting (compiling)...",
                _ => "LazyRay"
            };

            if (McpHttpServer.IsRunning && McpHttpServer.RequestCount > 0)
            {
                var elapsed = DateTime.Now - McpHttpServer.LastActivityTime;
                string ago = elapsed.TotalSeconds < 60
                    ? $"{(int)elapsed.TotalSeconds}s ago"
                    : elapsed.TotalMinutes < 60
                        ? $"{(int)elapsed.TotalMinutes}m ago"
                        : $"{(int)elapsed.TotalHours}h ago";
                tooltip += $"\n{McpHttpServer.RequestCount} requests | last: {McpHttpServer.LastToolName} ({ago})";
            }

            return tooltip;
        }

        private static void ShowMenu(Rect buttonRect)
        {
            var menu = new GenericMenu();

            if (McpHttpServer.IsRunning)
            {
                menu.AddDisabledItem(new GUIContent("Status: Awake"));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Restart Server"), false, () =>
                {
                    McpHttpServer.RestartServer();
                    MainToolbar.Refresh(ELEMENT_PATH);
                });
                menu.AddItem(new GUIContent("Stop Server"), false, () =>
                {
                    McpHttpServer.StopServer();
                    MainToolbar.Refresh(ELEMENT_PATH);
                });
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Status: Asleep"));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Start Server"), false, () =>
                {
                    McpHttpServer.StartServer();
                    MainToolbar.Refresh(ELEMENT_PATH);
                });
            }

            // v6.6.1: instance tint picker — match your Claude instance color.
            menu.AddSeparator("");
            string currentHex = null;
            try { currentHex = LazyRaySettings.Instance.InstanceColorHex; } catch { }
            foreach (var (name, color) in LazyRay.Tools.SetInstanceColorTool.Presets)
            {
                string hex = "#" + ColorUtility.ToHtmlStringRGB(color);
                bool active = currentHex != null &&
                              string.Equals(currentHex, hex, StringComparison.OrdinalIgnoreCase);
                string label = char.ToUpperInvariant(name[0]) + name.Substring(1);
                menu.AddItem(new GUIContent($"Instance Tint/{label}"), active, () =>
                {
                    LazyRay.Tools.SetInstanceColorTool.Apply(name);
                    MainToolbar.Refresh(ELEMENT_PATH);
                });
            }
            menu.AddItem(new GUIContent("Instance Tint/Off"), currentHex == null, () =>
            {
                LazyRay.Tools.SetInstanceColorTool.Apply("off");
                MainToolbar.Refresh(ELEMENT_PATH);
            });

            menu.AddSeparator("");
            menu.AddDisabledItem(new GUIContent($"Pipe: {McpHttpServer.PipeName}"));

            if (McpHttpServer.RequestCount > 0)
                menu.AddDisabledItem(new GUIContent($"Requests: {McpHttpServer.RequestCount}"));

            menu.DropDown(buttonRect);
        }
    }
}
