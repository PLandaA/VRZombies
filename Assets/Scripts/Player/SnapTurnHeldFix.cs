using System.Collections.Generic;
using UnityEngine;
using Autohand;

namespace VRZ.Player
{
    /// Snap turn with something in the hand.
    ///
    /// AutoHandPlayer.UpdateTurn (LateUpdate) rotates the tracking container. Everything that is a
    /// CHILD of the container moves with it: the hands, the head, and any object grabbed with
    /// parentOnGrab (AutoHand parents it to hand.transform.parent). AutoHand then re-seats the right
    /// hand on its grab point. For those objects nothing else is needed.
    ///
    /// Objects grabbed WITHOUT parentOnGrab (magazine and grenade, which must stay unparented so
    /// their NetworkTransform replicates world coordinates) are left behind by the container, and the
    /// hand gets pulled back to them. This component moves exactly those objects by the container's
    /// own motion and re-seats the holding hand on the moved grab point, so hand + object turn
    /// together. It never touches anything inside the container (doing so double-rotated the hands
    /// and the rifle: the "whip" after every turn).
    [DefaultExecutionOrder(-100)]
    public class SnapTurnHeldFix : MonoBehaviour
    {
        [SerializeField] private AutoHandPlayer player;
        private Vector3 _containerPosBefore;
        private Quaternion _containerRotBefore = Quaternion.identity;
        private readonly HashSet<Rigidbody> _done = new();

        private void Awake()
        {
            if (player == null) player = GetComponent<AutoHandPlayer>();
        }

        private void OnEnable() { if (player != null) player.OnSnapTurn += OnSnapTurn; }
        private void OnDisable() { if (player != null) player.OnSnapTurn -= OnSnapTurn; }

        private void Update()
        {
            // Sampled in Update (order -100), i.e. before AutoHandPlayer's LateUpdate moves and turns
            // the container: the pose it had before any turn this frame.
            if (player != null && player.trackingContainer != null) SampleContainer(player);
        }

        private void SampleContainer(AutoHandPlayer p)
        {
            _containerPosBefore = p.trackingContainer.position;
            _containerRotBefore = p.trackingContainer.rotation;
        }

        private void OnSnapTurn(AutoHandPlayer p)
        {
            var c = p.trackingContainer;
            // The exact rigid motion AutoHand just applied to the tracking container this frame
            // (recenter translation + RotateAround), measured instead of reconstructed.
            Quaternion rotDelta = c.rotation * Quaternion.Inverse(_containerRotBefore);
            if (Quaternion.Angle(Quaternion.identity, rotDelta) < 0.5f)
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning("[SnapTurnFix] OnSnapTurn fired but the container did not rotate this frame. Nothing moved.");
#endif
                SampleContainer(p);
                return;
            }
            _done.Clear();

            // 2026-09-23 fix of the fix ("weapon whips after a snap turn"). The hands are CHILDREN of
            // the tracking container, and so is any object grabbed with parentOnGrab (AutoHand
            // parents it to hand.transform.parent). RotateAround already moved all of those, and
            // AutoHand re-seats the right hand on its grab point. The old version rotated hands and
            // held bodies AGAIN -> hand + weapon ended 30 deg past the controllers and HandFollow
            // dragged them back over the next frames: the whip. Now only held bodies that live
            // OUTSIDE the container (parentOnGrab = false: magazine, grenade) are moved, by the
            // container's own delta, and the hand(s) holding them are re-seated on their grab point
            // (a child of the object), exactly as AutoHand itself does for the right hand.
            MoveHeldIfOutside(p.handRight, c, rotDelta);
            MoveHeldIfOutside(p.handLeft, c, rotDelta);
            Physics.SyncTransforms();
            SampleContainer(p);

#if VRZ_NET_DIAGNOSTICS
            // Diagnostic: hand body vs its controller target. After a correct turn this stays at its
            // normal few-cm; a whip shows as a large gap at t0 that closes over the next frames.
            var hand = p.handRight != null && p.handRight.holdingObj != null ? p.handRight : (p.handLeft != null && p.handLeft.holdingObj != null ? p.handLeft : null);
            if (hand != null) StartCoroutine(TraceAfterTurn(hand, Quaternion.Angle(Quaternion.identity, rotDelta)));
#endif
        }

        private void MoveHeldIfOutside(Hand hand, Transform container, Quaternion rotDelta)
        {
            if (hand == null || hand.holdingObj == null) return;
            var held = hand.holdingObj.body;
            if (held == null) return;
            if (held.transform.IsChildOf(container)) return;   // parentOnGrab: already moved by AutoHand

            if (_done.Add(held))   // a two-handed object is moved once
            {
                Vector3 newPos = container.position + rotDelta * (held.position - _containerPosBefore);
                Quaternion newRot = rotDelta * held.rotation;
                held.position = newPos;
                held.rotation = newRot;
                held.linearVelocity = Vector3.zero;
                held.angularVelocity = Vector3.zero;
                held.transform.SetPositionAndRotation(newPos, newRot);
            }

            // handGrabPoint is a child of the held object, so it moved with it: put the hand there.
            if (hand.body != null && hand.handGrabPoint != null)
            {
                hand.body.position = hand.handGrabPoint.position;
                hand.body.rotation = hand.handGrabPoint.rotation;
                hand.body.linearVelocity = Vector3.zero;
                hand.body.angularVelocity = Vector3.zero;
                hand.transform.SetPositionAndRotation(hand.handGrabPoint.position, hand.handGrabPoint.rotation);
            }
        }

#if VRZ_NET_DIAGNOSTICS
        private System.Collections.IEnumerator TraceAfterTurn(Hand hand, float angle)
        {
            string Gap() => hand.follow == null ? "n/a"
                : "hand-vs-controller pos " + (Vector3.Distance(hand.body.position, hand.follow.position) * 100f).ToString("F1") + " cm, rot " + Quaternion.Angle(hand.body.rotation, hand.follow.rotation).ToString("F0") + " deg";
            Debug.Log("[SnapTurnFix] turn " + angle.ToString("F0") + " deg | " + hand.holdingObj.name + " | t0 " + Gap());
            yield return new WaitForFixedUpdate();
            Debug.Log("[SnapTurnFix]   +1 fixed: " + Gap());
            for (int i = 0; i < 6; i++) yield return null;
            Debug.Log("[SnapTurnFix]   +6 frames: " + Gap());
            for (int i = 0; i < 20; i++) yield return null;
            Debug.Log("[SnapTurnFix]   +26 frames: " + Gap());
        }
#endif
    }
}
