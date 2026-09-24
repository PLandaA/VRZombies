using UnityEngine;
using Autohand;

namespace VRZ.FX
{

    /// Real light burst at the muzzle on every shot: 2-3 frames of warm orange that actually
    /// illuminates the scene. In a night arena each shot becomes a camera flash -- pure drama
    /// for the cost of one shadowless point light.
    public class MuzzleFlashLight : MonoBehaviour
    {
        [SerializeField] private float peakIntensity = 5f;
        [SerializeField] private float decayPerSecond = 90f;
        [SerializeField] private float range = 8f;

        private AutoGun _gun;
        private Light _light;
        private float _flash;

        private void Awake()
        {
            _gun = GetComponent<AutoGun>();
            if (_gun == null || _gun.shootForward == null) { enabled = false; return; }

            var go = new GameObject("MuzzleFlashLight");
            go.transform.SetParent(_gun.shootForward, false);
            go.transform.localPosition = new Vector3(0f, 0f, 0.02f);

            _light = go.AddComponent<Light>();
            _light.type = LightType.Point;
            _light.color = new Color(1f, 0.72f, 0.35f);   // burning powder
            _light.range = range;
            _light.intensity = 0f;
            _light.shadows = LightShadows.None;           // the shadow atlas is crowded enough
        }

        private void OnEnable() { if (_gun != null) _gun.OnShoot.AddListener(OnShoot); }
        private void OnDisable() { if (_gun != null) _gun.OnShoot.RemoveListener(OnShoot); }

        private void OnShoot(AutoGun gun) { _flash = peakIntensity; }

        private void Update()
        {
            if (_flash <= 0f && _light.intensity <= 0f) return;
            _light.intensity = _flash;
            _flash = Mathf.MoveTowards(_flash, 0f, decayPerSecond * Time.deltaTime);
        }
    }
}
