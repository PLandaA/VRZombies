using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VRZ.World
{
    /// Per-platform render budget for the arena, applied once on load (and A/B-testable in the editor
    /// with the S and B keys). Two costs that are invisible at night but not free on Quest:
    ///
    /// 1. Static geometry casting REALTIME shadows: 384 lightmapped renderers are redrawn into the
    ///    directional shadow map every frame although their shadows are already baked. Dynamic
    ///    casters (zombies, players, weapons) keep casting -- those shadows ground the characters.
    /// 2. Bloom with high-quality filtering and 6 iterations: ~12 full-screen blur passes per eye.
    ///    Quest gets bloom at 3 iterations, standard filtering; the look survives, the cost doesn't.
    public class PlatformRenderTweaks : MonoBehaviour
    {
        [Header("Static shadow casters")]
        [Tooltip("On Android: lightmapped static renderers stop casting realtime shadows")]
        [SerializeField] private bool cullStaticShadowsOnAndroid = true;

        [Header("Bloom")]
        [SerializeField] private Volume postVolume;
        [Tooltip("Bloom iterations on Android (PC keeps the profile's value)")]
        [SerializeField, Range(1, 8)] private int androidBloomIterations = 3;

        private readonly List<MeshRenderer> _culled = new();
        private Bloom _bloom;
        private bool _bloomHq; private int _bloomIter;   // originals, for restore

        private static bool IsAndroid =>
#if UNITY_ANDROID && !UNITY_EDITOR
            true;
#else
            false;
#endif

        private void Start()
        {
            if (postVolume == null) postVolume = FindFirstObjectByType<Volume>();
            if (postVolume != null && postVolume.profile.TryGet(out _bloom))   // .profile = runtime clone, the asset stays untouched
            {
                _bloomHq = _bloom.highQualityFiltering.value;
                _bloomIter = _bloom.maxIterations.value;
            }
            if (!IsAndroid) return;
            if (cullStaticShadowsOnAndroid) SetStaticShadows(false);
            SetCheapBloom(true);
        }

        /// Lightmapped static renderers: their shadow is in the lightmap already.
        private void SetStaticShadows(bool cast)
        {
            if (!cast)
            {
                _culled.Clear();
                foreach (var r in FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (!r.gameObject.isStatic) continue;
                    if (r.shadowCastingMode == ShadowCastingMode.Off) continue;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    _culled.Add(r);
                }
                Debug.Log($"[RenderTweaks] static shadow casters OFF ({_culled.Count} renderers)");
            }
            else
            {
                foreach (var r in _culled) if (r != null) r.shadowCastingMode = ShadowCastingMode.On;
                Debug.Log($"[RenderTweaks] static shadow casters ON ({_culled.Count} restored)");
                _culled.Clear();
            }
        }

        private void SetCheapBloom(bool cheap)
        {
            if (_bloom == null) return;
            _bloom.highQualityFiltering.value = cheap ? false : _bloomHq;
            _bloom.maxIterations.value = cheap ? androidBloomIterations : _bloomIter;
            Debug.Log($"[RenderTweaks] bloom {(cheap ? "CHEAP" : "PROFILE")}: hq={_bloom.highQualityFiltering.value} iterations={_bloom.maxIterations.value}");
        }

#if UNITY_EDITOR
        private bool _staticOff, _bloomCheap;
        private void Update()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            if (kb.sKey.wasPressedThisFrame) { _staticOff = !_staticOff; SetStaticShadows(!_staticOff); }
            if (kb.bKey.wasPressedThisFrame) { _bloomCheap = !_bloomCheap; SetCheapBloom(_bloomCheap); }
        }
#endif
    }
}
