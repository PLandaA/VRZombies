using UnityEngine;
using Fusion;
using Autohand;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Enemies;
using VRZ.FX;
using VRZ.World;

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

            base.FixedUpdateNetwork();
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
