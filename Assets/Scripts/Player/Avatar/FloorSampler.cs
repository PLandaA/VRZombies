using System;
using UnityEngine;

namespace VRZ.Player
{

    /// Finds the highest real ground under a point, SMOOTHED and REMEMBERED.
    ///
    /// Why it is this careful: with a rifle in hand, a naive downward ray crossed dozens of hand
    /// and weapon colliders, overflowed its hit buffer, dropped the ground, and fell back to a
    /// poisoned reference for one frame -- the avatar body popped in sync with every step.
    /// Hand/grab layers are masked out, the buffer is generous, a missing sample keeps the last
    /// good value, and terrain bumps settle over ~0.15s instead of twitching the body.
    public class FloorSampler
    {
        private const float MaxDistance = 6f;
        private const float SettleTime = 0.15f;
        private static readonly string[] IgnoredLayers = { "Hand", "Grabbing", "Grabbable", "HandPlayer" };

        private readonly RaycastHit[] _hits = new RaycastHit[16];
        private readonly int _mask;
        private readonly Func<Collider, bool> _ignore;
        private float _smoothedY = float.MinValue;

        /// True once at least one ground sample has been taken.
        public bool HasSample => _smoothedY > float.MinValue;

        /// `ignoreCollider` decides which hits are NOT ground (the player's own body, held props...).
        /// Injected so this class knows nothing about the player rig or the interaction framework.
        public FloorSampler(Func<Collider, bool> ignoreCollider = null)
        {
            _ignore = ignoreCollider;
            int mask = ~0;
            foreach (var name in IgnoredLayers)
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0) mask &= ~(1 << layer);
            }
            _mask = mask;
        }

        /// Samples the ground below `from` and returns the smoothed floor height, or float.MinValue
        /// if no ground has EVER been seen. Player and avatar colliders are ignored.
        public float Sample(Vector3 from, float dt)
        {
            int n = Physics.RaycastNonAlloc(from, Vector3.down, _hits, MaxDistance, _mask, QueryTriggerInteraction.Ignore);
            float best = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                var hit = _hits[i];
                if (hit.collider == null) continue;
                if (_ignore != null && _ignore(hit.collider)) continue;
                if (hit.point.y > best) best = hit.point.y;
            }

            if (best > float.MinValue)
            {
                if (!HasSample) _smoothedY = best;                              // first sample snaps
                else _smoothedY = Mathf.Lerp(_smoothedY, best, 1f - Mathf.Exp(-dt / SettleTime));
            }
            return _smoothedY;
        }
    }
}
