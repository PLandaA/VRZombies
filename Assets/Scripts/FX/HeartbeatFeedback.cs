using UnityEngine;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.World;

namespace VRZ.FX
{

    /// Low-health heartbeat: a procedurally synthesized lub-dub (no audio asset needed) that
    /// fades in below 35% health and accelerates as death approaches. Classic VR tension cue --
    /// you FEEL the danger without looking at any UI.
    public class HeartbeatFeedback : MonoBehaviour
    {
        [Tooltip("Health fraction where the heartbeat starts (0.35 = below 35%).")]
        [Range(0.1f, 0.6f)]
        [SerializeField] private float startThreshold = 0.35f;

        [Tooltip("Seconds between beats at the threshold (calm-ish) and at near-death (frantic).")]
        [SerializeField] private float slowInterval = 1.15f;
        [SerializeField] private float fastInterval = 0.55f;

        private AudioSource _source;
        private AudioClip _lubDub;
        private float _nextBeat;
        private float _severity = -1f;   // -1 = silent; 0..1 = how close to death

        private void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;        // inside your own chest, not in the world
            _source.priority = 64;
            _lubDub = SynthesizeLubDub();
        }

        private IPlayerState _player;

        private void OnDisable()
        {
            if (_player != null) { _player.OnHealthChanged -= OnHealthChanged; _player = null; }
        }

        /// Debt D4: severity is recomputed only when the replicated Health changes (Fusion
        /// OnChangedRender), not polled 4x/s. Beat timing still runs every frame for precision.
        private void OnHealthChanged(int health, int max)
        {
            _severity = -1f;
            if (_player != null && _player.IsAlive && health > 0 && max > 0)
            {
                float frac = health / (float)max;
                if (frac <= startThreshold)
                    _severity = 1f - Mathf.Clamp01(frac / startThreshold);   // 0 at threshold, 1 near death
            }
        }

        private void Update()
        {
            // Bind once to the local player (it can be replaced after a game over: re-bind then).
            var current = NetworkSession.Current?.GetPlayer();
            if (current != _player)
            {
                if (_player != null) _player.OnHealthChanged -= OnHealthChanged;
                _player = current;
                _severity = -1f;
                if (_player != null)
                {
                    _player.OnHealthChanged += OnHealthChanged;
                    OnHealthChanged(_player.Health, _player.MaxHealth);   // initial state
                }
            }

            if (_severity < 0f) return;

            if (Time.time >= _nextBeat)
            {
                _nextBeat = Time.time + Mathf.Lerp(slowInterval, fastInterval, _severity);
                _source.PlayOneShot(_lubDub, Mathf.Lerp(0.35f, 0.95f, _severity));
            }
        }

        /// One heartbeat: two low thumps ("lub" then a softer "dub"), each a decaying sine sweep
        /// (~62Hz falling to ~38Hz). Pure math, no imported audio.
        private static AudioClip SynthesizeLubDub()
        {
            const int rate = 44100;
            const float length = 0.55f;
            var samples = new float[(int)(rate * length)];

            void Thump(float startTime, float amplitude)
            {
                int start = (int)(startTime * rate);
                const float dur = 0.14f;
                int count = (int)(dur * rate);
                float phase = 0f;
                for (int i = 0; i < count && start + i < samples.Length; i++)
                {
                    float t = i / (float)count;                       // 0..1 inside the thump
                    float freq = Mathf.Lerp(62f, 38f, t);             // pitch falls like a real valve
                    phase += 2f * Mathf.PI * freq / rate;
                    float envelope = Mathf.Exp(-6f * t);              // fast decay
                    samples[start + i] += Mathf.Sin(phase) * envelope * amplitude;
                }
            }

            Thump(0.00f, 0.9f);    // lub
            Thump(0.17f, 0.6f);    // dub

            var clip = AudioClip.Create("LubDub", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
