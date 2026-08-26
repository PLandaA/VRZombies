using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LazyRay.Tools
{
    /// <summary>
    /// Capture screenshots from Scene View or Game View.
    /// Creates a fresh temporary camera at the SceneView's position and renders to RenderTexture.
    /// Works independently of window focus (no screen pixel reading).
    /// </summary>
    public class ScreenCaptureTool : IMcpTool
    {
        public string Name => "screenshot";
        public string Description =>
            "Take a screenshot of Unity editor views. Returns an image.\n" +
            "Use 'scene' for Scene View (default) or 'game' for Game View.";

        public bool IsDestructive => false;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""source"": { ""type"": ""string"", ""description"": ""View to capture: 'scene' (default) or 'game'"" },
                ""width"": { ""type"": ""integer"", ""description"": ""Image width in pixels (default 800)"" },
                ""height"": { ""type"": ""integer"", ""description"": ""Image height in pixels (default 600)"" }
            },
            ""required"": []
        }");

        public string Execute(JObject input)
        {
            string source = input.Value<string>("source") ?? "scene";
            int width = Mathf.Clamp(input.Value<int?>("width") ?? 800, 128, 2048);
            int height = Mathf.Clamp(input.Value<int?>("height") ?? 600, 128, 2048);

            try
            {
                byte[] pngBytes = source switch
                {
                    "scene" => CaptureSceneView(width, height),
                    "game" => CaptureGameView(width, height),
                    _ => throw new Exception($"Unknown source '{source}'. Use 'scene' or 'game'.")
                };

                string base64 = Convert.ToBase64String(pngBytes);

                return JsonConvert.SerializeObject(new
                {
                    type = "image",
                    mimeType = "image/png",
                    data = base64,
                    width,
                    height,
                    source
                });
            }
            catch (Exception ex)
            {
                return $"ERROR: Screenshot failed: {ex.Message}";
            }
        }

        private byte[] CaptureSceneView(int width, int height)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                throw new Exception("No active Scene View. Open a Scene View first.");

            var svCam = sceneView.camera;
            if (svCam == null)
                throw new Exception("Scene View camera not available.");

            // Create a FRESH camera — don't CopyFrom the SceneView camera
            // because SceneView camera has internal SRP state that breaks off-screen rendering
            var tempGO = new GameObject("__LazyRay_Capture__");
            tempGO.hideFlags = HideFlags.HideAndDontSave;
            var cam = tempGO.AddComponent<Camera>();

            try
            {
                // Copy only the essential view parameters manually
                cam.transform.position = svCam.transform.position;
                cam.transform.rotation = svCam.transform.rotation;
                cam.fieldOfView = svCam.fieldOfView;
                cam.nearClipPlane = svCam.nearClipPlane;
                cam.farClipPlane = svCam.farClipPlane;
                cam.orthographic = svCam.orthographic;
                cam.orthographicSize = svCam.orthographicSize;
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 1f);
                cam.cullingMask = svCam.cullingMask;
                cam.enabled = false; // Manual render only

                // Setup URP additional camera data so the render pipeline picks it up
                SetupURPCamera(tempGO);

                // Render to texture
                return RenderCameraToTexture(cam, width, height);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempGO);
            }
        }

        private byte[] CaptureGameView(int width, int height)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var allCams = Camera.allCameras;
                if (allCams.Length > 0) cam = allCams[0];
            }

            if (cam == null)
                throw new Exception("No camera found in scene for 'game' capture.");

            return RenderCameraToTexture(cam, width, height);
        }

        private byte[] RenderCameraToTexture(Camera cam, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 2;
            var prevTarget = cam.targetTexture;
            var prevActive = RenderTexture.active;

            try
            {
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);

                // Validate — if all black, try with SolidColor clear flags
                if (IsAllBlack(png))
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.Render();

                    RenderTexture.active = rt;
                    var tex2 = new Texture2D(width, height, TextureFormat.RGB24, false);
                    tex2.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                    tex2.Apply();

                    byte[] png2 = tex2.EncodeToPNG();
                    UnityEngine.Object.DestroyImmediate(tex2);

                    if (!IsAllBlack(png2)) return png2;
                    Debug.LogWarning("[LazyRay] Screenshot rendered black. Pipeline may not support off-screen Camera.Render().");
                }

                return png;
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        /// <summary>
        /// Add UniversalAdditionalCameraData so URP renders the temp camera properly.
        /// Uses reflection to avoid hard dependency on URP package.
        /// </summary>
        private void SetupURPCamera(GameObject cameraGO)
        {
            var urpDataType = FindType("UniversalAdditionalCameraData");
            if (urpDataType == null) return;

            var data = cameraGO.AddComponent(urpDataType);
            if (data == null) return;

            try
            {
                // Set CameraRenderType.Base = 0
                var renderTypeProp = urpDataType.GetProperty("renderType", BindingFlags.Public | BindingFlags.Instance);
                if (renderTypeProp != null)
                {
                    var baseValue = Enum.ToObject(renderTypeProp.PropertyType, 0);
                    renderTypeProp.SetValue(data, baseValue);
                }

                SetBoolProp(data, urpDataType, "renderPostProcessing", true);
                SetBoolProp(data, urpDataType, "renderShadows", true);
            }
            catch { }
        }

        private void SetBoolProp(object obj, Type type, string name, bool value)
        {
            try
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) prop.SetValue(obj, value);
            }
            catch { }
        }

        private Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == name) return t;
                }
                catch { }
            }
            return null;
        }

        private bool IsAllBlack(byte[] pngBytes)
        {
            var checkTex = new Texture2D(2, 2);
            if (!checkTex.LoadImage(pngBytes))
            {
                UnityEngine.Object.DestroyImmediate(checkTex);
                return true;
            }

            var pixels = checkTex.GetPixels32();
            UnityEngine.Object.DestroyImmediate(checkTex);

            int step = Mathf.Max(1, pixels.Length / 200);
            for (int i = 0; i < pixels.Length; i += step)
            {
                if (pixels[i].r > 5 || pixels[i].g > 5 || pixels[i].b > 5)
                    return false;
            }
            return true;
        }
    }
}
