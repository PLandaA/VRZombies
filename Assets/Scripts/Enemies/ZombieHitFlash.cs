using UnityEngine;

/// Red hit-flash feedback on zombies via MaterialPropertyBlock whenever networked health drops.
public class ZombieHitFlash : MonoBehaviour
{
    [Tooltip("Flash duration in seconds")]
    [SerializeField] private float flashDuration = 0.2f;
    [Tooltip("Tint color when taking damage")]
    [SerializeField] private Color flashColor = new Color(1f, 0.15f, 0.15f, 1f);

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private NetworkZombie _zombie;
    private Renderer[] _renderers;
    private MaterialPropertyBlock _mpb;
    private int _lastHealth = int.MinValue;
    private float _timer;

    private void Awake()
    {
        _zombie = GetComponent<NetworkZombie>();
        _renderers = GetComponentsInChildren<Renderer>(true);
        _mpb = new MaterialPropertyBlock();
    }

    private void Update()
    {
        if (_zombie != null && _zombie.Object != null && _zombie.Object.IsValid)
        {
            int h = _zombie.Health;
            if (_lastHealth == int.MinValue)
                _lastHealth = h;
            else if (h < _lastHealth)
                _timer = flashDuration;
            _lastHealth = h;
        }

        if (_timer > 0f)
        {
            _timer -= Time.deltaTime;
            float t = Mathf.Clamp01(_timer / flashDuration);
            ApplyTint(Color.Lerp(Color.white, flashColor, t));
            if (_timer <= 0f) ClearTint();
        }
    }

    private void ApplyTint(Color c)
    {
        foreach (var r in _renderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(_mpb);
            _mpb.SetColor(BaseColorId, c);
            _mpb.SetColor(ColorId, c);
            r.SetPropertyBlock(_mpb);
        }
    }

    private void ClearTint()
    {
        foreach (var r in _renderers)
        {
            if (r == null) continue;
            r.SetPropertyBlock(null);
        }
    }
}
