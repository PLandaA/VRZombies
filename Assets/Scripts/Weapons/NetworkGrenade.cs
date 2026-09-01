using System.Collections.Generic;
using UnityEngine;
using VRZ.Core;
using Fusion;
using Autohand;
using VRZ.Network;
using VRZ.Player;
using VRZ.Enemies;
using VRZ.FX;
using VRZ.World;

namespace VRZ.Weapons
{

    [RequireComponent(typeof(Grabbable))]
    [RequireComponent(typeof(NetworkObject))]
    /// Networked belt grenade (Fusion 2 Shared Mode): arms when thrown (released off the belt),
    /// synced fuse via TickTimer, authoritative radius damage to zombies/players, explosion FX on every client.
    public class NetworkGrenade : NetworkBehaviour
    {
        [Header("Fuse")]
        [Tooltip("Seconds between the throw (release) and the explosion")]
        [SerializeField] private float fuseSeconds = 3f;

        [Tooltip("Seconds after release before deciding it was a throw and not a belt placement")]
        [SerializeField] private float armCheckDelay = 0.25f;

        [Header("Damage")]
        [Tooltip("Explosion radius in meters")]
        [SerializeField] private float explosionRadius = 6f;

        [Tooltip("Damage to zombies at the center (linear falloff to 25% at the edge)")]
        [SerializeField] private int zombieDamage = 150;

        [Tooltip("Damage to any player caught in the radius (0 = no friendly fire / self damage)")]
        [SerializeField] private int playerDamage = 25;

        [Header("Effects")]
        [Tooltip("Particle prefab instantiated at the explosion point on every client")]
        [SerializeField] private GameObject explosionEffect;

        [Tooltip("3D explosion sound played at the explosion point on every client")]
        [SerializeField] private AudioClip explosionSound;

        [Networked] private TickTimer FuseTimer { get; set; }
        [Networked] private TickTimer DespawnTimer { get; set; }

        [Networked, OnChangedRender(nameof(OnArmedChanged))]
        public NetworkBool Armed { get; private set; }

        [Networked, OnChangedRender(nameof(OnExplodedChanged))]
        public NetworkBool Exploded { get; private set; }

        private Grabbable _grabbable;
        private Transform _visual;
        private bool _fxDone;

        private void Awake()
        {
            _grabbable = GetComponent<Grabbable>();
            if (transform.childCount > 0)
                _visual = transform.GetChild(0);
        }

        public override void Spawned()
        {
            _grabbable.OnBeforeGrabEvent += OnBeforeGrabbed;
            _grabbable.OnGrabEvent += OnGrabbed;
            _grabbable.OnReleaseEvent += OnReleased;

            // Late joiner or state already exploded before we spawned locally
            if (Exploded) HideVisuals();
            base.Spawned();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            _grabbable.OnBeforeGrabEvent -= OnBeforeGrabbed;
            _grabbable.OnGrabEvent -= OnGrabbed;
            _grabbable.OnReleaseEvent -= OnReleased;
            base.Despawned(runner, hasState);
        }

        // ── Authority on grab (same pattern as NetworkAutoAmmo / NetworkAutoGun) ──

        private void OnBeforeGrabbed(Hand hand, Grabbable grabbable)
        {
            Object.RequestStateAuthority();
            if (_grabbable.body != null)
                _grabbable.body.isKinematic = false;
        }

        private void OnGrabbed(Hand hand, Grabbable grabbable)
        {
            Object.RequestStateAuthority();
        }

        private void OnReleased(Hand hand, Grabbable grabbable)
        {
            // Wait a beat: if it snapped onto a PlacePoint (belt), it was a store, not a throw.
            Invoke(nameof(ArmIfThrown), armCheckDelay);
        }

        private void ArmIfThrown()
        {
            if (this == null || Object == null || !Object.IsValid) return;
            if (!Object.HasStateAuthority) return;
            if (Armed || Exploded) return;
            if (_grabbable.placePoint != null) return;  // resting on the belt
            if (_grabbable.IsHeld()) return;            // re-grabbed mid air

            Armed = true;
            FuseTimer = TickTimer.CreateFromSeconds(Runner, fuseSeconds);
        }

        // ── Simulation (StateAuthority = the thrower) ──

        public override void FixedUpdateNetwork()
        {
            if (!Object.HasStateAuthority) return;

            if (Armed && !Exploded && FuseTimer.Expired(Runner))
            {
                Exploded = true;
                ApplyDamage();
                DespawnTimer = TickTimer.CreateFromSeconds(Runner, 0.6f);
            }

            if (Exploded && DespawnTimer.Expired(Runner))
                Runner.Despawn(Object);
        }

