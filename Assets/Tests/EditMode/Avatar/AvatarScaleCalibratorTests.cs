using NUnit.Framework;
using VRZ.Player;

namespace VRZ.Tests.Avatar
{
    /// Pins down the calibration rules documented on AvatarScaleCalibrator: snap on the first
    /// valid reading, clamp, freeze while crouching, neutral pose on the desk, feet never below floor.
    public class AvatarScaleCalibratorTests
    {
        // Model head sits 1.6 m above the body root; calibrate only standing >= 1.2 m;
        // scale eases over 0.5 s; below 0.5 m the headset is assumed to be resting on a desk.
        private const float ModelHead = 1.6f;
        private const float MinCalib = 1.2f;
        private const float Smooth = 0.5f;
        private const float Neutral = 0.5f;
        private const float Dt = 1f / 60f;
        private const float Eps = 1e-4f;

        private static AvatarScaleCalibrator NewCalibrator(float minCalib = MinCalib) =>
            new AvatarScaleCalibrator(ModelHead, minCalib, Smooth, Neutral);

        [Test]
        public void CurrentScale_IsOne_BeforeAnyCalibration()
        {
            var c = NewCalibrator();
            Assert.That(c.CurrentScale, Is.EqualTo(1f));
        }

        [Test]
        public void FirstValidReading_SnapsScale_WithoutSmoothing()
        {
            var c = NewCalibrator();

            var r = c.Evaluate(headY: 1.76f, floorY: 0f, Dt);

            // 1.76 / 1.6 = 1.1 exactly on the very first sample (no SmoothDamp lag).
            Assert.That(r.Scale, Is.EqualTo(1.1f).Within(Eps));
            Assert.That(c.CurrentScale, Is.EqualTo(1.1f).Within(Eps));
        }

        [Test]
        public void StandingTall_BodyRootSitsModelHeadHeightBelowHead()
        {
            var c = NewCalibrator();

            var r = c.Evaluate(headY: 1.76f, floorY: 0f, Dt);

            // body = head - modelHead * scale = 1.76 - 1.6 * 1.1 = 0 -> feet exactly on the floor.
            Assert.That(r.BodyY, Is.EqualTo(0f).Within(Eps));
            Assert.That(r.HeadY, Is.EqualTo(1.76f).Within(Eps), "head is passed through unchanged");
        }

        [Test]
        public void FloorHeight_IsRelative_NotAbsolute()
        {
            var c = NewCalibrator();

            // Same 1.76 m player, but the floor is 0.3 m up (terrain bump).
            var r = c.Evaluate(headY: 2.06f, floorY: 0.3f, Dt);

            Assert.That(r.Scale, Is.EqualTo(1.1f).Within(Eps));
            Assert.That(r.BodyY, Is.EqualTo(0.3f).Within(Eps));
        }

        [Test]
        public void Scale_IsClampedToMax()
        {
            var c = NewCalibrator();

            var r = c.Evaluate(headY: 5f, floorY: 0f, Dt);   // 5 / 1.6 = 3.125

            Assert.That(r.Scale, Is.EqualTo(1.3f).Within(Eps));
        }

        [Test]
        public void Scale_IsClampedToMin()
        {
            // Lower the calibration gate so a 0.7 m "player" is still allowed to calibrate.
            var c = NewCalibrator(minCalib: 0.6f);

            var r = c.Evaluate(headY: 0.7f, floorY: 0f, Dt);   // 0.7 / 1.6 = 0.4375

            Assert.That(r.Scale, Is.EqualTo(0.55f).Within(Eps));
        }

        [Test]
        public void Crouching_FreezesScale_ButBodyStillDucks()
        {
            var c = NewCalibrator();
            c.Evaluate(headY: 1.76f, floorY: 0f, Dt);          // calibrated at 1.1

            var r = c.Evaluate(headY: 1.0f, floorY: 0f, Dt);   // below MinCalib, above Neutral

            Assert.That(r.Scale, Is.EqualTo(1.1f).Within(Eps), "crouching must not shrink the avatar");
            // Body would be 1.0 - 1.76 = -0.76, but the floor is a hard limit.
            Assert.That(r.BodyY, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void HeadsetOnDesk_ShowsNeutralPose_AtUncalibratedScale()
        {
            var c = NewCalibrator();

            var r = c.Evaluate(headY: 0.3f, floorY: 0f, Dt);   // below Neutral

            Assert.That(r.Scale, Is.EqualTo(1f));
            Assert.That(r.HeadY, Is.EqualTo(ModelHead).Within(Eps), "head is re-placed at the model's standing height");
            Assert.That(r.BodyY, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void HeadsetOnDesk_KeepsLastCalibratedScale()
        {
            var c = NewCalibrator();
            c.Evaluate(headY: 1.76f, floorY: 0f, Dt);          // calibrated at 1.1

            var r = c.Evaluate(headY: 0.3f, floorY: 0f, Dt);

            Assert.That(r.Scale, Is.EqualTo(1.1f).Within(Eps));
            Assert.That(r.HeadY, Is.EqualTo(ModelHead * 1.1f).Within(Eps));
        }

        [Test]
        public void BodyY_NeverGoesBelowFloor()
        {
            var c = NewCalibrator();

            // Uncalibrated (scale 1): body = 1.0 - 1.6 = -0.6, but floor is at 0.5.
            var r = c.Evaluate(headY: 1.0f, floorY: 0.5f, Dt);

            Assert.That(r.BodyY, Is.EqualTo(0.5f).Within(Eps));
        }

        [Test]
        public void LaterReadings_EaseTowardTarget_NotSnap()
        {
            var c = NewCalibrator();
            c.Evaluate(headY: 1.76f, floorY: 0f, Dt);          // 1.1

            var r = c.Evaluate(headY: 1.92f, floorY: 0f, Dt);  // target 1.2

            Assert.That(r.Scale, Is.GreaterThan(1.1f), "must start moving toward the new target");
            Assert.That(r.Scale, Is.LessThan(1.2f), "must NOT reach it in a single 16 ms frame");
        }

        [Test]
        public void LaterReadings_ConvergeToTarget_GivenTime()
        {
            var c = NewCalibrator();
            c.Evaluate(headY: 1.76f, floorY: 0f, Dt);

            AvatarScaleCalibrator.Result r = default;
            for (int i = 0; i < 600; i++)                      // 10 s at 60 fps, smoothTime is 0.5 s
                r = c.Evaluate(headY: 1.92f, floorY: 0f, Dt);

            Assert.That(r.Scale, Is.EqualTo(1.2f).Within(1e-3f));
        }
    }
}
