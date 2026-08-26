using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace LazyRay.Tools
{
    /// <summary>
    /// v6.6: sets the instance tint color from chat ("píntate de verde").
    /// Updates the settings asset, the Scene View border, the toolbar
    /// badge and the instance registry entry — so the bridge and
    /// list_unity_projects can show matching colors per project.
    /// </summary>
    public class SetInstanceColorTool : IMcpTool
    {
        public string Name => "set_instance_color";

        public string Description =>
            "Set this Unity instance's tint color (Scene View border + toolbar badge + registry). " +
            "Accepts a hex color like '#4CAF50', a preset name (red, green, blue, yellow, orange, purple, brown, white, black), " +
            "or 'off' to disable tinting. Match it with the Claude Desktop instance icon color to identify pairs at a glance.";

        public bool IsDestructive => false;

        public JObject InputSchema => JObject.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""color"": { ""type"": ""string"", ""description"": ""Hex '#RRGGBB', preset name (red/green/blue/yellow/orange/purple/brown/white/black), or 'off'"" }
            },
            ""required"": [""color""]
        }");

        /// <summary>Shared preset palette (also used by the toolbar menu).</summary>
        public static readonly (string name, Color color)[] Presets =
        {
            ("red",    new Color(0.898f, 0.224f, 0.208f)),
            ("green",  new Color(0.263f, 0.627f, 0.278f)),
            ("blue",   new Color(0.118f, 0.533f, 0.898f)),
            ("yellow", new Color(0.992f, 0.847f, 0.208f)),
            ("orange", new Color(0.984f, 0.549f, 0.000f)),
            ("purple", new Color(0.557f, 0.141f, 0.667f)),
            ("brown",  new Color(0.427f, 0.298f, 0.255f)),
            ("white",  new Color(0.95f, 0.95f, 0.95f)),
            ("black",  new Color(0.10f, 0.10f, 0.10f)),
        };

        public string Execute(JObject input)
        {
            string colorStr = (input.Value<string>("color") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(colorStr))
                return "ERROR: 'color' is required.";
            return Apply(colorStr);
        }

        /// <summary>
        /// Apply a tint by preset name, hex or 'off'. Main thread only.
        /// Also callable from the toolbar dropdown menu.
        /// </summary>
        public static string Apply(string colorStr)
        {
            colorStr = colorStr.Trim().ToLowerInvariant();
            var settings = LazyRay.Core.LazyRaySettings.Instance;

            if (colorStr == "off" || colorStr == "none" || colorStr == "clear")
            {
                settings.instanceColor = Color.clear;
            }
            else
            {
                Color c = default;
                bool found = false;
                foreach (var (name, preset) in Presets)
                {
                    if (name == colorStr) { c = preset; found = true; break; }
                }
                if (!found && !ColorUtility.TryParseHtmlString(
                        colorStr.StartsWith("#") ? colorStr : "#" + colorStr, out c))
                    return $"ERROR: Could not parse color '{colorStr}'. Use '#RRGGBB', a preset name, or 'off'.";

                c.a = 1f;
                settings.instanceColor = c;
            }

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssetIfDirty(settings);

            // Refresh everything that shows the tint.
            try { LazyRay.Core.InstanceRegistry.Register(LazyRay.Core.McpHttpServer.PipeName); } catch { }
            SceneView.RepaintAll();
            try { UnityEditor.Toolbars.MainToolbar.Refresh("LazyRay/Status"); } catch { }

            string hex = settings.InstanceColorHex;
            return hex == null
                ? "Instance tint disabled."
                : $"Instance tint set to {hex} — Scene View border, toolbar badge and registry updated. " +
                  "Match your Claude Desktop instance icon to this color.";
        }
    }
}
