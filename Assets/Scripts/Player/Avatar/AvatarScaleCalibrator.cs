using UnityEngine;

namespace VRZ.Player
{

    /// Player-height scale calibration for the avatar. Pure logic, no Unity objects: feed it the
    /// head height and the floor height every frame, get back the scale to apply, where the body
    /// root belongs, and the (possibly corrected) head height.
    ///
    /// Rules that make it feel right:
    ///  - the scale is CALIBRATED, not live: it adjusts slowly and only while the player stands
    ///    tall enough. Crouching or tracking dips freeze it -> the body ducks but never shrinks.
    ///  - a headset resting on a desk (head far below neutralPoseHeight) shows a neutral standing
    ///    pose at the last known scale instead of a crumpled avatar.
    ///  - the body root never goes below the floor.
    public class AvatarScaleCalibrator
    {
        public struct Result
        {
            public float Scale;
            public float BodyY;
            public float HeadY;
        }

        private const float MinScale = 0.55f;
        private const float MaxScale = 1.3f;

        private readonly float _modelHeadHeight;
        private readonly float _minCalibrationHeight;
        private readonly float _smoothTime;
        private readonly float _neutralPoseHeight;

        private float _calibratedScale = -1f;   // < 0 = not calibrated yet
        private float _velocity;

        /// Scale currently in use (1 until the first valid calibration).
        public float CurrentScale => _calibratedScale > 0f ? _calibratedScale : 1f;

        public AvatarScaleCalibrator(float modelHeadHeight, float minCalibrationHeight, float smoothTime, float neutralPoseHeight)
        {
            _modelHeadHeight = Mathf.Max(0.1f, modelHeadHeight);
            _minCalibrationHeight = minCalibrationHeight;
            _smoothTime = smoothTime;
            _neutralPoseHeight = neutralPoseHeight;
        }

        public Result Evaluate(float headY, float floorY, float dt)
        {
            float headH = headY - floorY;
            var r = new Result { HeadY = headY };

            if (headH < _neutralPoseHeight)
            {
                // Headset on a desk: neutral standing pose, keep whatever scale we had.
                r.Scale = CurrentScale;
                r.HeadY = floorY + _modelHeadHeight * r.Scale;
                r.BodyY = floorY;
            }
            else
            {
                if (headH >= _minCalibrationHeight)
                {
                    float target = Mathf.Clamp(headH / _modelHeadHeight, MinScale, MaxScale);
                    if (_calibratedScale <= 0f) _calibratedScale = target;       // first reading snaps
                    else _calibratedScale = Mathf.SmoothDamp(_calibratedScale, target, ref _velocity, _smoothTime, Mathf.Infinity, dt);
                }
                r.Scale = CurrentScale;
                r.BodyY = headY - _modelHeadHeight * r.Scale;
            }

            r.BodyY = Mathf.Max(r.BodyY, floorY);   // safety net: feet never below the floor
            return r;
        }
    }
}
