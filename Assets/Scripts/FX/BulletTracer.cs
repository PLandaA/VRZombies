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

    /// Brief glowing bullet tracer: a thin fading line from muzzle to impact. Makes night-time
    /// gunfire readable. Uses Sprites/Default (URP-safe, vertex color, transparent).
    public class BulletTracer : MonoBehaviour
    {
        [SerializeField] private Color tracerColor = new Color(1f, 0.85f, 0.5f, 0.85f);
        [SerializeField] private float width = 0.012f;
        [SerializeField] private float lifeTime = 0.07f;

        private static Material _mat;

        public void Fire(Vector3 from, Vector3 to)
        {
            if (_mat == null)
                _mat = new Material(Shader.Find("Sprites/Default"));

            var go = new GameObject("Tracer");
            var lr = go.AddComponent<LineRenderer>();
            lr.material = _mat;
            lr.positionCount = 2;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
            lr.startWidth = width;
            lr.endWidth = width * 0.4f;
            lr.startColor = tracerColor;
            lr.endColor = new Color(tracerColor.r, tracerColor.g, tracerColor.b, 0.25f);
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            StartCoroutine(Fade(lr, go));
        }

        private IEnumerator Fade(LineRenderer lr, GameObject go)
        {
            float t = 0f;
            Color c0 = lr.startColor, c1 = lr.endColor;
            while (t < lifeTime)
            {
                t += Time.deltaTime;
                float a = 1f - (t / lifeTime);
                lr.startColor = new Color(c0.r, c0.g, c0.b, c0.a * a);
                lr.endColor = new Color(c1.r, c1.g, c1.b, c1.a * a);
                yield return null;
            }
            Destroy(go);
        }
    }
}
