using System.Collections;
using UnityEngine;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.World;

namespace VRZ.FX
{

    /// Distant storm: every 25-70s a double lightning flash pulses the skybox exposure (on a
    /// runtime COPY of the material -- the asset is never mutated) plus an optional delayed
    /// thunder clip whose delay sells the distance.
    public class StormAmbience : MonoBehaviour
    {
        [SerializeField] private Vector2 intervalRange = new Vector2(15f, 40f);
        [SerializeField] private float flashExposureBoost = 3.2f;
        [SerializeField] private float lightFlashIntensity = 3.5f;
        [SerializeField] private AudioClip thunderClip;
        [SerializeField] private Vector2 thunderDelayRange = new Vector2(1.5f, 4f);
        [Range(0f, 1f)] [SerializeField] private float thunderVolume = 0.6f;

        private Material _skyInstance;
        private float _baseExposure;
        private Light _flashLight;

        private void Start()
        {
            if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Exposure"))
            {
                _skyInstance = new Material(RenderSettings.skybox);   // runtime copy: asset untouched
                RenderSettings.skybox = _skyInstance;
                _baseExposure = _skyInstance.GetFloat("_Exposure");

                // A dedicated directional light sells the strike: the whole WORLD blinks, not just the sky
                var lightGo = new GameObject("LightningFlash");
                lightGo.transform.SetParent(transform, false);
                lightGo.transform.rotation = Quaternion.Euler(55f, -30f, 0f);
                _flashLight = lightGo.AddComponent<Light>();
                _flashLight.type = LightType.Directional;
                _flashLight.color = new Color(0.75f, 0.8f, 1f);
                _flashLight.intensity = 0f;
                _flashLight.shadows = LightShadows.None;

                StartCoroutine(StormLoop());
            }
        }

        private IEnumerator StormLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(Random.Range(intervalRange.x, intervalRange.y));
                yield return Flash(0.22f);
                yield return new WaitForSeconds(0.09f);
                yield return Flash(0.5f);   // classic double-strike, long enough to catch mid-combat

                if (thunderClip != null && Camera.main != null)
                {
                    yield return new WaitForSeconds(Random.Range(thunderDelayRange.x, thunderDelayRange.y));
                    AudioSource.PlayClipAtPoint(thunderClip, Camera.main.transform.position, thunderVolume);
                }
            }
        }

        private IEnumerator Flash(float duration)
        {
            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = 1f - Mathf.Abs((t / duration) * 2f - 1f);   // up then down
                _skyInstance.SetFloat("_Exposure", _baseExposure + flashExposureBoost * k);
                if (_flashLight != null) _flashLight.intensity = lightFlashIntensity * k;
                yield return null;
            }
            _skyInstance.SetFloat("_Exposure", _baseExposure);
            if (_flashLight != null) _flashLight.intensity = 0f;
        }
    }
}
