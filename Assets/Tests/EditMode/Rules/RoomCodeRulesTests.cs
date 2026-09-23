using System.Collections.Generic;
using NUnit.Framework;
using VRZ.Core;

namespace VRZ.Tests.Rules
{
    public class RoomCodeRulesTests
    {
        private const string Prefix = "VRZ-";

        /// A deterministic stand-in for UnityEngine.Random.Range: returns the given values in order.
        private static System.Func<int, int, int> Sequence(params int[] values)
        {
            int i = 0;
            return (min, max) => values[i++ % values.Length];
        }

        [Test]
        public void Pick_ReturnsThePrefixedCode()
        {
            Assert.That(RoomCodeRules.Pick(new HashSet<string>(), Prefix, Sequence(42)), Is.EqualTo("VRZ-42"));
        }

        [Test]
        public void Pick_SkipsCodesThatAreTaken()
        {
            var taken = new HashSet<string> { "VRZ-42" };
            Assert.That(RoomCodeRules.Pick(taken, Prefix, Sequence(42, 57)), Is.EqualTo("VRZ-57"));
        }

        [Test]
        public void Pick_AsksForTwoDigitRange_WithExclusiveMax()
        {
            int seenMin = -1, seenMax = -1;
            RoomCodeRules.Pick(null, Prefix, (min, max) => { seenMin = min; seenMax = max; return min; });
            Assert.That(seenMin, Is.EqualTo(10));
            Assert.That(seenMax, Is.EqualTo(100));
        }

        [Test]
        public void Pick_FindsTheLastFreeCode_EvenWhenRandomKeepsMissing()
        {
            var taken = new HashSet<string>();
            for (int c = 10; c < 100; c++) if (c != 73) taken.Add(Prefix + c);
            Assert.That(RoomCodeRules.Pick(taken, Prefix, Sequence(10)), Is.EqualTo("VRZ-73"));
        }

        [Test]
        public void Pick_AllNinetyCodesTaken_ReturnsNull()
        {
            var taken = new HashSet<string>();
            for (int c = 10; c < 100; c++) taken.Add(Prefix + c);
            Assert.That(RoomCodeRules.Pick(taken, Prefix, Sequence(42)), Is.Null);
        }

        [Test]
        public void Pick_WithoutRandom_UsesTheOrderedScan()
        {
            Assert.That(RoomCodeRules.Pick(null, Prefix, null), Is.EqualTo("VRZ-10"));
        }

        [Test]
        public void DisplayCode_StripsThePrefix_AndLeavesOtherNamesAlone()
        {
            Assert.That(RoomCodeRules.DisplayCode("VRZ-42", Prefix), Is.EqualTo("42"));
            Assert.That(RoomCodeRules.DisplayCode("OtherRoom", Prefix), Is.EqualTo("OtherRoom"));
            Assert.That(RoomCodeRules.DisplayCode(null, Prefix), Is.EqualTo(""));
        }
    }
}
