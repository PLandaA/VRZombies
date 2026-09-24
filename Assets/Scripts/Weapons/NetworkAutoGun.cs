using UnityEngine;
using VRZ.Core;
using System.Reflection;
using Fusion;
using Autohand;
using VRZ.FX;

namespace VRZ.Weapons
{

    [RequireComponent(typeof(AutoGun))]
    [RequireComponent(typeof(NetworkObject))]
    /// Networked rifle: tick-synced shot effects and shooter-authoritative hit damage over Fusion 2 Shared Mode.
    public class NetworkAutoGun : NetworkBehaviour
    {
        // No NetworkedAmmo here: it would mirror the magazine's count, which
        // NetworkAutoAmmo already replicates, and nothing ever read it.
        [Networked] private int LastShootTick { get; set; }
        [Networked] private Vector3 SlideLocalPosition { get; set; }
        [Networked, OnChangedRender(nameof(OnLoadedMagChanged))]
        public NetworkBehaviourId LoadedMagId { get; private set; }

        /// Round in the chamber (AutoGun.slideLoaded), replicated (2026-09-21). AutoHand keeps it
        /// as a private per-client flag, so a rifle racked by player A and picked up by player B
        /// would not fire on B until B racked it again. The owner mirrors the flag each tick;
        /// proxies copy it into their AutoGun by reflection, so whoever takes authority next
        /// already has the right chamber state. We cannot call LoadSlide(): it consumes a round.
        [Networked, OnChangedRender(nameof(OnChamberedChanged))]
        private NetworkBool Chambered { get; set; }

        private static readonly FieldInfo SlideLoadedField =
            typeof(AutoGun).GetField("slideLoaded", BindingFlags.NonPublic | BindingFlags.Instance);

        [Header("Slide")]
        [SerializeField] private Transform slideTransform;
        [SerializeField] private float slideInterpolationSpeed = 25f;

        [Header("Damage")]
        [SerializeField] private int bulletDamage = 25;

        private AutoGun _gun;
        private AutoGunEffects _effects;
        private Grabbable _grabbable;
        private ChangeDetector _changes;
        private WeaponFeel _feel;
        private BulletTracer _tracer;

        private void Awake()
        {
            _gun = GetComponent<AutoGun>();
            _effects = GetComponent<AutoGunEffects>();
            _grabbable = GetComponent<Grabbable>();
            _feel = GetComponent<WeaponFeel>();
            _tracer = GetComponent<BulletTracer>();

            // Feel fix (2026-09-20, "rifle rotates in snappy jumps while the empty hand is smooth"):
            // every Rigidbody starts with maxAngularVelocity = 7 rad/s (~400 deg/s). A VR wrist
            // flick exceeds that, and the rifle hangs off a joint, so it lagged behind the hand and
            // caught up in one jump. 40 was too much: grabbing by the front grip (long lever to the
            // centre of mass) made the joint oscillate violently. 15 rad/s (~860 deg/s) covers a
            // fast wrist turn without letting the joint ring.
            var body = GetComponent<Rigidbody>();
            if (body != null) body.maxAngularVelocity = 15f;

            // Self-wire: the serialized slideTransform historically pointed at the rifle root,
            // which made the remote slide-lerp fight the root NetworkRigidbody3D. Find the real slide.
            if (slideTransform == null || slideTransform == transform.root)
            {
                foreach (var t in transform.root.GetComponentsInChildren<Transform>(true))
                    if (t.name.Trim().StartsWith("Slide")) { slideTransform = t; break; }
            }
        }

