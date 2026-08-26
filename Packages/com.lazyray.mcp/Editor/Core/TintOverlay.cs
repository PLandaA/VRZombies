using UnityEditor;
using UnityEngine;

namespace LazyRay.Core
{
    /// <summary>
    /// v6.6: paints a colored border around every Scene View when an
    /// instance tint is set in LazyRaySettings — so with two Unity
    /// instances open you can tell them apart at a glance, matching the
    /// color of the Claude Desktop instance icon driving each one.
    /// </summary>
    [InitializeOnLoad]
    public static class TintOverlay
    {
        private const float BORDER = 4f;

        static TintOverlay()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        private static void OnSceneGui(SceneView view)
        {
            Color c;
            try { c = LazyRaySettings.Instance.instanceColor; }
            catch { return; }
            if (c.a <= 0.01f) return;

            c.a = 0.9f;
            Handles.BeginGUI();
            float w = view.position.width;
            float h = view.position.height;
            EditorGUI.DrawRect(new Rect(0, 0, w, BORDER), c);          // top
            EditorGUI.DrawRect(new Rect(0, h - BORDER - 20, w, BORDER), c); // bottom (above tab strip)
            EditorGUI.DrawRect(new Rect(0, 0, BORDER, h), c);          // left
            EditorGUI.DrawRect(new Rect(w - BORDER, 0, BORDER, h), c); // right
            Handles.EndGUI();
        }

        /// <summary>
        /// Nearest colored-square emoji for the current instance tint, or
        /// null when tinting is off. Used as a toolbar badge (the Unity 6
        /// main toolbar API is text-only, so emoji is the color channel).
        /// </summary>
        public static string ColorSquareEmoji()
        {
            Color c;
            try { c = LazyRaySettings.Instance.instanceColor; }
            catch { return null; }
            if (c.a <= 0.01f) return null;

            (Color color, string emoji)[] squares =
            {
                (new Color(0.898f, 0.224f, 0.208f), "\U0001F7E5"), // red
                (new Color(0.263f, 0.627f, 0.278f), "\U0001F7E9"), // green
                (new Color(0.118f, 0.533f, 0.898f), "\U0001F7E6"), // blue
                (new Color(0.992f, 0.847f, 0.208f), "\U0001F7E8"), // yellow
                (new Color(0.984f, 0.549f, 0.000f), "\U0001F7E7"), // orange
                (new Color(0.557f, 0.141f, 0.667f), "\U0001F7EA"), // purple
                (new Color(0.427f, 0.298f, 0.255f), "\U0001F7EB"), // brown
                (new Color(0.10f, 0.10f, 0.10f),    "\u2B1B"),     // black
                (new Color(0.95f, 0.95f, 0.95f),    "\u2B1C"),     // white
            };

            string best = null;
            float bestDist = float.MaxValue;
            foreach (var (sc, emoji) in squares)
            {
                float dr = c.r - sc.r, dg = c.g - sc.g, db = c.b - sc.b;
                float dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist) { bestDist = dist; best = emoji; }
            }
            return best;
        }
    }
}
