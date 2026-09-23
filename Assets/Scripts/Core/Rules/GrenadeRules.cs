using UnityEngine;

namespace VRZ.Core
{
    /// Grenade blast rules (pure). NetworkGrenade calls these on the thrower's client; tests call
    /// them directly.
    public static class GrenadeRules
    {
        /// Fraction of the maximum damage a zombie still takes at the very edge of the blast.
        public const float EdgeDamageFraction = 0.25f;

        /// Zombie damage: full at the centre, falling linearly to 25% at the edge. Distances beyond
        /// the radius stay at 25%: the blast query is a sphere OVERLAP, so a zombie whose collider
        /// touches the sphere can have its centre slightly outside it.
        public static int ZombieDamage(float distance, float radius, int maxDamage)
        {
            float t = radius > 0f ? Mathf.Clamp01(distance / radius) : 1f;
            return Mathf.RoundToInt(Mathf.Lerp(maxDamage, maxDamage * EdgeDamageFraction, t));
        }

        /// Players take flat damage, but only when their body is inside the radius.
        public static bool HitsPlayer(float distance, float radius) => distance <= radius;
    }
}
