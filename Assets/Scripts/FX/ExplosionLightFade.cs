using UnityEngine;

/// Big warm light flash for explosions: spikes on spawn and fades out, then disables itself.
/// Lives on the FX_GrenadeExplosion prefab root.
public class ExplosionLightFade : MonoBehaviour
{
    [SerializeField] private Color color = new Color(1f, 0.6f, 0.25f);
    [SerializeField] private float intensity = 14f;
    [SerializeField] private float range = 14f;
    [SerializeField] private float fadeTime = 0.35f;

    private Light _light;
    private float _t;

    private void Awake()
    {
        var go = new GameObject("ExplosionLight");
        go.transform.SetParent(transform, false);
        _light = go.AddComponent<Light>();
        _light.type = LightType.Point;
        _light.color = color;
        _light.range = range;
        _light.intensity = intensity;
        _light.shadows = LightShadows.None;
    }

    private void Update()
    {
        _t += Time.deltaTime;
        float k = Mathf.Clamp01(_t / fadeTime);
        _light.intensity = Mathf.Lerp(intensity, 0f, k * k);
        if (k >= 1f) _light.enabled = false;
    }
}
