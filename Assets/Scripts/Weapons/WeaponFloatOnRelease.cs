using System.Collections;
using Autohand;
using Fusion;
using UnityEngine;

namespace VRZ.Weapons
{
    /// A released rifle stays exactly where the hand let go of it, floating, instead of dropping
    /// to the floor (player feedback 2026-10-07). Grabbing it again wakes the body: AutoHand holds
    /// objects through a ConfigurableJoint connected to the rifle's Rigidbody, and a kinematic
    /// body would drag the HAND to the rifle instead of the rifle to the hand (NetworkAutoAmmo and
    /// NetworkGrenade already wake their bodies the same way).
    ///
    /// Networking: physics only runs on the State Authority (Fusion keeps proxy bodies kinematic
    /// and mirrors the owner's pose and kinematic flag), so only the authority freezes. Whoever
    /// grabs it next requests authority first (NetworkAutoGun), then wakes it locally.
    [RequireComponent(typeof(Rigidbody))]
    public class WeaponFloatOnRelease : MonoBehaviour
    {
        [Tooltip("Off: the rifle falls like before")]
        [SerializeField] private bool floatWhenReleased = true;

        private Rigidbody _body;
        private NetworkObject _netObject;
        private Fusion.Addons.Physics.NetworkRigidbody3D _netRb;
        private Grabbable[] _grabbables;

        private void Awake()
        {
            _body = GetComponent<Rigidbody>();
            _netObject = GetComponent<NetworkObject>();
            _netRb = GetComponent<Fusion.Addons.Physics.NetworkRigidbody3D>();
            _grabbables = GetComponentsInChildren<Grabbable>(true);
        }

        private void OnEnable()
        {
            foreach (var g in _grabbables)
            {
                g.OnBeforeGrabEvent += OnBeforeGrab;
                g.OnReleaseEvent += OnRelease;
            }
        }

        private void OnDisable()
        {
            foreach (var g in _grabbables)
            {
                if (g == null) continue;
                g.OnBeforeGrabEvent -= OnBeforeGrab;
                g.OnReleaseEvent -= OnRelease;
            }
        }

        private void OnBeforeGrab(Hand hand, Grabbable grabbable)
        {
            // Wake it for the joint. Also covers the lobby rifles, which start kinematic on their stand.
            if (!_body.isKinematic) return;
            _body.isKinematic = false;
            _body.WakeUp();

            // Fusion still holds "kinematic + asleep + frozen pose" in its state buffer until the
            // next tick, and NetworkRigidbody.CopyToEngine pushes that frozen pose onto the transform
            // while the hand's joint pulls the other way: that is a shake. Teleport re-seeds the
            // buffer and the interpolation with the pose the rifle has right now (report 2026-10-08:
            // grenade thrown, rifle re-grabbed, rifle shaking).
            if (_netRb != null && _netObject != null && _netObject.IsValid && _netObject.HasStateAuthority)
                _netRb.Teleport(transform.position, transform.rotation);
        }

        private void OnRelease(Hand hand, Grabbable grabbable)
        {
            if (!floatWhenReleased) return;
            // Next frame: a two-hand swap releases one grabbable while the other is still (or about
            // to be) held, and AutoHand finishes the hand-over after this event.
            StartCoroutine(FreezeIfFree());
        }

        private IEnumerator FreezeIfFree()
        {
            yield return null;
            foreach (var g in _grabbables)
                if (g != null && g.IsHeld()) yield break;
            if (_netObject != null && _netObject.IsValid && !_netObject.HasStateAuthority) yield break;

            _body.linearVelocity = Vector3.zero;
            _body.angularVelocity = Vector3.zero;
            _body.isKinematic = true;
        }
    }
}
