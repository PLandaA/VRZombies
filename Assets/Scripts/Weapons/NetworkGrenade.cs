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
    /// Networked belt grenade (Fusion 2 Shared Mode): personal to the client that spawned it
    /// (the partner cannot grab it), arms when thrown (released off the belt),
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

            // Netcode fix A4: grenades are personal. Only the client that spawned this one (its
            // State Authority) may grab it; the partner's hands ignore it (no highlight, no grab,
            // no distance grab). AutoHand checks this flag in Grabbable.CanGrab, which every grab
            // path goes through. The prefab has no AllowStateAuthorityOverride, so authority can
            // never move: deciding once here is enough.
            _grabbable.isGrabbable = Object.HasStateAuthority;

            // Late joiner or state already exploded before we spawned locally
            if (Exploded) HideVisuals();
            base.Spawned();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            _grabbable.OnBeforeGrabEvent -= OnBeforeGrabbed;
            _grabbable.OnGrabEvent -= OnGrabbed;
            _grabbable.OnReleaseEvent -= OnReleased;
            // Detach from the belt PlacePoint while still alive (see NetworkAutoAmmo.Despawned).
            if (_grabbable.placePoint != null)
                _grabbable.placePoint.Remove(_grabbable);
            base.Despawned(runner, hasState);
        }

        // ── Grab: only ever runs on the owner (see Spawned) ──────────────────────────────

        private void OnBeforeGrabbed(Hand hand, Grabbable grabbable)
        {
            // The belt PlacePoint made the body kinematic while parked; free it for the hand joint.
            if (_grabbable.body != null)
                _grabbable.body.isKinematic = false;
        }

        private void OnGrabbed(Hand hand, Grabbable grabbable)
        {
            // Nothing to do: the owner already holds State Authority. The old
            // RequestStateAuthority() here could never succeed for anyone else anyway (the
            // prefab does not allow authority override) and only masked the problem.
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
                int dmg = GrenadeRules.ZombieDamage(dist, explosionRadius, zombieDamage);   // pure rule, unit-tested
                // Netcode fix A5: no kill prediction; the victim's State Authority names the killer.
                target.ApplyDamage(new DamageInfo(dmg, target.Position, transform.position));
            }

            var session = NetworkSession.Current;
            if (playerDamage <= 0 || session == null) return;

            // Local physical body of the thrower
            DamagePlayerIfInRange(session.GetPlayer(),
                AutoHandPlayer.Instance != null ? AutoHandPlayer.Instance.transform : null);

            // Remote avatars, from the registry (debt D3: no scene scan per explosion)
            var nm = NetworkManager.instance;
            if (nm == null) return;
            foreach (var rig in nm.Rigs)
            {
                if (rig == null || rig.Object == null || !rig.Object.IsValid) continue;
                bool isLocal = rig.Object.StateAuthority == Runner.LocalPlayer ||
                               rig.Object.InputAuthority == Runner.LocalPlayer;
                if (isLocal) continue;
                // InputAuthority is set by Runner.Spawn(..., playerRef) in MapDefault; fall back to
                // StateAuthority like NetworkZombie.ResolveNetworkPlayer does, just in case.
                var owner = rig.Object.InputAuthority != PlayerRef.None ? rig.Object.InputAuthority : rig.Object.StateAuthority;
                DamagePlayerIfInRange(session.GetPlayer(owner), rig.transform);
            }
        }

        private void DamagePlayerIfInRange(IPlayerState np, Transform body)
        {
            if (np == null || body == null) return;
            if (!np.IsValid) return;
            IDamageable target = np;
            if (!target.IsAlive) return;
            if (!GrenadeRules.HitsPlayer(Vector3.Distance(body.position, transform.position), explosionRadius)) return;
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
