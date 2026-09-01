using UnityEngine;

namespace VRZ.Player
{

    /// Elbow pole-vector placement. The bend direction must stay PERPENDICULAR to the
    /// shoulder->hand axis: deep folds (hand pulled to the chest) otherwise leave the solver
    /// without a valid bend plane and the forearm slices through the hand. The body-space bias
    /// (out / down / back) is projected onto that perpendicular plane at every fold depth.
    public static class ElbowPole
    {
        private const float PoleDistance = 0.35f;

        /// Minimum shoulder->hand distance for the bend plane to be meaningful.
        public const float MinArmLength = 0.05f;

        /// Pure geometry: where the elbow hint SHOULD be this frame, given only positions and the
        /// body's basis vectors. Returns false when the arm is too short to define a bend plane.
        /// side = +1 right arm, -1 left arm. scale = avatar scale.
        public static bool TryCompute(Vector3 shoulderPos, Vector3 handPos,
                                      Vector3 bodyRight, Vector3 bodyForward,
                                      Vector3 bodySpaceOffset, float side, float scale,
                                      out Vector3 goal)
        {
            goal = default;

            Vector3 axis = handPos - shoulderPos;
            float armLen = axis.magnitude;
            if (armLen < MinArmLength) return false;
            axis /= armLen;

            Vector3 bias =
                bodyRight * (bodySpaceOffset.x * side) +
                Vector3.up * bodySpaceOffset.y +
                bodyForward * bodySpaceOffset.z;
            Vector3 pole = Vector3.ProjectOnPlane(bias, axis);
            if (pole.sqrMagnitude < 0.0004f)
                pole = Vector3.ProjectOnPlane(bodyRight * side - bodyForward, axis);
            pole.Normalize();

            goal = Vector3.Lerp(shoulderPos, handPos, 0.5f) + pole * (PoleDistance * scale);
            return true;
        }

        /// Moves `hint` toward the ideal pole position for this frame (Transform adapter over TryCompute).
        /// k = smoothing factor (0..1).
        public static void Aim(Transform shoulder, Transform handTarget, Transform hint, Transform body,
                               Vector3 bodySpaceOffset, float side, float scale, float k)
        {
            if (shoulder == null || handTarget == null || hint == null || body == null) return;

            if (TryCompute(shoulder.position, handTarget.position, body.right, body.forward,
                           bodySpaceOffset, side, scale, out Vector3 goal))
                hint.position = Vector3.Lerp(hint.position, goal, k);
        }
    }
}
