using UnityEngine;

namespace VRZ.Enemies
{

    /// Red hit-flash feedback on zombies via MaterialPropertyBlock whenever networked health drops.
    /// Event-driven: listens to NetworkZombie.OnHealthChanged instead of reading Health every frame;
    /// Update only animates the tint while a flash is running.
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
        private float _timer;

        private void Awake()
        {
            _zombie = GetComponent<NetworkZombie>();
            _renderers = GetComponentsInChildren<Renderer>(true);
            _mpb = new MaterialPropertyBlock();
        }

        private void OnEnable()
        {
            if (_zombie == null) return;
            _zombie.OnLocalReset += ResetForNewLife;
            _zombie.OnHealthChanged += OnHealthChanged;
        }

        private void OnDisable()
        {
            if (_zombie == null) return;
            _zombie.OnLocalReset -= ResetForNewLife;
            _zombie.OnHealthChanged -= OnHealthChanged;
        }

        /// Pool readiness: drop any red tint still showing from the previous life.
        private void ResetForNewLife()
        {
            _timer = 0f;
            ClearTint();
        }

        private void OnHealthChanged(int previous, int current)
        {
            if (current < previous) _timer = flashDuration;
        }

        private void Update()
        {
            if (_timer <= 0f) return;   // idle: nothing to animate, nothing to read

            _timer -= Time.deltaTime;
            float t = Mathf.Clamp01(_timer / flashDuration);
            ApplyTint(Color.Lerp(Color.white, flashColor, t));
            if (_timer <= 0f) ClearTint();
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
}
