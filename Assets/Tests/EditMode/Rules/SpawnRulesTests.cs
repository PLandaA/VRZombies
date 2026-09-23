using NUnit.Framework;
using VRZ.Core;

namespace VRZ.Tests.Rules
{
    public class SpawnRulesTests
    {
        [Test]
        public void TwoPlayers_GetDistinctSlots()
        {
            var ids = new[] { 1, 2 };
            Assert.That(SpawnRules.SlotFor(ids, 1, 2), Is.EqualTo(0));
            Assert.That(SpawnRules.SlotFor(ids, 2, 2), Is.EqualTo(1));
        }

        [Test]
        public void NonConsecutiveIds_AfterARejoin_StillGetDistinctSlots()
        {
            // The old rule (id % slots) put ids 1 and 3 on the same spawn point.
            var ids = new[] { 1, 3 };
            Assert.That(SpawnRules.SlotFor(ids, 1, 2), Is.EqualTo(0));
            Assert.That(SpawnRules.SlotFor(ids, 3, 2), Is.EqualTo(1));
        }

        [Test]
        public void OrderOfActivePlayers_DoesNotMatter()
        {
            // Every client may enumerate ActivePlayers in a different order.
            Assert.That(SpawnRules.SlotFor(new[] { 3, 1 }, 3, 2), Is.EqualTo(SpawnRules.SlotFor(new[] { 1, 3 }, 3, 2)));
        }

        [Test]
        public void MorePlayersThanSlots_Wraps()
        {
            Assert.That(SpawnRules.SlotFor(new[] { 1, 2, 3 }, 3, 2), Is.EqualTo(0));
        }

        [Test]
        public void UnknownPlayer_NoSlots_OrNoList_FallBackToZero()
        {
            Assert.That(SpawnRules.SlotFor(new[] { 1, 2 }, 7, 2), Is.EqualTo(0));
            Assert.That(SpawnRules.SlotFor(new[] { 1, 2 }, 2, 0), Is.EqualTo(0));
            Assert.That(SpawnRules.SlotFor(null, 2, 2), Is.EqualTo(0));
        }
    }
}