        /// StateAuthority only: authoritative damage through each victim's own damage RPC.
        private void ApplyDamage()
        {
            // Every IDamageable inside the blast (zombies, props, whatever comes next), once each,
            // with distance falloff. Players are handled separately below: their physical bodies
            // are not part of the networked hierarchy the overlap finds.
            var damaged = new HashSet<IDamageable>();
            foreach (var hit in Physics.OverlapSphere(transform.position, explosionRadius))
            {
                var target = hit.GetComponentInParent<IDamageable>();
                if (target == null || !target.IsAlive || target is IPlayerState) continue;
                if (!damaged.Add(target)) continue;

                float dist = Vector3.Distance(target.Position, transform.position);
                int dmg = Mathf.RoundToInt(Mathf.Lerp(zombieDamage, zombieDamage * 0.25f, Mathf.Clamp01(dist / explosionRadius)));
                bool killingBlow = target.Health <= dmg;
                target.ApplyDamage(new DamageInfo(dmg, target.Position, transform.position));
                if (killingBlow) ScoreEvents.RegisterKill(false, target.Position);
            }

            var session = NetworkSession.Current;
            if (playerDamage <= 0 || session == null) return;

            // Local physical body of the thrower
            DamagePlayerIfInRange(session.GetPlayer(),
                AutoHandPlayer.Instance != null ? AutoHandPlayer.Instance.transform : null);

            // Remote avatars
            foreach (var rig in FindObjectsByType<NetworkRig>(FindObjectsSortMode.None))
            {
                if (rig.Object == null || !rig.Object.IsValid) continue;
                bool isLocal = rig.Object.StateAuthority == Runner.LocalPlayer ||
                               rig.Object.InputAuthority == Runner.LocalPlayer;
                if (isLocal) continue;
                DamagePlayerIfInRange(session.GetPlayer(rig.Object.InputAuthority), rig.transform);
            }
        }

        private void DamagePlayerIfInRange(IPlayerState np, Transform body)
        {
            if (np == null || body == null) return;
            if (!np.IsValid) return;
            IDamageable target = np;
            if (!target.IsAlive) return;
            if (Vector3.Distance(body.position, transform.position) > explosionRadius) return;
            target.ApplyDamage(new DamageInfo(playerDamage, body.position, transform.position));
        }

        // ── Presentation (every client) ──

        private void Update()
        {
            // Armed warning: the shell pulses until it blows
            if (_visual != null && Armed && !Exploded)
            {
                float pulse = 1f + Mathf.Abs(Mathf.Sin(Time.time * 14f)) * 0.12f;
                _visual.localScale = Vector3.one * pulse;
            }
        }

        private void OnArmedChanged() { }

        private void OnExplodedChanged()
        {
            if (!Exploded || _fxDone) return;
            _fxDone = true;

            if (explosionEffect != null)
            {
                var fx = Instantiate(explosionEffect, transform.position, Quaternion.identity);
                if (fx.GetComponentInChildren<ExplosionLightFade>() == null)
                    fx.AddComponent<ExplosionLightFade>();          // warm light flash on any VFX
                foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>())
                    if (!ps.isPlaying) ps.Play();                    // defensive: fire even if PlayOnAwake is off
                Destroy(fx, 5f);
            }
            if (explosionSound != null)
                Feedback.Sink.EnqueueSound(explosionSound, transform.position, 1f);

            // Distance-based haptic rumble on the local player's hands (pure local juice)
            if (AutoHandPlayer.Instance != null)
            {
                float dist = Vector3.Distance(AutoHandPlayer.Instance.transform.position, transform.position);
                float maxDist = explosionRadius * 1.6f;
                if (dist < maxDist)
                {
                    float amp = Mathf.Lerp(1f, 0.15f, dist / maxDist);
                    if (AutoHandPlayer.Instance.handRight != null)
                        AutoHandPlayer.Instance.handRight.PlayHapticVibration(0.3f, amp);
                    if (AutoHandPlayer.Instance.handLeft != null)
                        AutoHandPlayer.Instance.handLeft.PlayHapticVibration(0.3f, amp);
                }
            }

            HideVisuals();
        }

        private void HideVisuals()
        {
            foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            if (_grabbable != null) _grabbable.isGrabbable = false;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, explosionRadius);
        }
    }
}
