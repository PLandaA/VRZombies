using UnityEngine;
using UnityEditor;

#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace LazyRay.Core
{
    [CreateAssetMenu(fileName = "LazyRaySettings", menuName = "LazyRay/Settings")]
    public class LazyRaySettings : ScriptableObject
    {
        // Settings asset lives in project Assets (packages are read-only at runtime)
        private const string SETTINGS_PATH = "Assets/LazyRayData/Resources/LazyRaySettings.asset";
        private static LazyRaySettings _instance;

        public static LazyRaySettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<LazyRaySettings>("LazyRaySettings");
                    if (_instance == null)
                    {
                        _instance = CreateInstance<LazyRaySettings>();
                        var dir = System.IO.Path.GetDirectoryName(SETTINGS_PATH);
                        if (!System.IO.Directory.Exists(dir))
                            System.IO.Directory.CreateDirectory(dir);
                        AssetDatabase.CreateAsset(_instance, SETTINGS_PATH);
                        AssetDatabase.SaveAssets();
                        Debug.Log("[LazyRay] Created settings asset at " + SETTINGS_PATH);
                    }
                }
                return _instance;
            }
        }

        [Header("Instance Identity")]
        [Tooltip("v6.6: Tint color for this Unity instance (toolbar badge, Scene View border, registry). Alpha 0 = off. Match it with your Claude Desktop instance icon color.")]
        public Color instanceColor = Color.clear;

        /// <summary>Hex like "#4CAF50", or null when tinting is off.</summary>
        public string InstanceColorHex =>
            instanceColor.a > 0.01f ? "#" + ColorUtility.ToHtmlStringRGB(instanceColor) : null;

        [Header("MCP Server")]
        [Tooltip("Port for the local HTTP server (legacy, kept for compat)")]
        public int serverPort = 7823;

        [Tooltip("Auto-start MCP server when Unity opens")]
        public bool autoStartServer = true;

        [Header("Behavior")]
        [Tooltip("Ask for confirmation in Unity before editing files or hierarchy")]
        public bool requireConfirmation = false;

        [Tooltip("Max folder depth when scanning project structure")]
        public int maxFolderScanDepth = 4;

        [Tooltip("Max console log entries to capture")]
        public int maxConsoleLogEntries = 500;

        [Header("Paths")]
        [Tooltip("Folders to exclude from scanning")]
        public string[] excludedFolders = new[]
        {
            "Library", "Temp", "Logs", "obj", "Builds",
            "Packages", ".git", ".vs", ".idea"
        };

        [Tooltip("File extensions to treat as scripts/text")]
        public string[] scriptExtensions = new[]
        {
            ".cs", ".shader", ".hlsl", ".cginc", ".compute",
            ".json", ".xml", ".yaml", ".yml", ".txt", ".md", ".asmdef"
        };
    }
}
