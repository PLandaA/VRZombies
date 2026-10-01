#if VRZ_NET_DIAGNOSTICS
using TMPro;
using UnityEngine;

namespace VRZ.FX
{
    /// In-headset frame-rate readout for diagnostic builds (2026-10-01). Exists only under
    /// VRZ_NET_DIAGNOSTICS, like every other probe: creates itself at startup, follows the active
    /// camera across scene loads, and shows current FPS, the minimum over the last 5 s and the
    /// frame time. Every 5 s it also logs "[FPS] ..." so the Quest log carries the numbers for
    /// VRZ > Dev > Dummy Partner > Pull Quest log. Not a measurement of the compositor (that is
    /// VrApi's "FPS=72/72"), but of the app's own frame delivery, which is what we tune.
    public class FpsReadout : MonoBehaviour
    {
        private const float Window = 5f;
        private const float RefreshEvery = 0.25f;

        private TextMeshPro _text;
        private Transform _cam;
        private float _ema = 72f, _min = 999f, _windowStart, _refreshAt;
        private int _windowFrames;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindFirstObjectByType<FpsReadout>() != null) return;
            var go = new GameObject("FpsReadout (diagnostics)");
            DontDestroyOnLoad(go);
            go.AddComponent<FpsReadout>();
        }

        private void Awake()
        {
            var textGo = new GameObject("FpsText");
            textGo.transform.SetParent(transform, false);
            _text = textGo.AddComponent<TextMeshPro>();
            _text.fontSize = 0.5f;
            _text.alignment = TextAlignmentOptions.BottomRight;
            _text.rectTransform.sizeDelta = new Vector2(0.5f, 0.2f);
            _text.color = new Color(1f, 1f, 1f, 0.85f);
            _text.text = "fps";
            _windowStart = Time.unscaledTime;
        }

        private void LateUpdate()
        {
            // Follow whichever camera is active (the rig is recreated on every scene load).
            if (_cam == null || !_cam.gameObject.activeInHierarchy)
            {
                var cam = Camera.main;
                _cam = cam != null ? cam.transform : null;
                if (_cam == null) return;
            }
            // Bottom-right of the view, 0.7 m ahead: readable without blocking the sights.
            transform.position = _cam.position + _cam.forward * 0.7f + _cam.right * 0.28f - _cam.up * 0.22f;
            transform.rotation = _cam.rotation;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;
            float fps = 1f / dt;
            _ema = Mathf.Lerp(_ema, fps, 0.1f);
            if (fps < _min) _min = fps;
            _windowFrames++;

            float now = Time.unscaledTime;
            if (now >= _refreshAt)
            {
                _refreshAt = now + RefreshEvery;
                _text.text = Mathf.RoundToInt(_ema) + " fps  min " + Mathf.RoundToInt(_min) + "  " + (dt * 1000f).ToString("F1") + " ms";
                _text.color = _ema >= 70f ? new Color(0.6f, 1f, 0.6f, 0.85f) : _ema >= 60f ? new Color(1f, 0.9f, 0.5f, 0.85f) : new Color(1f, 0.5f, 0.5f, 0.85f);
            }
            if (now - _windowStart >= Window)
            {
                float avg = _windowFrames / (now - _windowStart);
                Debug.Log("[FPS] avg " + avg.ToString("F1") + " min " + _min.ToString("F1") + " over " + Window + " s");
                _windowStart = now; _windowFrames = 0; _min = 999f;
            }
        }
    }
}
#endif
