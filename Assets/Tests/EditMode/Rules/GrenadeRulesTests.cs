using NUnit.Framework;
using VRZ.Core;

namespace VRZ.Tests.Rules
{
    public class GrenadeRulesTests
    {
        [Test]
        public void ZombieDamage_AtCentre_IsFull()
        {
            Assert.That(GrenadeRules.ZombieDamage(0f, 6f, 150), Is.EqualTo(150));
        }

        [Test]
        public void ZombieDamage_AtEdge_IsQuarter()
        {
            Assert.That(GrenadeRules.ZombieDamage(6f, 6f, 100), Is.EqualTo(25));
        }

        [Test]
        public void ZombieDamage_Halfway_IsLinear()
        {
            // Lerp(200, 50, 0.5) = 125, exact (no rounding ambiguity).
            Assert.That(GrenadeRules.ZombieDamage(2f, 4f, 200), Is.EqualTo(125));
        }

        [Test]
        public void ZombieDamage_BeyondRadius_StaysAtEdgeFraction()
        {
            // The blast is a sphere OVERLAP: a collider can touch it with its centre outside.
            Assert.That(GrenadeRules.ZombieDamage(9f, 6f, 100), Is.EqualTo(25));
        }

        [Test]
        public void ZombieDamage_NeverIncreasesWithDistance()
        {
            int previous = int.MaxValue;
            for (float d = 0f; d <= 8f; d += 0.25f)
            {
                int dmg = GrenadeRules.ZombieDamage(d, 6f, 150);
                Assert.That(dmg, Is.LessThanOrEqualTo(previous), "at distance " + d);
                previous = dmg;
            }
        }

        [Test]
        public void ZombieDamage_ZeroRadius_DoesNotDivideByZero()
        {
            Assert.That(GrenadeRules.ZombieDamage(0f, 0f, 100), Is.EqualTo(25));
        }

        [Test]
        public void HitsPlayer_InsideOrOnTheEdge_ButNotBeyond()
        {
            Assert.That(GrenadeRules.HitsPlayer(0f, 6f), Is.True);
            Assert.That(GrenadeRules.HitsPlayer(6f, 6f), Is.True);
            Assert.That(GrenadeRules.HitsPlayer(6.01f, 6f), Is.False);
        }
    }
}
