using System;
using UnityEngine;

namespace VRZ.World
{
    /// Per-platform terrain detail budget. Grass is the single biggest GPU cost in the arena
    /// (measured: 13.7M tris/frame at 40m/0.55 on PC), and Quest has a fraction of a PC GPU,
    /// so the same scene needs two budgets. Applied once on Awake; the Terrain component keeps
    /// its authored values as the fallback.
    [RequireComponent(typeof(Terrain))]
    public class TerrainQualityProfile : MonoBehaviour
    {
        [Serializable]
        public struct Profile
        {
            [Tooltip("Meters from the camera where detail meshes (grass) stop being drawn")]
            public float detailDistance;
            [Range(0f, 1f), Tooltip("Global multiplier on the painted detail density")]
            public float detailDensity;
            [Tooltip("Heightmap LOD tolerance in pixels; higher = fewer terrain triangles")]
            public float pixelError;
        }

        [SerializeField] private Profile pc      = new Profile { detailDistance = 30f, detailDensity = 0.5f,  pixelError = 12f };
        [SerializeField] private Profile android = new Profile { detailDistance = 12f, detailDensity = 0.3f,  pixelError = 20f };

        private void Awake() => Apply(IsAndroid ? android : pc, IsAndroid ? "Android" : "PC");

        private static bool IsAndroid =>
#if UNITY_ANDROID && !UNITY_EDITOR
            true;
#else
            false;
#endif

        /// Also callable from the inspector context menu to preview the Quest budget in the editor.
        [ContextMenu("Apply Android profile")]
        private void ApplyAndroid() => Apply(android, "Android (preview)");

        [ContextMenu("Apply PC profile")]
        private void ApplyPc() => Apply(pc, "PC");

        private void Apply(Profile p, string label)
        {
            var terrain = GetComponent<Terrain>();
            terrain.detailObjectDistance = p.detailDistance;
            terrain.detailObjectDensity = p.detailDensity;
            terrain.heightmapPixelError = p.pixelError;
            Debug.Log($"[Terrain] {label} profile: detail {p.detailDistance}m x{p.detailDensity:F2}, pixelError {p.pixelError}");
        }
    }
}