        public override void Spawned()
        {
            _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);
            _gun.OnShoot.AddListener(OnLocalShoot);
            _gun.OnHitEvent.AddListener(OnLocalHit);
            _gun.OnAmmoPlaceEvent.AddListener(OnLocalAmmoPlace);
            _gun.OnAmmoRemoveEvent.AddListener(OnLocalAmmoRemove);
            _grabbable.OnBeforeGrabEvent += OnBeforeGrabbed;
            _grabbable.OnGrabEvent += OnGrabbed;
            foreach (var childGrab in GetComponentsInChildren<Grabbable>(true))
            {
                if (childGrab != _grabbable)
                    childGrab.OnBeforeGrabEvent += OnChildGrabbed;

                // Authority-on-approach: request ownership the moment a hand HIGHLIGHTS any part
                // of the weapon. The network round-trip completes while the player is still
                // reaching, so by grab time the client already owns it -- this kills the
                // "heavier than normal for the client" first-grab feel (the master never felt it
                // because scene objects start under its authority).
                childGrab.OnHighlightEvent += OnAnyHighlight;
            }
            if (Object.HasStateAuthority)
            {
                if (slideTransform != null)
                    SlideLocalPosition = slideTransform.localPosition;
                Chambered = _gun.IsSlideLoaded();
            }
            else
            {
                OnChamberedChanged();   // proxies: take the owner's chamber state from the start
            }
            OnLoadedMagChanged();
            base.Spawned();
        }

        /// Proxies mirror the owner's chamber flag into their own AutoGun (see Chambered).
        private void OnChamberedChanged()
        {
            if (Object.HasStateAuthority || _gun == null || SlideLoadedField == null) return;
            if (_gun.IsSlideLoaded() != (bool)Chambered)
                SlideLoadedField.SetValue(_gun, (bool)Chambered);
        }

        // ── Held-without-authority watchdog (2026-09-21, "rifle shakes violently in the
        // NON-master's hands"). If our authority request was dropped, the hand joint pulls the
        // rifle one way while NetworkRigidbody3D (proxy) snaps it back to the owner's pose every
        // frame: exactly a violent shake. Keep asking while we hold it, and say so in the log.
        private float _nextAuthorityRetry;
#if VRZ_NET_DIAGNOSTICS
        private float _nextHeldLog;
#endif

        private void Update()
        {
            if (Object == null || !Object.IsValid || _grabbable == null) return;
            bool heldLocally = _grabbable.IsHeld();
            if (!heldLocally) return;

            if (!Object.HasStateAuthority && Time.time >= _nextAuthorityRetry)
            {
                _nextAuthorityRetry = Time.time + 0.5f;
                Object.RequestStateAuthority();
                Debug.LogWarning("[NetGun] Held but no State Authority (owner=" + Object.StateAuthority + "): re-requesting.");
            }

#if VRZ_NET_DIAGNOSTICS
            if (Time.time >= _nextHeldLog)
            {
                _nextHeldLog = Time.time + 1f;
                var hand = _grabbable.GetHeldBy() != null && _grabbable.GetHeldBy().Count > 0 ? _grabbable.GetHeldBy()[0] : null;
                var body = _grabbable.body;
                string gap = (hand != null && body != null)
                    ? (Vector3.Distance(body.position, hand.handGrabPoint.position) * 100f).ToString("F1") + " cm"
                    : "n/a";
                Debug.Log("[NetGun] held: authority=" + Object.HasStateAuthority + " owner=" + Object.StateAuthority + " master=" + Runner.IsSharedModeMasterClient + " body-hand gap=" + gap + " kinematic=" + (body != null && body.isKinematic));
            }
#endif
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            _gun.OnShoot.RemoveListener(OnLocalShoot);
            _gun.OnHitEvent.RemoveListener(OnLocalHit);
            _gun.OnAmmoPlaceEvent.RemoveListener(OnLocalAmmoPlace);
            _gun.OnAmmoRemoveEvent.RemoveListener(OnLocalAmmoRemove);
            _grabbable.OnBeforeGrabEvent -= OnBeforeGrabbed;
            _grabbable.OnGrabEvent -= OnGrabbed;
            foreach (var childGrab in GetComponentsInChildren<Grabbable>(true))
            {
                if (childGrab != _grabbable)
                    childGrab.OnBeforeGrabEvent -= OnChildGrabbed;
                childGrab.OnHighlightEvent -= OnAnyHighlight;
            }
            base.Despawned(runner, hasState);
        }

