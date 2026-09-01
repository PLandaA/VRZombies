using UnityEngine;

namespace VRZ.Player
{

    /// Framerate-independent exponential filter for a position + rotation pair. Takes the buzz out
    /// of physics-driven hands pressed against geometry without any perceptible lag.
    public class PoseSmoother
    {
        private Vector3 _pos;
        private Quaternion _rot = Quaternion.identity;
        private bool _primed;

        public void Reset() => _primed = false;

        /// Returns the filtered pose. `timeConstant` <= 0 disables filtering (raw pass-through).
        public void Filter(ref Vector3 position, ref Quaternion rotation, float timeConstant, float dt)
        {
            if (timeConstant <= 0.0001f) { _primed = false; return; }

            if (!_primed) { _pos = position; _rot = rotation; _primed = true; }

            float k = 1f - Mathf.Exp(-dt / timeConstant);
            _pos = Vector3.Lerp(_pos, position, k);
            _rot = Quaternion.Slerp(_rot, rotation, k);
            position = _pos;
            rotation = _rot;
        }
    }
}
