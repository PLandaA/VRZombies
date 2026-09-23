using Autohand;
using UnityEngine;

namespace VRZ.Weapons
{
    /// Keeps a held object at its ORIGINAL parent (the scene root) while it is in a hand, so its
    /// NetworkTransform replicates world coordinates. (Fix 2026-09-23, magazine/grenade "ghost".)
    ///
    /// Why this exists instead of simply parentOnGrab = false:
    ///   - Fusion's NetworkTransform ALWAYS replicates localPosition/localRotation (verified in the
    ///     Fusion.Runtime 2.0.10 IL: CopyToBuffer reads local, CopyToEngine writes local).
    ///   - AutoHand, with parentOnGrab, parents the grabbed object to hand.transform.parent
    ///     (TrackerOffsets). The owner then sends coordinates relative to its play space and the
    ///     partner renders the object near the arena's world origin: the "ghost".
    ///   - But AutoHand's PlacePoint.CanPlace REFUSES objects with parentOnGrab = false when the place
    ///     point belongs to another grabbable (the rifle's MagPlacePoint), so the magazine could not
    ///     be inserted anymore.
    /// Keeping parentOnGrab = true (AutoHand decides with the FLAG) and undoing the actual parenting
    /// here satisfies both. Grabbable.OnGrab parents first and raises OnGrabEvent last, in the same
    /// call, so no network tick ever captures the parented state. Only runs on the grabbing client.
    [RequireComponent(typeof(Grabbable))]
    public class KeepWorldParentWhileHeld : MonoBehaviour
    {
        private Grabbable _grabbable;

        private void Awake()
        {
            _grabbable = GetComponent<Grabbable>();
            _grabbable.OnGrabEvent += OnGrabbed;
        }

        private void OnDestroy()
        {
            if (_grabbable != null) _grabbable.OnGrabEvent -= OnGrabbed;
        }

        private void OnGrabbed(Hand hand, Grabbable grab)
        {
            var root = _grabbable.rootTransform;
            if (root == null) return;
            var original = _grabbable.originalParent;   // AutoHand's own record (scene root for these objects)
            if (root.parent != original)
                root.SetParent(original, true);         // keep world pose; the grab joint keeps holding it
        }
    }
}
