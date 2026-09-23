using NUnit.Framework;
using VRZ.Core;

namespace VRZ.Tests.Rules
{
    public class ZombieDamageRulesTests
    {
        [Test]
        public void NonLethalHit_ReducesHealth_WithoutKilling()
        {
            var o = ZombieDamageRules.Apply(100, false, 25);
            Assert.That(o.Applied, Is.True);
            Assert.That(o.Health, Is.EqualTo(75));
            Assert.That(o.Killed, Is.False);
        }

        [Test]
        public void ExactlyLethalHit_Kills()
        {
            var o = ZombieDamageRules.Apply(25, false, 25);
            Assert.That(o.Health, Is.EqualTo(0));
            Assert.That(o.Killed, Is.True);
        }

        [Test]
        public void Overkill_ClampsHealthAtZero()
        {
            var o = ZombieDamageRules.Apply(20, false, 50);
            Assert.That(o.Health, Is.EqualTo(0));
            Assert.That(o.Killed, Is.True);
        }

        [Test]
        public void HitOnCorpse_IsNotApplied()
        {
            var o = ZombieDamageRules.Apply(0, true, 25);
            Assert.That(o.Applied, Is.False);
            Assert.That(o.Killed, Is.False);
            Assert.That(o.Health, Is.EqualTo(0));
        }

        [Test]
        public void NegativeDamage_NeverHeals()
        {
            var o = ZombieDamageRules.Apply(50, false, -30);
            Assert.That(o.Health, Is.EqualTo(50));
            Assert.That(o.Killed, Is.False);
        }

        [Test]
        public void LastHitGetsTheKill_AndACorpseShotCannotStealIt()
        {
            // Mirrors NetworkZombie.RPC_TakeDamage: the attacker is recorded only when the hit applies.
            int health = 100; bool dead = false; int lastDamager = 0;
            void Hit(int shooter, int amount)
            {
                var o = ZombieDamageRules.Apply(health, dead, amount);
                if (!o.Applied) return;
                health = o.Health; lastDamager = shooter;
                if (o.Killed) dead = true;
            }

            Hit(shooter: 1, amount: 60);   // A does most of the damage
            Hit(shooter: 2, amount: 50);   // B lands the killing blow
            Hit(shooter: 1, amount: 25);   // A shoots the corpse

            Assert.That(dead, Is.True);
            Assert.That(lastDamager, Is.EqualTo(2), "the last APPLIED hit owns the kill");
        }
    }
}
