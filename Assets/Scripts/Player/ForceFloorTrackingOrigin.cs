using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// Forces the XR runtime to use Floor tracking origin so the camera reports
/// real head height above the physical floor (the AutoHand rig has no XROrigin).
public class ForceFloorTrackingOrigin : MonoBehaviour
{
    private void Start()
    {
        var subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        foreach (var s in subsystems)
        {
            bool ok = s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
            Debug.Log("[TrackingOrigin] '" + s.subsystemDescriptor.id + "' -> Floor: " + ok +
                      " (mode actual: " + s.GetTrackingOriginMode() + ")");
        }
        if (subsystems.Count == 0)
            Debug.LogWarning("[TrackingOrigin] No XRInputSubsystem found (no headset connected?).");
    }
}
