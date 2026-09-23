using Fusion;
using Fusion.Addons.Physics;
using UnityEngine;
using Autohand;

namespace VRZ.Weapons
{
    /// A dropped weapon that nobody picks up for `returnAfterSeconds` goes back to where it started
    /// (its scene position at spawn). Prevents a rifle lost between rocks or launched by a grenade
    /// from ending the run.
    ///
    /// Networking: only the state authority (the last player who held it -- grabbing requests
    /// authority) counts down and teleports. NetworkRigidbody3D.Teleport snaps every client without
    /// interpolating the trip. If someone else grabs it first, authority moves and the old timer stops.
    [RequireComponent(typeof(NetworkRigidbody3D))]
    public class WeaponHomeReturn : NetworkBehaviour
    {
        [SerializeField] private float returnAfterSeconds = 5f;
        [Tooltip("Already this close to home: nothing to do (scene rifles start under the master client's authority)")]
        [SerializeField] private float homeRadius = 0.5f;

        private Grabbable[] _grabbables;
        private Rigidbody _body;
        private Vector3 _homePos;
        private Quaternion _homeRot;
        private float _droppedFor;

        public override void Spawned()
        {
            _grabbables = GetComponentsInChildren<Grabbable>(true);
            _body = GetComponent<Rigidbody>();
            _homePos = transform.position;
            _homeRot = transform.rotation;
            _droppedFor = 0f;
        }

        private bool IsHeldLocally()
        {
            foreach (var g in _grabbables)
                if (g != null && g.IsHeld()) return true;
            return false;
        }

        private void Update()
        {
            if (Object == null || !Object.IsValid || !Object.HasStateAuthority) { _droppedFor = 0f; return; }
            if (IsHeldLocally()) { _droppedFor = 0f; return; }
            if (Vector3.Distance(transform.position, _homePos) < homeRadius) { _droppedFor = 0f; return; }

            _droppedFor += Time.deltaTime;
            if (_droppedFor < returnAfterSeconds) return;
            _droppedFor = 0f;
            ReturnHome();
        }

        private void ReturnHome()
        {
            // Direct move rather than NetworkRigidbody3D.Teleport(): the addon's teleport requires a
            // RunnerSimulatePhysics on the runner, and this project lets Unity own the physics step.
            // Fusion syncs TRSP every tick, so proxies see the jump over one tick (~31 ms) = a snap.
            if (_body != null)
            {
                _body.linearVelocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
                _body.position = _homePos;
                _body.rotation = _homeRot;
            }
            transform.SetPositionAndRotation(_homePos, _homeRot);
            Debug.Log($"[Weapon] {name} unclaimed for {returnAfterSeconds}s -> returned home.");
        }
    }
}
