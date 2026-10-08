using UnityEngine;
using Fusion;
using Autohand;

namespace VRZ.Weapons
{

    [RequireComponent(typeof(AutoAmmo))]
    [RequireComponent(typeof(NetworkObject))]
    /// Syncs AutoHand magazine state (ammo count, insertion into the rifle) across clients.
    public class NetworkAutoAmmo : NetworkBehaviour
    {

        [Networked, OnChangedRender(nameof(OnAmmoChanged))]
        public int NetworkedAmmo { get; private set; }

        private AutoAmmo  _autoAmmo;
        private Grabbable _grabbable;

        private void Awake()
        {
            _autoAmmo  = GetComponent<AutoAmmo>();
            _grabbable = GetComponent<Grabbable>();
        }

        public override void Spawned()
        {
            if (Object.HasStateAuthority)
                NetworkedAmmo = _autoAmmo.currentAmmo;
            else
                _autoAmmo.SetAmmo(NetworkedAmmo);

            // Runner.Spawn(prefab, pos, rot) moves the Transform, not the Rigidbody's internal pose.
            // With Physics.autoSyncTransforms off and Fusion driving Physics.Simulate, the first
            // physics step then writes the body's stale pose (the prefab's) back over the transform:
            // dispenser magazines "teleported" to one fixed spot (2026-10-02). Sync the body here.
            if (_grabbable.body != null)
            {
                _grabbable.body.position = transform.position;
                _grabbable.body.rotation = transform.rotation;
            }

            _grabbable.OnBeforeGrabEvent += OnBeforeGrabbed;
            _grabbable.OnGrabEvent += OnGrabbed;
            base.Spawned();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            _grabbable.OnBeforeGrabEvent -= OnBeforeGrabbed;
            _grabbable.OnGrabEvent -= OnGrabbed;

            // Leave the PlacePoint NOW, while the object is still alive. Despawned runs before
            // Unity's OnDestroy; if we wait, AutoHand's Grabbable.OnDestroy -> PlacePoint.Remove
            // tries to re-parent a GameObject that is already being destroyed and logs
            // "Cannot set the parent ... while it is being destroyed" on every runner shutdown.
            if (_grabbable.placePoint != null)
                _grabbable.placePoint.Remove(_grabbable);

            base.Despawned(runner, hasState);
        }

        public override void FixedUpdateNetwork()
        {
            if (Object.HasStateAuthority && NetworkedAmmo != _autoAmmo.currentAmmo)
                NetworkedAmmo = _autoAmmo.currentAmmo;

            if (Object.HasStateAuthority) DespawnWhenSpentAndLoose();

            base.FixedUpdateNetwork();
        }

        [Tooltip("Seconds an EMPTY magazine may float loose (not in a rifle, not in a hand) before it is despawned. Long enough to see it eject, short enough not to litter the arena. Intermission cleanup still catches anything else.")]
        [SerializeField] private float emptyLingerSeconds = 3f;
        private TickTimer _spentTimer;

        /// Empty magazines used to float where they were ejected (FloatingWeapon: no gravity) until
        /// the intermission cleanup; a long wave left the arena littered with them. The State
        /// Authority (the player who used it) despawns a spent, loose magazine after a short linger.
        private void DespawnWhenSpentAndLoose()
        {
            bool spentAndLoose = _autoAmmo.currentAmmo <= 0 && transform.parent == null && !_grabbable.IsHeld();
            if (!spentAndLoose) { _spentTimer = TickTimer.None; return; }

            if (!_spentTimer.IsRunning) { _spentTimer = TickTimer.CreateFromSeconds(Runner, emptyLingerSeconds); return; }
            if (_spentTimer.Expired(Runner)) Runner.Despawn(Object);
        }

        private void OnAmmoChanged()
        {
            if (Object.HasStateAuthority) return;
            _autoAmmo.SetAmmo(NetworkedAmmo);
        }

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
    }
}
