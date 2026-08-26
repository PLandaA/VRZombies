using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LazyRay.Tools
{
    /// <summary>
    /// Control the Scene View camera programmatically.
    /// Actions: look_at, frame, set_position, orbit, align_with_view, top/front/right/perspective
    /// </summary>
    public class SceneNavigationTool : IMcpTool
    {
        public string Name => "scene_nav";
        public string Description =>
            "Control the Scene View camera.\n" +
            "Actions:\n" +
            "- look_at: Point camera at position [x,y,z]\n" +
            "- frame: Frame a GameObject (zoom to fit)\n" +
            "- set_position: Move camera to position [x,y,z] with optional rotation [x,y,z]\n" +
            "- orbit: Rotate around current pivot by [yaw, pitch] degrees\n" +
            "- set_orthographic: Switch to orthographic or perspective\n" +
            "- top: Top-down view\n" +
            "- front: Front view\n" +
            "- right: Right side view\n" +
            "- get_view: Get current camera position/rotation/pivot info";

        public bool IsDestructive => false;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""action"": { ""type"": ""string"", ""description"": ""Action: look_at, frame, set_position, orbit, set_orthographic, top, front, right, get_view"" },
                ""path"": { ""type"": ""string"", ""description"": ""GameObject path (for frame action)"" },
                ""position"": { ""type"": ""string"", ""description"": ""Position as [x,y,z] JSON array"" },
                ""rotation"": { ""type"": ""string"", ""description"": ""Rotation as [x,y,z] euler angles"" },
                ""size"": { ""type"": ""number"", ""description"": ""View size / zoom level (smaller = more zoomed in)"" },
                ""orthographic"": { ""type"": ""boolean"", ""description"": ""Orthographic mode (true/false)"" },
                ""yaw"": { ""type"": ""number"", ""description"": ""Yaw angle in degrees (for orbit)"" },
                ""pitch"": { ""type"": ""number"", ""description"": ""Pitch angle in degrees (for orbit)"" }
            },
            ""required"": [""action""]
        }");

        public string Execute(JObject input)
        {
            string action = input.Value<string>("action");

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return "ERROR: No active Scene View. Open a Scene View window first.";

            try
            {
                return action switch
                {
                    "look_at" => DoLookAt(sceneView, input),
                    "frame" => DoFrame(sceneView, input),
                    "set_position" => DoSetPosition(sceneView, input),
                    "orbit" => DoOrbit(sceneView, input),
                    "set_orthographic" => DoSetOrthographic(sceneView, input),
                    "top" => DoPresetView(sceneView, Quaternion.Euler(90, 0, 0), "Top"),
                    "front" => DoPresetView(sceneView, Quaternion.Euler(0, 0, 0), "Front"),
                    "right" => DoPresetView(sceneView, Quaternion.Euler(0, -90, 0), "Right"),
                    "get_view" => DoGetView(sceneView),
                    _ => $"ERROR: Unknown action '{action}'"
                };
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.Message}";
            }
        }

        private string DoLookAt(SceneView sv, JObject input)
        {
            var pos = ParseVector3(input, "position");
            if (pos == null) return "ERROR: 'position' required as [x,y,z]";

            float size = input.Value<float?>("size") ?? sv.size;
            sv.LookAt(pos.Value, sv.rotation, size);
            sv.Repaint();

            return $"Camera now looking at ({pos.Value.x:F2}, {pos.Value.y:F2}, {pos.Value.z:F2}), size={size:F2}";
        }

        private string DoFrame(SceneView sv, JObject input)
        {
            string path = input.Value<string>("path");
            if (string.IsNullOrEmpty(path)) return "ERROR: 'path' required for frame action";

            var go = HierarchyUtils.FindByPath(path);
            if (go == null) return $"ERROR: GameObject '{path}' not found";

            // Select and frame
            Selection.activeGameObject = go;
            sv.FrameSelected();
            sv.Repaint();

            return $"Framed '{go.name}' at ({go.transform.position.x:F2}, {go.transform.position.y:F2}, {go.transform.position.z:F2})";
        }

        private string DoSetPosition(SceneView sv, JObject input)
        {
            var pos = ParseVector3(input, "position");
            if (pos == null) return "ERROR: 'position' required as [x,y,z]";

            var rot = ParseVector3(input, "rotation");
            float size = input.Value<float?>("size") ?? sv.size;

            sv.pivot = pos.Value;
            if (rot != null)
                sv.rotation = Quaternion.Euler(rot.Value);
            sv.size = size;
            sv.Repaint();

            var euler = sv.rotation.eulerAngles;
            return $"Camera at ({pos.Value.x:F2}, {pos.Value.y:F2}, {pos.Value.z:F2}), " +
                   $"rotation=({euler.x:F1}, {euler.y:F1}, {euler.z:F1}), size={size:F2}";
        }

        private string DoOrbit(SceneView sv, JObject input)
        {
            float yaw = input.Value<float?>("yaw") ?? 0;
            float pitch = input.Value<float?>("pitch") ?? 0;

            var current = sv.rotation.eulerAngles;
            sv.rotation = Quaternion.Euler(current.x + pitch, current.y + yaw, 0);
            sv.Repaint();

            var euler = sv.rotation.eulerAngles;
            return $"Orbited by yaw={yaw:F1}° pitch={pitch:F1}°. Now at rotation ({euler.x:F1}, {euler.y:F1}, {euler.z:F1})";
        }

        private string DoSetOrthographic(SceneView sv, JObject input)
        {
            bool ortho = input.Value<bool?>("orthographic") ?? !sv.orthographic;
            sv.orthographic = ortho;
            sv.Repaint();
            return $"View mode: {(ortho ? "Orthographic" : "Perspective")}";
        }

        private string DoPresetView(SceneView sv, Quaternion rotation, string name)
        {
            sv.rotation = rotation;
            sv.orthographic = true;
            sv.Repaint();
            return $"Switched to {name} view (orthographic)";
        }

        private string DoGetView(SceneView sv)
        {
            var pivot = sv.pivot;
            var rot = sv.rotation.eulerAngles;
            var camPos = sv.camera.transform.position;

            return $"Scene View State:\n" +
                   $"  Pivot: ({pivot.x:F3}, {pivot.y:F3}, {pivot.z:F3})\n" +
                   $"  Camera Position: ({camPos.x:F3}, {camPos.y:F3}, {camPos.z:F3})\n" +
                   $"  Rotation: ({rot.x:F1}, {rot.y:F1}, {rot.z:F1})\n" +
                   $"  Size (zoom): {sv.size:F3}\n" +
                   $"  Orthographic: {sv.orthographic}\n" +
                   $"  FOV: {sv.cameraSettings.fieldOfView:F1}°\n" +
                   $"  2D Mode: {sv.in2DMode}";
        }

        private Vector3? ParseVector3(JObject input, string key)
        {
            string str = input.Value<string>(key);
            if (string.IsNullOrEmpty(str)) return null;
            try
            {
                var arr = JArray.Parse(str);
                return new Vector3(arr[0].Value<float>(), arr[1].Value<float>(), arr[2].Value<float>());
            }
            catch { return null; }
        }
    }
}
