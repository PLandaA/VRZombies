using UnityEngine;

namespace VRZ.Player
{
    /// Follow model for the death spectator. Pure logic (no Unity objects), unit-tested.
    ///
    /// The camera follows the partner's TRANSLATION only, through a fixed world-space anchor
    /// chosen behind and above their head at anchor time. Two things never move the container:
    ///   - the spectator's own head (positional tracking stays untouched -- the earlier version
    ///     re-aimed the eyes every frame and cancelled it with a 0.3 s lag: "the world slides"),
    ///   - the partner turning (no orbiting: their facing is read only when an anchor is chosen).
    /// When the partner ends up too close, too far or facing the spectator for a while, a new
    /// anchor is chosen and the view JUMPS there (instant relocations are comfortable in VR,
    /// continuous artificial motion is not).
    public class SpectatorFollow
    {
        public float FollowDistance = 2.2f;   // behind the partner's head (m)
        public float FollowHeight = 0.7f;     // above the partner's head (m)
        public float Smoothing = 3f;          // 1/s: how quickly the anchor point catches up with the partner
        public float MinDistance = 0.9f;      // flat eyes-to-head distance that forces a new anchor
        public float MaxDistance = 4.5f;
        public float FacingGrace = 2f;        // seconds the partner may face the spectator before re-anchoring
        public float ReanchorCooldown = 1.5f; // minimum time between two jumps

        private Vector3 _anchor;      // desired eyes position relative to the partner's head (world axes)
        private Vector3 _target;      // smoothed desired eyes position
        private bool _anchored;
        private float _facingTime;
        private float _cooldown;

        public bool IsAnchored => _anchored;

        public void Reset() { _anchored = false; _facingTime = 0f; _cooldown = 0f; }

        /// Chooses a new anchor behind the partner (as they face NOW) and returns the translation
        /// that puts the eyes there immediately.
        public Vector3 Anchor(Vector3 partnerHead, Vector3 partnerFacing, Vector3 eyes)
        {
            Vector3 desiredEyes = partnerHead - Flat(partnerFacing) * FollowDistance + Vector3.up * FollowHeight;
            _anchor = desiredEyes - partnerHead;
            _target = desiredEyes;
            _anchored = true;
            _facingTime = 0f;
            _cooldown = ReanchorCooldown;
            return desiredEyes - eyes;
        }

        /// Per-frame container translation. `jumped` is true when a new anchor was chosen (the
        /// caller may re-orient the view toward the partner in the same frame).
        public Vector3 Step(Vector3 partnerHead, Vector3 partnerFacing, Vector3 eyes, float dt, out bool jumped)
        {
            jumped = false;
            if (!_anchored || NeedsReanchor(partnerHead, partnerFacing, eyes, dt))
            {
                jumped = true;
                return Anchor(partnerHead, partnerFacing, eyes);
            }

            Vector3 goal = partnerHead + _anchor;
            Vector3 previous = _target;
            _target = Vector3.Lerp(_target, goal, 1f - Mathf.Exp(-Smoothing * dt));
            return _target - previous;
        }

        private bool NeedsReanchor(Vector3 partnerHead, Vector3 partnerFacing, Vector3 eyes, float dt)
        {
            _cooldown -= dt;

            Vector3 toEyes = eyes - partnerHead; toEyes.y = 0f;
            float distance = toEyes.magnitude;

            // "Facing the spectator": their flat forward points at us (within ~60 degrees).
            bool facingUs = distance > 0.001f && Vector3.Dot(Flat(partnerFacing), toEyes / distance) > 0.5f;
            _facingTime = facingUs ? _facingTime + dt : 0f;

            if (_cooldown > 0f) return false;
            return distance < MinDistance || distance > MaxDistance || _facingTime > FacingGrace;
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 0.0001f ? Vector3.forward : v.normalized;
        }
    }
}
