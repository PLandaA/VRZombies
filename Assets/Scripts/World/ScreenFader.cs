using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace VRZ.World
{
    /// Full-view fade to black for scene transitions (VR-safe: a world-space canvas that follows
    /// whichever camera is current). Survives scene loads; re-parents itself when the rig is
    /// replaced. Why it exists: Fusion activates the arena with a synchronous scene integration
    /// (measured 550 ms in the editor, ~GC.Collect + unload of the lobby) that cannot be avoided,
    /// only hidden. Lobby fades out during the last second of the countdown; the arena fades in
    /// once the local character has spawned.
    public class ScreenFader : MonoBehaviour
    {
        public static ScreenFader Instance { get; private set; }

        private const float PanelDistance = 0.12f;     // in front of the eyes, closer than hands ever get
        private Canvas _canvas;
        private Image _image;
        private float _alpha;
        private Coroutine _running;

        public bool IsBlack => _alpha >= 0.999f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("[ScreenFader]");
            DontDestroyOnLoad(go);
            go.AddComponent<ScreenFader>();
        }

        private void Awake()
        {
            Instance = this;
            BuildCanvas();
        }

        private void BuildCanvas()
        {
            var canvasGo = new GameObject("FadeCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = short.MaxValue;               // above every other world canvas
            canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(4000, 4000);
            canvasGo.transform.localScale = Vector3.one * 0.001f;

            var imgGo = new GameObject("Black");
            imgGo.transform.SetParent(canvasGo.transform, false);
            _image = imgGo.AddComponent<Image>();
            _image.raycastTarget = false;
            var rt = imgGo.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

            Apply(0f);
        }

        private void LateUpdate()
        {
            // Follow the current camera by POSE, never by parenting: the rig (and its camera) is
            // destroyed on every scene switch, and a parented canvas would die with it.
            var cam = Camera.main;
            if (cam == null || _canvas == null) return;
            var t = _canvas.transform;
            var ct = cam.transform;
            t.SetPositionAndRotation(ct.position + ct.forward * PanelDistance, ct.rotation);
        }

        private void Apply(float a)
        {
            _alpha = Mathf.Clamp01(a);
            _image.color = new Color(0f, 0f, 0f, _alpha);
            _canvas.enabled = _alpha > 0.001f;                    // no draw call when fully transparent
        }

        public static void FadeOut(float seconds) => Instance?.FadeTo(1f, seconds);
        public static void FadeIn(float seconds) => Instance?.FadeTo(0f, seconds);
        public static void SetBlack() => Instance?.Apply(1f);

        private void FadeTo(float target, float seconds)
        {
            if (_running != null) StopCoroutine(_running);
            _running = StartCoroutine(FadeRoutine(target, seconds));
        }

        private IEnumerator FadeRoutine(float target, float seconds)
        {
            float from = _alpha;
            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;                      // immune to slow-mo (RoundJuice)
                Apply(Mathf.Lerp(from, target, Mathf.Clamp01(t / seconds)));
                yield return null;
            }
            Apply(target);
            _running = null;
        }
    }
}
