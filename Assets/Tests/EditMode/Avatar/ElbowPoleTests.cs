using NUnit.Framework;
using UnityEngine;
using VRZ.Player;

namespace VRZ.Tests.Avatar
{
    /// Geometry contract of ElbowPole: the pole is always perpendicular to the shoulder->hand
    /// axis, sits PoleDistance*scale from the arm midpoint, and mirrors laterally per side.
    public class ElbowPoleTests
    {
        private const float PoleDistance = 0.35f;   // mirrors ElbowPole's private constant
        private const float Eps = 1e-4f;

        // A body facing +Z: right is +X, forward is +Z.
        private static readonly Vector3 Right = Vector3.right;
        private static readonly Vector3 Forward = Vector3.forward;
        private static readonly Vector3 Shoulder = new Vector3(0f, 1.4f, 0f);

        [Test]
        public void ArmShorterThanMinimum_ReturnsFalse()
        {
            var hand = Shoulder + Vector3.right * (ElbowPole.MinArmLength * 0.5f);

            bool ok = ElbowPole.TryCompute(Shoulder, hand, Right, Forward, Vector3.down, +1f, 1f, out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void ArmAlongX_DownBias_PlacesPoleBelowMidpoint()
        {
            var hand = Shoulder + Vector3.right * 0.6f;

            bool ok = ElbowPole.TryCompute(Shoulder, hand, Right, Forward, Vector3.down, +1f, 1f, out var goal);

            Assert.That(ok, Is.True);
            var expected = new Vector3(0.3f, 1.4f - PoleDistance, 0f);   // midpoint + down * 0.35
            Assert.That(Vector3.Distance(goal, expected), Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void Pole_IsPerpendicularToArmAxis_ForDiagonalArm()
        {
            var hand = Shoulder + new Vector3(0.3f, -0.2f, 0.4f);   // arbitrary reach
            var bias = new Vector3(0.5f, -0.7f, -0.4f);

            ElbowPole.TryCompute(Shoulder, hand, Right, Forward, bias, +1f, 1f, out var goal);

            Vector3 mid = (Shoulder + hand) * 0.5f;
            Vector3 axis = (hand - Shoulder).normalized;
            float dot = Vector3.Dot((goal - mid).normalized, axis);
            Assert.That(dot, Is.EqualTo(0f).Within(Eps), "bend direction must lie in the plane perpendicular to the arm");
        }

        [Test]
        public void Side_MirrorsLateralBias()
        {
            var hand = Shoulder + Vector3.forward * 0.6f;          // arm reaching straight ahead
            var outward = new Vector3(1f, 0f, 0f);                  // "out" in body space

            ElbowPole.TryCompute(Shoulder, hand, Right, Forward, outward, +1f, 1f, out var rightGoal);
            ElbowPole.TryCompute(Shoulder, hand, Right, Forward, outward, -1f, 1f, out var leftGoal);

            Assert.That(rightGoal.x, Is.EqualTo(+PoleDistance).Within(Eps), "right elbow points to +X");
            Assert.That(leftGoal.x, Is.EqualTo(-PoleDistance).Within(Eps), "left elbow points to -X");
        }

        [Test]
        public void Scale_MultipliesPoleDistance()
        {
            var hand = Shoulder + Vector3.right * 0.6f;

            ElbowPole.TryCompute(Shoulder, hand, Right, Forward, Vector3.down, +1f, 2f, out var goal);

            Vector3 mid = (Shoulder + hand) * 0.5f;
            Assert.That(Vector3.Distance(goal, mid), Is.EqualTo(PoleDistance * 2f).Within(Eps));
        }

        [Test]
        public void BiasParallelToArm_FallsBackToOutAndBack()
        {
            var hand = Shoulder + Vector3.right * 0.6f;   // arm along +X
            var bias = Vector3.right;                     // fully parallel -> projection is zero

            bool ok = ElbowPole.TryCompute(Shoulder, hand, Right, Forward, bias, +1f, 1f, out var goal);

            // Fallback = ProjectOnPlane(right*side - forward, axis) = (1,0,-1) minus its X part = (0,0,-1).
            Assert.That(ok, Is.True);
            var expected = new Vector3(0.3f, 1.4f, -PoleDistance);
            Assert.That(Vector3.Distance(goal, expected), Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void Aim_MovesHintTransform_ByBlendFactor()
        {
            // The Transform adapter is thin, but it is the line the rig actually calls.
            var shoulder = TempObject("shoulder", Shoulder);
            var hand = TempObject("hand", Shoulder + Vector3.right * 0.6f);
            var body = TempObject("body", Vector3.zero);           // identity rotation: right=+X, forward=+Z
            var hint = TempObject("hint", new Vector3(10f, 10f, 10f));
            try
            {
                ElbowPole.Aim(shoulder, hand, hint, body, Vector3.down, +1f, 1f, k: 1f);

                var expected = new Vector3(0.3f, 1.4f - PoleDistance, 0f);
                Assert.That(Vector3.Distance(hint.position, expected), Is.EqualTo(0f).Within(Eps));
            }
            finally
            {
                foreach (var t in new[] { shoulder, hand, body, hint })
                    Object.DestroyImmediate(t.gameObject);
            }
        }

        [Test]
        public void Aim_WithNullTransform_DoesNothing()
        {
            var hint = TempObject("hint", new Vector3(10f, 10f, 10f));
            try
            {
                ElbowPole.Aim(null, null, hint, null, Vector3.down, +1f, 1f, 1f);
                Assert.That(hint.position, Is.EqualTo(new Vector3(10f, 10f, 10f)));
            }
            finally { Object.DestroyImmediate(hint.gameObject); }
        }

        /// Editor-only scratch object: HideAndDontSave keeps it out of the open scene (no dirtying).
        private static Transform TempObject(string name, Vector3 position)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            go.transform.position = position;
            return go.transform;
        }
    }
}
