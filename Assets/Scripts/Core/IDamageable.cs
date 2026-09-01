using UnityEngine;

namespace VRZ.Core
{
    /// Everything a damage source needs to know about a hit, in one immutable package.
    public readonly struct DamageInfo
    {
        public readonly int Amount;
        public readonly Vector3 HitPoint;
        public readonly Vector3 SourcePosition;   // where the damage came from (directional feedback)
        public readonly bool Critical;            // the TARGET decided this hit was critical

        public DamageInfo(int amount, Vector3 hitPoint, Vector3 sourcePosition, bool critical = false)
        {
            Amount = amount;
            HitPoint = hitPoint;
            SourcePosition = sourcePosition;
            Critical = critical;
        }
    }

    /// Anything that can be hurt. Weapons, explosions and zombie attacks talk ONLY to this
    /// contract, never to concrete classes -- a barrel, a destructible prop or a new enemy type
    /// joins the game by implementing it, with zero changes to the damage sources (Open/Closed).
    ///
    /// Two design choices worth noting:
    ///  - IsCriticalHit lives on the target: a zombie knows where its head is, a barrel knows
    ///    where its valve is. Sources just ask.
    ///  - Health is exposed read-only so sources can PREDICT a killing blow for instant local
    ///    feedback (score popup, hitmarker) before the authoritative damage RPC lands.
    public interface IDamageable
    {
        bool IsAlive { get; }
        int Health { get; }
        Vector3 Position { get; }

        bool IsCriticalHit(Vector3 hitPoint);
        void ApplyDamage(in DamageInfo damage);
    }
}
