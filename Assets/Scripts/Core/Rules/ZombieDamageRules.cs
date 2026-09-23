using UnityEngine;

namespace VRZ.Core
{
    /// Result of applying one hit to a zombie.
    public readonly struct DamageOutcome
    {
        /// False when the hit was ignored (the zombie was already dead). Callers record the attacker
        /// only when this is true, which is what makes "the last hit gets the kill" hold: a shot that
        /// lands on a corpse can never overwrite the killer.
        public readonly bool Applied;
        public readonly int Health;
        /// True only for the hit that brought health to zero.
        public readonly bool Killed;

        public DamageOutcome(bool applied, int health, bool killed)
        {
            Applied = applied; Health = health; Killed = killed;
        }
    }

    /// Zombie damage rules (pure). NetworkZombie.RPC_TakeDamage runs this on the zombie's State
    /// Authority and writes the result into its [Networked] state.
    public static class ZombieDamageRules
    {
        public static DamageOutcome Apply(int health, bool alreadyDead, int amount)
        {
            if (alreadyDead) return new DamageOutcome(false, health, false);
            // Negative amounts are treated as zero: any client can send the damage RPC, and a
            // negative value must never heal.
            int newHealth = Mathf.Max(0, health - Mathf.Max(0, amount));
            return new DamageOutcome(true, newHealth, newHealth <= 0);
        }
    }
}
