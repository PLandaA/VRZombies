using System.Collections;
using UnityEngine;

namespace VRZ.FX
{

    /// Brief glowing bullet tracer: a thin fading line from muzzle to impact. Makes night-time
    /// gunfire readable. Uses Sprites/Default (URP-safe, vertex color, transparent).
    public class BulletTracer : MonoBehaviour
    {
        [SerializeField] private Color tracerColor = new Color(1f, 0.85f, 0.5f, 0.85f);
        [SerializeField] private float width = 0.012f;
        [SerializeField] private float lifeTime = 0.07f;

        private static Material _mat;

        /// lifeTimeOverride / widthScale: remote shots use a longer, wider tracer (see NetworkAutoGun).
        public void Fire(Vector3 from, Vector3 to, float lifeTimeOverride = -1f, float widthScale = 1f)
        {
            if (_mat == null)
                _mat = new Material(Shader.Find("Sprites/Default"));

            float w = width * widthScale;
            float life = lifeTimeOverride > 0f ? lifeTimeOverride : lifeTime;

            var go = new GameObject("Tracer");
            var lr = go.AddComponent<LineRenderer>();
            lr.material = _mat;
            lr.positionCount = 2;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
            lr.startWidth = w;
            lr.endWidth = w * 0.4f;
            lr.startColor = tracerColor;
            lr.endColor = new Color(tracerColor.r, tracerColor.g, tracerColor.b, 0.25f);
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            StartCoroutine(Fade(lr, go, life));
        }

        private IEnumerator Fade(LineRenderer lr, GameObject go, float life)
        {
            float t = 0f;
            Color c0 = lr.startColor, c1 = lr.endColor;
            while (t < life)
            {
                t += Time.deltaTime;
                float a = 1f - (t / life);
                lr.startColor = new Color(c0.r, c0.g, c0.b, c0.a * a);
                lr.endColor = new Color(c1.r, c1.g, c1.b, c1.a * a);
                yield return null;
            }
            Destroy(go);
        }
    }
}
