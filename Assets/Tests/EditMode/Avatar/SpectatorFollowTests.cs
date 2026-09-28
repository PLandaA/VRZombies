using NUnit.Framework;
using UnityEngine;
using VRZ.Player;

namespace VRZ.Tests.Avatar
{
    /// The spectator camera must follow only the partner's translation: the spectator's own head
    /// motion and the partner's turning never move the container, and relocations are jumps.
    public class SpectatorFollowTests
    {
        private const float Dt = 1f / 72f;
        private const float Eps = 1e-4f;

        private static readonly Vector3 Head = new Vector3(10f, 1.7f, 10f);
        private static readonly Vector3 FacingZ = Vector3.forward;

        private static SpectatorFollow NewFollow() => new SpectatorFollow
        {
            FollowDistance = 2f, FollowHeight = 0.5f, Smoothing = 3f,
            MinDistance = 0.9f, MaxDistance = 4.5f, FacingGrace = 2f, ReanchorCooldown = 1.5f
        };

        private static void AssertClose(Vector3 actual, Vector3 expected) =>
            Assert.That((actual - expected).magnitude, Is.EqualTo(0f).Within(Eps), $"expected {expected} but was {actual}");

        private static Vector3 Run(SpectatorFollow f, Vector3 head, Vector3 facing, Vector3 eyes, int frames, out bool anyJump)
        {
            Vector3 total = Vector3.zero; anyJump = false;
            for (int i = 0; i < frames; i++)
            {
                total += f.Step(head, facing, eyes + total, Dt, out bool j);
                anyJump |= j;
            }
            return total;
        }

        [Test]
        public void FirstStep_JumpsEyesBehindAndAbovePartner()
        {
            var f = NewFollow();
            var eyes = Vector3.zero;

            var delta = f.Step(Head, FacingZ, eyes, Dt, out bool jumped);

            Assert.That(jumped, Is.True);
            AssertClose(eyes + delta, Head - Vector3.forward * 2f + Vector3.up * 0.5f);
        }

        [Test]
        public void Anchor_FlattensPartnerFacing()
        {
            var f = NewFollow();
            var tilted = new Vector3(0f, -0.8f, 0.6f);   // partner looking down at the floor

            var delta = f.Anchor(Head, tilted, Vector3.zero);

            AssertClose(delta, Head - Vector3.forward * 2f + Vector3.up * 0.5f);
        }

        [Test]
        public void PartnerStill_NoDrift()
        {
            var f = NewFollow();
            f.Anchor(Head, FacingZ, Vector3.zero);
            var eyes = Head - Vector3.forward * 2f + Vector3.up * 0.5f;

            var total = Run(f, Head, FacingZ, eyes, 200, out bool jumped);

            Assert.That(jumped, Is.False);
            Assert.That(total.magnitude, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void SpectatorMovesOwnHead_ContainerDoesNotMove()
        {
            // The bug that was in the headset: leaning 20 cm made the world slide back.
            var f = NewFollow();
            f.Anchor(Head, FacingZ, Vector3.zero);
            var eyes = Head - Vector3.forward * 2f + Vector3.up * 0.5f + new Vector3(0.2f, -0.1f, 0.2f);

            var total = Run(f, Head, FacingZ, eyes, 200, out bool jumped);

            Assert.That(jumped, Is.False);
            Assert.That(total.magnitude, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void PartnerTurns_NoOrbit()
        {
            var f = NewFollow();
            f.Anchor(Head, FacingZ, Vector3.zero);
            var eyes = Head - Vector3.forward * 2f + Vector3.up * 0.5f;

            // Turning 90 degrees away from us: facing +X, we are still behind-left, not in front.
            var total = Run(f, Head, Vector3.right, eyes, 100, out bool jumped);

            Assert.That(jumped, Is.False);
            Assert.That(total.magnitude, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void PartnerWalks_ContainerFollowsTheSameTranslation()
        {
            var f = NewFollow();
            f.Anchor(Head, FacingZ, Vector3.zero);
            var eyes = Head - Vector3.forward * 2f + Vector3.up * 0.5f;
            var moved = Head + new Vector3(3f, 0f, 1.5f);

            var total = Run(f, moved, FacingZ, eyes, 720, out bool jumped);   // 10 s: fully converged

            Assert.That(jumped, Is.False);
            AssertClose(total, new Vector3(3f, 0f, 1.5f));
        }

        [Test]
        public void PartnerTooClose_ReanchorsOnlyAfterCooldown()
        {
            var f = NewFollow();
            f.Anchor(Head, FacingZ, Vector3.zero);
            var eyes = Head - Vector3.forward * 2f + Vector3.up * 0.5f;
            var close = eyes + Vector3.forward * 0.3f + Vector3.down * 0.5f;   // 0.3 m in front of the eyes

            Run(f, close, FacingZ, eyes, 100, out bool early);        // 1.4 s < 1.5 s cooldown
            Assert.That(early, Is.False);

            Run(f, close, FacingZ, eyes, 20, out bool late);          // past the cooldown
            Assert.That(late, Is.True);
        }

        [Test]
        public void PartnerFacesSpectator_ReanchorsAfterGrace_NotBefore()
        {
            var f = NewFollow();
            f.Anchor(Head, FacingZ, Vector3.zero);
            var eyes = Head - Vector3.forward * 2f + Vector3.up * 0.5f;

            var total = Run(f, Head, Vector3.back, eyes, 130, out bool early);   // 1.8 s < 2 s grace
            Assert.That(early, Is.False);

            Run(f, Head, Vector3.back, eyes + total, 30, out bool late);         // 2.2 s
            Assert.That(late, Is.True);
        }
    }
}
