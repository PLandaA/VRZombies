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

    /// Death celebration: blood puff on every client + floating score popup ("+10" / "+25 HEADSHOT")
    /// only on the killer's client (netcode fix A5: the zombie's replicated LastDamager decides).
    /// Death sounds live natively in NetworkZombie. Subscribes to OnDiedRender.
    public class ZombieJuice : MonoBehaviour
    {
        // Points come from ScoreEvents (single source of truth) so the popup can never disagree
        // with what the scoreboard actually adds.
        private static int KillPoints => ScoreEvents.KillPoints;
        private static int HeadshotPoints => ScoreEvents.HeadshotPoints;

        [Header("Blood Puff")]
        [Tooltip("Optional: your own blood VFX prefab. When assigned it replaces the procedural puff.")]
        [SerializeField] private GameObject bloodPrefabOverride;
        [SerializeField] private Color bloodColor = new Color(0.45f, 0.03f, 0.03f, 0.9f);

        private NetworkZombie _zombie;
        private static Material _bloodMat;

        private void Awake() { _zombie = GetComponent<NetworkZombie>(); }
        private void OnEnable() { if (_zombie != null) _zombie.OnDiedRender += OnDied; }
        private void OnDisable() { if (_zombie != null) _zombie.OnDiedRender -= OnDied; }

        private void OnDied(bool headshot)
        {
            Vector3 pos = transform.position + Vector3.up * 1.3f;
            BloodPuff(pos);
            if (_zombie != null && _zombie.KilledByLocalPlayer)
                SpawnPopup(pos + Vector3.up * 0.4f, headshot);
        }

        private void BloodPuff(Vector3 pos)
        {
            // User-authored VFX takes over when assigned
            if (bloodPrefabOverride != null)
            {
                var vfx = Instantiate(bloodPrefabOverride, pos, Quaternion.identity);
                var vfxPs = vfx.GetComponentInChildren<ParticleSystem>();
                if (vfxPs != null && !vfxPs.isPlaying) vfxPs.Play();
                Destroy(vfx, 4f);
                return;
            }

            if (_bloodMat == null)
            {
                _bloodMat = new Material(Shader.Find("Sprites/Default"));
            }
            var go = new GameObject("BloodPuff");
            go.transform.position = pos;
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.2f, 3.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.22f);
            main.startColor = bloodColor;
            main.gravityModifier = 0.7f;
            main.maxParticles = 30;
            main.loop = false;
            var em = ps.emission;
            em.rateOverTime = 0f;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)22) });
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = 0.15f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = _bloodMat;
            ps.Play();
            Destroy(go, 1.5f);
        }

        private void SpawnPopup(Vector3 pos, bool headshot)
        {
            PopupText.Spawn(pos,
                headshot ? "+" + HeadshotPoints + "\n<size=55%>HEADSHOT!</size>" : "+" + KillPoints,
                headshot ? new Color(1f, 0.75f, 0.15f) : new Color(1f, 0.95f, 0.8f),
                headshot ? 2.6f : 2.1f);
        }
    }
}
