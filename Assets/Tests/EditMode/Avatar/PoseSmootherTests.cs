using NUnit.Framework;
using UnityEngine;
using VRZ.Player;

namespace VRZ.Tests.Avatar
{
    /// The hand filter must: pass through when disabled, prime on the first sample (no lag),
    /// ease toward new samples, and be framerate independent.
    public class PoseSmootherTests
    {
        private const float Tc = 0.05f;      // time constant used by the rig for hands
        private const float Eps = 1e-4f;

        [Test]
        public void ZeroTimeConstant_PassesThroughUnchanged()
        {
            var s = new PoseSmoother();
            var pos = new Vector3(1f, 2f, 3f);
            var rot = Quaternion.Euler(0f, 45f, 0f);

            s.Filter(ref pos, ref rot, timeConstant: 0f, dt: 1f / 60f);

            Assert.That(pos, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(Quaternion.Angle(rot, Quaternion.Euler(0f, 45f, 0f)), Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void FirstSample_PrimesFilter_NoLag()
        {
            var s = new PoseSmoother();
            var pos = new Vector3(1f, 2f, 3f);
            var rot = Quaternion.Euler(0f, 45f, 0f);

            s.Filter(ref pos, ref rot, Tc, 1f / 60f);

            // A fresh filter must not drag the hand from the origin: it snaps to the first sample.
            Assert.That(pos, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(Quaternion.Angle(rot, Quaternion.Euler(0f, 45f, 0f)), Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void SecondSample_MovesPartway_ByExponentialFactor()
        {
            var s = new PoseSmoother();
            var pos = Vector3.zero;
            var rot = Quaternion.identity;
            s.Filter(ref pos, ref rot, Tc, 1f / 60f);       // primed at origin

            pos = new Vector3(1f, 0f, 0f);
            s.Filter(ref pos, ref rot, Tc, dt: Tc);         // dt == timeConstant

            // k = 1 - e^(-dt/tc) = 1 - e^-1 = 0.6321...
            float expectedK = 1f - Mathf.Exp(-1f);
            Assert.That(pos.x, Is.EqualTo(expectedK).Within(Eps));
        }

        [Test]
        public void LargerDeltaTime_MovesFurther_FramerateIndependent()
        {
            var fast = new PoseSmoother();   // 90 fps
            var slow = new PoseSmoother();   // 30 fps
            var pos = Vector3.zero; var rot = Quaternion.identity;
            fast.Filter(ref pos, ref rot, Tc, 1f / 90f);
            pos = Vector3.zero; rot = Quaternion.identity;
            slow.Filter(ref pos, ref rot, Tc, 1f / 30f);

            var pFast = Vector3.right; var rFast = Quaternion.identity;
            fast.Filter(ref pFast, ref rFast, Tc, 1f / 90f);
            var pSlow = Vector3.right; var rSlow = Quaternion.identity;
            slow.Filter(ref pSlow, ref rSlow, Tc, 1f / 30f);

            // One 33 ms frame must cover more ground than one 11 ms frame, otherwise the filter
            // would feel three times laggier at 30 fps.
            Assert.That(pSlow.x, Is.GreaterThan(pFast.x));
        }

        [Test]
        public void Rotation_SlerpsToward_NewSample()
        {
            var s = new PoseSmoother();
            var pos = Vector3.zero;
            var rot = Quaternion.identity;
            s.Filter(ref pos, ref rot, Tc, 1f / 60f);

            rot = Quaternion.Euler(0f, 90f, 0f);
            s.Filter(ref pos, ref rot, Tc, Tc);

            float angle = Quaternion.Angle(Quaternion.identity, rot);
            Assert.That(angle, Is.GreaterThan(0f).And.LessThan(90f));
        }

        [Test]
        public void Reset_ReprimesOnNextSample()
        {
            var s = new PoseSmoother();
            var pos = Vector3.zero; var rot = Quaternion.identity;
            s.Filter(ref pos, ref rot, Tc, 1f / 60f);

            s.Reset();
            pos = new Vector3(5f, 5f, 5f);
            s.Filter(ref pos, ref rot, Tc, 1f / 60f);

            // After Reset the far-away sample is accepted as-is instead of being eased from origin.
            Assert.That(pos, Is.EqualTo(new Vector3(5f, 5f, 5f)));
        }

        [Test]
        public void DisablingThenEnabling_ReprimesInsteadOfResumingOldState()
        {
            var s = new PoseSmoother();
            var pos = Vector3.zero; var rot = Quaternion.identity;
            s.Filter(ref pos, ref rot, Tc, 1f / 60f);        // primed at origin

            pos = new Vector3(5f, 0f, 0f);
            s.Filter(ref pos, ref rot, 0f, 1f / 60f);         // filter disabled this frame

            pos = new Vector3(5f, 0f, 0f);
            s.Filter(ref pos, ref rot, Tc, 1f / 60f);         // enabled again

            Assert.That(pos.x, Is.EqualTo(5f).Within(Eps), "must not ease from the stale origin");
        }
    }
}
