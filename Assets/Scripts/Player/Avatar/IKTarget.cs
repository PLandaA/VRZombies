using UnityEngine;

namespace VRZ.Player
{

    /// A VRIK target transform plus the tracked-space offsets that map a tracked frame (camera,
    /// controller, physical hand) onto the avatar's bone frame.
    [System.Serializable]
    public struct IKTarget
    {
        [Tooltip("IK target transform driven by the rig (VRIK head / hand target).")]
        public Transform targetTransform;

        [Tooltip("Position offset applied in the TRACKED space (rotates with the hand/head).")]
        public Vector3 positionOffset;

        [Tooltip("Rotation offset in euler angles, applied after the tracked rotation.")]
        public Vector3 rotationOffset;

        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            if (targetTransform == null) return;
            // The offset lives in tracked space, so e.g. (0, 0.04, -0.01) always means "4cm toward
            // the wrist" regardless of how the hand is oriented.
            targetTransform.SetPositionAndRotation(
                position + rotation * positionOffset,
                rotation * Quaternion.Euler(rotationOffset));
        }
    }
}