        public override void FixedUpdateNetwork()
        {
            if (Object.HasStateAuthority)
            {
                if (slideTransform != null && SlideLocalPosition != slideTransform.localPosition)
                    SlideLocalPosition = slideTransform.localPosition;
                // Owner is the source of truth for the chamber; a proxy that just took authority
                // already has the right flag (OnChamberedChanged), so this is a no-op for it.
                bool loaded = _gun.IsSlideLoaded();
                if ((bool)Chambered != loaded) Chambered = loaded;
            }
            base.FixedUpdateNetwork();
        }

        public override void Render()
        {
            foreach (var change in _changes.DetectChanges(this, out _, out _))
            {
                if (change == nameof(LastShootTick) && !Object.HasStateAuthority)
                    PlayRemoteShootEffects();
            }
            if (!Object.HasStateAuthority && slideTransform != null)
            {
                slideTransform.localPosition = Vector3.Lerp(slideTransform.localPosition, SlideLocalPosition, Time.deltaTime * slideInterpolationSpeed);
            }

            if (LoadedMagId.IsValid && _gun != null && _gun.magazinePoint != null && Runner != null &&
                Runner.TryFindBehaviour(LoadedMagId, out NetworkBehaviour magBeh2))
            {
                if (magBeh2.transform.parent != _gun.magazinePoint.transform)
                    magBeh2.transform.SetParent(_gun.magazinePoint.transform, true);
                magBeh2.transform.localPosition = Vector3.zero;
                magBeh2.transform.localRotation = Quaternion.identity;
            }
        }

        private void OnAnyHighlight(Hand hand, Grabbable g)
        {
            if (Object != null && Object.IsValid && !Object.HasStateAuthority)
                Object.RequestStateAuthority();
        }

        private void OnBeforeGrabbed(Hand hand, Grabbable grabbable)
        {
            Object.RequestStateAuthority();
            if (_grabbable.body != null)
                _grabbable.body.isKinematic = false;
            if (LoadedMagId.IsValid && Runner.TryFindBehaviour(LoadedMagId, out NetworkBehaviour magBeh))
                magBeh.Object.RequestStateAuthority();
        }

        private void OnChildGrabbed(Hand hand, Grabbable childGrabbable)
        {
            Object.RequestStateAuthority();
            MarkLocalPlayerReady();
            if (_grabbable.body != null)
                _grabbable.body.isKinematic = false;
            if (LoadedMagId.IsValid && Runner.TryFindBehaviour(LoadedMagId, out NetworkBehaviour magBeh))
                magBeh.Object.RequestStateAuthority();
        }

            private void OnGrabbed(Hand hand, Grabbable grabbable)
        {
            Object.RequestStateAuthority();
            MarkLocalPlayerReady();
        }

        private void MarkLocalPlayerReady()
        {
            var np = NetworkSession.Current?.GetPlayer();
            if (np != null && !np.Ready)
            {
                np.Ready = true;
                Debug.Log("[NetGun] Local player marked READY (grabbed a weapon)");
            }
        }

        private void OnLocalAmmoPlace(AutoGun gun, AutoAmmo ammo)
        {
            if (ammo == null) return;
            var netAmmo = ammo.GetComponent<NetworkAutoAmmo>();
            if (netAmmo == null) { Debug.LogWarning("[NetGun] Placed ammo has no NetworkAutoAmmo: " + ammo.name); return; }
            netAmmo.Object.RequestStateAuthority();
            if (Object.HasStateAuthority)
                LoadedMagId = netAmmo.Id;
        }

        private void OnLocalAmmoRemove(AutoGun gun, AutoAmmo ammo)
        {
            if (Object.HasStateAuthority)
                LoadedMagId = default;
        }

        /// The magazine this client parented under the magazine point (proxies only; the owner's
        /// magazine is parented by AutoHand's PlacePoint). Needed to UNparent it on removal.
        private Transform _parentedMag;

