using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace VRZ.Player
{
    /// Fixed foveated rendering on Quest (2026-10-12). The first store review measured the arena
    /// GPU-bound below 60 fps; on a Quest 3S, OVR Metrics showed GPU 85-95% with the CPU idle even
    /// after render scale 1.0, post-processing off and no main-light shadows. Foveation draws the
    /// periphery of each eye at reduced resolution, where the lenses blur anyway, and keeps the
    /// centre of the view untouched.
    ///
    /// Needs the OpenXR "Foveated Rendering" feature enabled with the SRP Foveation API (URP, Vulkan).
    /// Static bootstrap instead of a component: nothing to wire in the scenes, applied after the
    /// first scene and again on every scene load. Without a running headset (editor, tests) it does nothing.
    public static class FoveatedRenderingBootstrap
    {
        /// 0 = off, 1 = strongest. Quest 3S has no eye tracking, so this is fixed (not gaze-driven).
        public const float Level = 1f;

        private static readonly List<XRDisplaySubsystem> s_Displays = new List<XRDisplaySubsystem>();
        private static bool s_Logged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Apply();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Apply();

        private static void Apply()
        {
            SubsystemManager.GetSubsystems(s_Displays);
            foreach (var display in s_Displays)
            {
                if (!display.running) continue;
                display.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.None;
                display.foveatedRenderingLevel = Level;
                if (!s_Logged)
                {
                    s_Logged = true;
                    Debug.Log("[Foveation] fixed foveated rendering level " + Level + " on '" + display.subsystemDescriptor.id + "'");
                }
            }
        }
    }
}
