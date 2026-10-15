using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace VRZ.FX
{
    /// Quest renders without post-processing (2026-10-15: the first store review measured the arena
    /// below 60 fps; on a Quest 3S the post pass alone cost ~10 fps with the GPU saturated). This bakes
    /// the visible part of the scene's post profile -- its Color Adjustments: color filter, post
    /// exposure and saturation -- into the scene's own lighting, fog and sky once, at load, so the
    /// look survives at zero per-frame cost. Not reproduced: contrast (a non-linear curve), Bloom
    /// (it barely triggers without HDR) and Vignette. Texture colours keep their own saturation.
    ///
    /// Runs before any Start(), so StormAmbience copies the already-graded sky and keeps it as the
    /// base of every lightning flash. Android player only: the editor and PC keep the real post pass.
    public static class PostLookBaker
    {
        private static readonly HashSet<int> s_BakedScenes = new HashSet<int>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Init()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
#endif
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // RenderSettings belong to the active scene; every load in this game is Single.
            if (scene != SceneManager.GetActiveScene()) return;
            if (!s_BakedScenes.Add(scene.handle)) return;
            Bake(scene.name);
        }

        private static void Bake(string sceneName)
        {
            ColorAdjustments ca = null;
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
                if (v.isGlobal && v.sharedProfile != null && v.sharedProfile.TryGet(out ca) && ca.active) break;
            if (ca == null || !ca.active) return;

            float exposure = ca.postExposure.overrideState ? Mathf.Pow(2f, ca.postExposure.value) : 1f;
            Color filter   = ca.colorFilter.overrideState ? ca.colorFilter.value : Color.white;
            float keep     = ca.saturation.overrideState ? Mathf.Clamp01(1f + ca.saturation.value / 100f) : 1f;
            float gain     = exposure * Luminance(filter);          // the brightness part of the filter

            // Ambient light comes from the skybox as a spherical-harmonics probe: scale and desaturate it.
            var probe = RenderSettings.ambientProbe;
            float ambientBefore = Luminance(new Color(probe[0, 0], probe[1, 0], probe[2, 0]));
            for (int i = 0; i < 9; i++)
            {
                var c = Grade(new Color(probe[0, i], probe[1, i], probe[2, i]), filter, exposure, keep);
                probe[0, i] = c.r; probe[1, i] = c.g; probe[2, i] = c.b;
            }
            RenderSettings.ambientProbe = probe;
            float ambientAfter = Luminance(new Color(probe[0, 0], probe[1, 0], probe[2, 0]));

            // Sky: a runtime copy, the asset is never touched.
            if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Exposure"))
            {
                var sky = new Material(RenderSettings.skybox);
                sky.SetFloat("_Exposure", sky.GetFloat("_Exposure") * gain);
                if (sky.HasProperty("_Tint")) sky.SetColor("_Tint", Desaturate(sky.GetColor("_Tint"), keep));
                RenderSettings.skybox = sky;
            }

            RenderSettings.fogColor = Grade(RenderSettings.fogColor, filter, exposure, keep);

            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                // bakingOutput, not lightmapBakeType: that property only exists in the editor.
                if (light.bakingOutput.isBaked && light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked) continue;
                light.color = Desaturate(light.color * filter, keep);
                light.intensity *= exposure;
            }

            Debug.Log("[PostLookBaker] '" + sceneName + "': gain " + gain.ToString("F2") + ", saturation x" + keep.ToString("F2")
                      + " baked into sky, fog and lights; ambient L0 " + ambientBefore.ToString("F3") + " -> " + ambientAfter.ToString("F3")
                      + (ambientBefore <= 0f ? " (no ambient probe: ambient not graded)" : ""));
        }

        private static Color Grade(Color c, Color filter, float exposure, float keep)
        {
            var graded = Desaturate(c * filter * exposure, keep);
            graded.a = c.a;   // grade the colour only: fog and light alpha are not brightness
            return graded;
        }

        /// Linear mix towards the Rec.709 luminance; keep = 1 leaves the colour as is.
        private static Color Desaturate(Color c, float keep)
        {
            float l = Luminance(c);
            return new Color(Mathf.Lerp(l, c.r, keep), Mathf.Lerp(l, c.g, keep), Mathf.Lerp(l, c.b, keep), c.a);
        }

        private static float Luminance(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
    }
}