        private void OnLoadedMagChanged()
        {
            Transform magPP = (_gun != null && _gun.magazinePoint != null) ? _gun.magazinePoint.transform : null;

            if (!LoadedMagId.IsValid || Runner == null || magPP == null)
            {
                // Bug fix (2026-09-21, "ghost magazine somewhere else in the map, cannot be grabbed"):
                // the mag was ejected on the owner but proxies kept it parented under this rifle.
                // NetworkTransform (SyncParent off) replicates LOCAL coordinates, so the owner's
                // world position was being applied as a local offset from the magazine point:
                // the proxy mag rendered at rifle + world coords, re-pinned there every frame.
                UnparentMag();
                return;
            }
            if (!Runner.TryFindBehaviour(LoadedMagId, out NetworkBehaviour magBeh)) { Debug.LogWarning("[NetGun] LoadedMagId set but behaviour not found"); return; }

            if (_parentedMag != null && _parentedMag != magBeh.transform) UnparentMag();   // a different mag replaced the old one
            if (!Object.HasStateAuthority)
            {
                // Proxies only: the owner's mag is already parented by AutoHand's PlacePoint.
                magBeh.transform.SetParent(magPP, true);
                magBeh.transform.localPosition = Vector3.zero;
                magBeh.transform.localRotation = Quaternion.identity;
                _parentedMag = magBeh.transform;
            }
            var autoAmmo = magBeh.GetComponent<AutoAmmo>();
            if (autoAmmo != null && _gun != null)
            {
                var field = typeof(AutoGun).GetField("loadedAmmo", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) field.SetValue(_gun, autoAmmo);
            }
        }

        private void UnparentMag()
        {
            if (_parentedMag == null) return;
            // Keep world pose: NetworkTransform's next Render brings it to the owner's real place.
            if (_parentedMag.parent != null) _parentedMag.SetParent(null, true);
            _parentedMag = null;
        }

        private void OnLocalHit(AutoGun gun, RaycastHit hit)
        {
            if (hit.collider == null) return;

            // Tracer: muzzle -> impact point (local shooter's view)
            if (_tracer != null && _gun != null && _gun.shootForward != null)
                _tracer.Fire(_gun.shootForward.position, hit.point);

            // Anything that implements IDamageable can be shot; the target decides what counts
            // as a critical hit (zombie head, barrel valve...) -- the gun never learns its type.
            var target = hit.collider.GetComponentInParent<IDamageable>();
            if (target != null && target.IsAlive)
            {
                bool critical = target.IsCriticalHit(hit.point);
                int dmg = critical ? bulletDamage * 2 : bulletDamage;
                // No kill prediction here. The zombie's State Authority decides
                // who killed it (LastDamager) and NetworkZombie.Render credits the local player.
                target.ApplyDamage(new DamageInfo(dmg, hit.point, _gun.shootForward.position, critical));
                if (_feel != null) _feel.PlayHitmarker(critical);
            }
        }

        private void OnLocalShoot(AutoGun gun)
        {
            if (!Object.HasStateAuthority) return;
            LastShootTick = Runner.Tick;
        }

        private void PlayRemoteShootEffects()
        {
            if (_feel != null) _feel.PlayShotVisuals();

            // Remote tracer: straight ray from the muzzle (we don't know the exact hit point)
            if (_tracer != null && _gun != null && _gun.shootForward != null)
                _tracer.Fire(_gun.shootForward.position, _gun.shootForward.position + _gun.shootForward.forward * 30f);

            if (_effects == null) return;
            if (_effects.shootSound != null && _effects.shootSound.clip != null)
                _effects.shootSound.PlayOneShot(_effects.shootSound.clip);
            if (_effects.shootParticle != null)
            {
                var p = _effects.shootParticle;
                PrefabPool.Spawn(p.gameObject, _gun.shootForward.position, _gun.shootForward.rotation, p.main.duration + 0.5f);
            }
            _effects.EjectShell();
        }
    }
}
