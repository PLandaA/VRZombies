using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace LazyRay.Tools
{
    /// <summary>
    /// Control Unity's Play Mode: enter, exit, pause, step.
    /// Also provides scene save before play to prevent data loss.
    /// </summary>
    public class PlayModeControlTool : IMcpTool
    {
        public string Name => "play_mode";
        public string Description =>
            "Control Unity Play Mode.\n" +
            "Actions:\n" +
            "- play: Enter play mode (auto-saves scene first)\n" +
            "- stop: Exit play mode\n" +
            "- pause: Toggle pause\n" +
            "- step: Advance one frame (while paused)\n" +
            "- status: Get current play mode state";

        public bool IsDestructive => true;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""description"": ""Action: play, stop, pause, step, status"" },
                ""save_before_play"": { ""type"": ""boolean"", ""description"": ""Save scene before entering play mode (default: true)"" }
            },
            ""required"": [""action""]
        }");

        public string Execute(JObject input)
        {
            string action = input.Value<string>("action");

            try
            {
                return action switch
                {
                    "play" => DoPlay(input),
                    "stop" => DoStop(),
                    "pause" => DoPause(),
                    "step" => DoStep(),
                    "status" => DoStatus(),
                    _ => $"ERROR: Unknown action '{action}'. Use: play, stop, pause, step, status"
                };
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        private string DoPlay(JObject input)
        {
            if (EditorApplication.isPlaying)
                return "Already in Play Mode.";

            bool save = input.Value<bool?>("save_before_play") ?? true;
            if (save)
            {
                EditorSceneManager.SaveOpenScenes();
            }

            EditorApplication.isPlaying = true;
            return $"▶ Entering Play Mode...{(save ? " (scenes saved)" : "")}";
        }

        private string DoStop()
        {
            if (!EditorApplication.isPlaying)
                return "Not in Play Mode.";

            EditorApplication.isPlaying = false;
            return "⏹ Exiting Play Mode...";
        }

        private string DoPause()
        {
            if (!EditorApplication.isPlaying)
                return "Not in Play Mode — nothing to pause.";

            EditorApplication.isPaused = !EditorApplication.isPaused;
            return EditorApplication.isPaused ? "⏸ Paused" : "▶ Resumed";
        }

        private string DoStep()
        {
            if (!EditorApplication.isPlaying)
                return "Not in Play Mode — cannot step.";

            EditorApplication.isPaused = true;
            EditorApplication.Step();
            return "⏭ Stepped one frame";
        }

        private string DoStatus()
        {
            return $"Play Mode Status:\n" +
                   $"  Is Playing: {EditorApplication.isPlaying}\n" +
                   $"  Is Paused: {EditorApplication.isPaused}\n" +
                   $"  Is Compiling: {EditorApplication.isCompiling}\n" +
                   $"  Time Since Startup: {EditorApplication.timeSinceStartup:F1}s\n" +
                   $"  Game Time: {(EditorApplication.isPlaying ? Time.time.ToString("F2") + "s" : "N/A")}\n" +
                   $"  Frame Count: {(EditorApplication.isPlaying ? Time.frameCount.ToString() : "N/A")}";
        }
    }
}
