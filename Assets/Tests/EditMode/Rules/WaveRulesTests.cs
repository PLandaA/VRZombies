using NUnit.Framework;
using VRZ.Core;
using VRZ.Tests.Fakes;

namespace VRZ.Tests.Rules
{
    /// Wave rules against fakes and against the real NullNetworkSession (the "nobody joined" case).
    public class WaveRulesTests
    {
        private static FakePlayerState Player(bool ready = false, int health = 100, bool valid = true) =>
            new FakePlayerState { Ready = ready, Health = health, IsValid = valid };

        // ── CountReady ──

        [Test]
        public void CountReady_NullSession_IsZero()
        {
            Assert.That(WaveRules.CountReady(new NullNetworkSession().Players), Is.EqualTo(0));
        }

        [Test]
        public void CountReady_CountsOnlyReadyPlayers()
        {
            var players = new IPlayerState[] { Player(ready: true), Player(ready: false), Player(ready: true) };

            Assert.That(WaveRules.CountReady(players), Is.EqualTo(2));
        }

        [Test]
        public void CountReady_IgnoresInvalidAndNull()
        {
            var players = new IPlayerState[] { Player(ready: true, valid: false), null, Player(ready: true) };

            Assert.That(WaveRules.CountReady(players), Is.EqualTo(1));
        }

        // ── AllPlayersDead ──

        [Test]
        public void AllPlayersDead_NullSession_IsFalse()
        {
            // Nobody has joined: that is "waiting", not "everybody died".
            Assert.That(WaveRules.AllPlayersDead(new NullNetworkSession().Players), Is.False);
        }

        [Test]
        public void AllPlayersDead_OneAlive_IsFalse()
        {
            var players = new IPlayerState[] { Player(health: 0), Player(health: 1) };

            Assert.That(WaveRules.AllPlayersDead(players), Is.False);
        }

        [Test]
        public void AllPlayersDead_EveryoneAtZero_IsTrue()
        {
            var players = new IPlayerState[] { Player(health: 0), Player(health: 0) };

            Assert.That(WaveRules.AllPlayersDead(players), Is.True);
        }

        [Test]
        public void AllPlayersDead_OnlyInvalidPlayers_IsFalse()
        {
            // Despawning players must not trigger a game over on their way out.
            var players = new IPlayerState[] { Player(health: 0, valid: false), null };

            Assert.That(WaveRules.AllPlayersDead(players), Is.False);
        }

        [Test]
        public void AllPlayersDead_InvalidAliveDoesNotCount()
        {
            // The only "alive" one is invalid -> the valid dead one decides.
            var players = new IPlayerState[] { Player(health: 100, valid: false), Player(health: 0) };

            Assert.That(WaveRules.AllPlayersDead(players), Is.True);
        }

        // ── ZombiesForWave ──

        [TestCase(1, 2, 1, ExpectedResult = 2)]
        [TestCase(2, 2, 1, ExpectedResult = 3)]
        [TestCase(5, 2, 1, ExpectedResult = 6)]
        [TestCase(3, 4, 3, ExpectedResult = 10)]
        [TestCase(1, 4, 0, ExpectedResult = 4)]
        public int ZombiesForWave_IsBasePlusLinearRamp(int wave, int baseZombies, int added) =>
            WaveRules.ZombiesForWave(wave, baseZombies, added);

        [Test]
        public void ZombiesForWave_WaveZeroOrNegative_IsZero()
        {
            Assert.That(WaveRules.ZombiesForWave(0, 2, 1), Is.EqualTo(0));
            Assert.That(WaveRules.ZombiesForWave(-3, 2, 1), Is.EqualTo(0));
        }

        // ── Fake sanity: the damage contract behaves like the real player for these rules ──

        [Test]
        public void FakePlayer_DiesThroughIDamageable()
        {
            var p = Player(health: 30);
            var players = new IPlayerState[] { p };
            Assert.That(WaveRules.AllPlayersDead(players), Is.False);

            p.ApplyDamage(new DamageInfo(30, default, default));

            Assert.That(p.IsAlive, Is.False);
            Assert.That(WaveRules.AllPlayersDead(players), Is.True);
        }
    }
}
