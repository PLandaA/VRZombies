using System.Collections.Generic;
using UnityEngine;
using VRZ.Core;

namespace VRZ.FX
{
    /// Event Queue for presentation feedback (popups, one-shot sounds). Gameplay code enqueues
    /// through IFeedbackSink; the queue flushes once per frame in LateUpdate with two things a
    /// direct call can't do:
    ///   COALESCING  - several kills in one frame become ONE "x N" popup instead of six overlapping
    ///   BUDGETING   - at most MaxSameSoundPerFrame plays of the same clip per frame
    /// Sounds play through a pooled set of 3D AudioSources (no PlayClipAtPoint GameObject churn).
    /// Registers itself as the game's feedback sink on startup (Core ships no default).
    public class FeedbackQueue : MonoBehaviour, IFeedbackSink
    {
        private static FeedbackQueue _instance;
        public static FeedbackQueue Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[FeedbackQueue]");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<FeedbackQueue>();
                }
                return _instance;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RegisterAsSink() => Feedback.Override(Instance);

        // ── Kill popups ──
        private struct KillEvent { public bool Headshot; public Vector3 Position; }
        private readonly List<KillEvent> _kills = new();

        private const float ComboWindow = 2.2f;
        private float _lastKillTime = -99f;
        private int _comboCount;

        public void EnqueueKill(bool headshot, Vector3 worldPos) =>
            _kills.Add(new KillEvent { Headshot = headshot, Position = worldPos });

        // ── One-shot 3D sounds ──
        private struct SoundEvent { public AudioClip Clip; public Vector3 Position; public float Volume; }
        private readonly List<SoundEvent> _sounds = new();
        private readonly Dictionary<AudioClip, int> _playsThisFrame = new();
        private readonly List<AudioSource> _voices = new();
        private const int VoiceCount = 12;
        private const int MaxSameSoundPerFrame = 2;

        public void EnqueueSound(AudioClip clip, Vector3 position, float volume = 1f)
        {
            if (clip == null) return;
            _sounds.Add(new SoundEvent { Clip = clip, Position = position, Volume = volume });
        }

        private void Awake()
        {
            for (int i = 0; i < VoiceCount; i++)
            {
                var v = new GameObject("Voice" + i).AddComponent<AudioSource>();
                v.transform.SetParent(transform, false);
                v.playOnAwake = false;
                v.spatialBlend = 1f;
                v.rolloffMode = AudioRolloffMode.Logarithmic;
                v.minDistance = 1.5f;
                v.maxDistance = 40f;
                _voices.Add(v);
            }
        }

        private void LateUpdate()
        {
            FlushKills();
            FlushSounds();
        }

        /// All kills of this frame collapse into a single combo step and a single popup.
        private void FlushKills()
        {
            if (_kills.Count == 0) return;

            int n = _kills.Count;
            Vector3 center = Vector3.zero;
            foreach (var k in _kills) center += k.Position;
            center /= n;

            if (Time.time - _lastKillTime <= ComboWindow) _comboCount += n;
            else _comboCount = n;
            _lastKillTime = Time.time;
            _kills.Clear();

            if (_comboCount < 2) return;
            string label = _comboCount == 2 ? "DOUBLE KILL!"
                         : _comboCount == 3 ? "TRIPLE KILL!"
                         : n >= 3 ? "MULTIKILL x" + _comboCount
                         : "MEGA KILL x" + _comboCount;
            float size = Mathf.Min(3.2f + _comboCount * 0.35f, 5f);
            PopupText.Spawn(center + Vector3.up * 2.3f, label, new Color(1f, 0.45f, 0.1f), size, 1.5f, 1.2f);
        }

        /// Plays queued sounds through the voice pool, capping identical clips per frame.
        private void FlushSounds()
        {
            if (_sounds.Count == 0) return;
            _playsThisFrame.Clear();

            foreach (var s in _sounds)
            {
                _playsThisFrame.TryGetValue(s.Clip, out int plays);
                if (plays >= MaxSameSoundPerFrame) continue;
                _playsThisFrame[s.Clip] = plays + 1;

                var voice = TakeVoice();
                voice.transform.position = s.Position;
                voice.clip = s.Clip;
                voice.volume = s.Volume;
                voice.pitch = Random.Range(0.94f, 1.06f);
                voice.Play();
            }
            _sounds.Clear();
        }

        private AudioSource TakeVoice()
        {
            AudioSource oldest = _voices[0];
            float oldestTime = float.MaxValue;
            foreach (var v in _voices)
            {
                if (!v.isPlaying) return v;
                float remaining = v.clip != null ? v.clip.length - v.time : 0f;
                if (remaining < oldestTime) { oldestTime = remaining; oldest = v; }
            }
            return oldest;
        }
    }
}
