using System.Collections;
using UnityEngine;
using TMPro;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.World;

namespace VRZ.FX
{

    /// Round drama: a floating "ROUND N" banner in front of the local player when a wave starts
    /// (+ optional horde howl), and two-layer ambient music that crossfades with horde pressure
    /// (calm during intermission, intense as living zombies pile up).
    public class RoundJuice : MonoBehaviour
    {
        [Header("Wiring")]
        [SerializeField] private ZombieSpawner spawner;

        [Header("Round Banner")]
        [SerializeField] private float bannerSeconds = 2.6f;
        [SerializeField] private AudioClip hordeHowl;

        [Header("Music Layers")]
        [SerializeField] private AudioClip calmLayer;
        [SerializeField] private AudioClip intenseLayer;
        [Range(0f, 1f)] [SerializeField] private float musicVolume = 0.35f;
        [Tooltip("Living zombies at which the intense layer reaches full blend")]
        [SerializeField] private int zombiesForFullIntensity = 8;

        private AudioSource _calm;
        private AudioSource _intense;
        private float _blend;

        private void Start()
        {
            _calm = MakeLayer(calmLayer);
            _intense = MakeLayer(intenseLayer);
            if (_intense != null) _intense.volume = 0f;

            if (spawner == null) spawner = FindFirstObjectByType<ZombieSpawner>();
            if (spawner != null)
            {
                spawner.OnWaveStarted.AddListener(OnWaveStarted);
                spawner.OnIntermissionStarted.AddListener(OnRoundCleared);
            }

            StartCoroutine(MusicBlendLoop());
        }

        /// Cinematic beat: brief slow-motion the instant the round's last zombie falls.
        private void OnRoundCleared(int nextWave)
        {
            if (_slowMo == null) _slowMo = StartCoroutine(SlowMo());
        }

        private Coroutine _slowMo;

        private IEnumerator SlowMo()
        {
            Debug.Log("[SlowMo] Round clear -- cinematic beat");
            float baseScale = Time.timeScale;
            float baseFixed = Time.fixedDeltaTime;
            Time.timeScale = 0.45f;
            Time.fixedDeltaTime = baseFixed * 0.45f;

            // The world is mostly STILL at round clear (the last zombie just died), so the ear
            // carries the moment: both music layers drop pitch like a slowed tape
            if (_calm != null) _calm.pitch = 0.55f;
            if (_intense != null) _intense.pitch = 0.55f;

            yield return new WaitForSecondsRealtime(0.85f);

            Time.timeScale = baseScale;
            Time.fixedDeltaTime = baseFixed;
            if (_calm != null) _calm.pitch = 1f;
            if (_intense != null) _intense.pitch = 1f;
            _slowMo = null;
        }

        private AudioSource MakeLayer(AudioClip clip)
        {
            if (clip == null) return null;
            var src = gameObject.AddComponent<AudioSource>();
            src.clip = clip;
            src.loop = true;
            src.volume = musicVolume;
            src.spatialBlend = 0f;
            src.Play();
            return src;
        }

        private void OnWaveStarted(int wave)
        {
            StartCoroutine(ShowBanner(wave));
            if (hordeHowl != null && Camera.main != null)
                AudioSource.PlayClipAtPoint(hordeHowl, Camera.main.transform.position, 0.8f);
        }

        private IEnumerator ShowBanner(int wave)
        {
            var cam = Camera.main;
            if (cam == null) yield break;

            var go = new GameObject("RoundBanner");
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = "<size=60%>ROUND</size>\n" + wave;
            tmp.fontSize = 4.5f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.85f, 0.1f, 0.08f);
            tmp.rectTransform.sizeDelta = new Vector2(3f, 2f);

            float t = 0f;
            while (t < bannerSeconds)
            {
                t += Time.deltaTime;
                var head = cam.transform;
                go.transform.position = head.position + head.forward * 2.2f + Vector3.up * 0.25f;
                go.transform.rotation = Quaternion.LookRotation(go.transform.position - head.position);
                float a = Mathf.Clamp01(Mathf.Min(t * 3f, (bannerSeconds - t) * 2f));
                var c = tmp.color; c.a = a; tmp.color = c;
                yield return null;
            }
            Destroy(go);
        }

        private IEnumerator MusicBlendLoop()
        {
            var wait = new WaitForSeconds(1.5f);
            while (true)
            {
                int alive = 0;
                foreach (var z in FindObjectsByType<NetworkZombie>(FindObjectsSortMode.None))
                    if (z.State != NetworkZombie.ZombieState.Dead) alive++;

                float target = Mathf.Clamp01((float)alive / Mathf.Max(1, zombiesForFullIntensity));
                _blend = Mathf.MoveTowards(_blend, target, 0.25f);

                if (_calm != null) _calm.volume = musicVolume * (1f - _blend * 0.7f);
                if (_intense != null) _intense.volume = musicVolume * _blend;
                yield return wait;
            }
        }
    }
}
