using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VRZ.Player;

namespace VRZ.Tests.Avatar
{
    /// FloorSampler against REAL colliders: it is a thin wrapper around a physics query, so faking the
    /// query would test nothing. The Unity Test Runner already runs EditMode tests in a temporary,
    /// untitled scene and restores the editor's scenes afterwards, so the colliders are created there
    /// and destroyed after every test. The geometry sits 20 km from the origin so the ray cannot touch
    /// anything else.
    public class FloorSamplerTests
    {
        private static readonly Vector3 Origin = new Vector3(20000f, 0f, 20000f);
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created) if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
            Physics.SyncTransforms();
        }

        /// Box whose TOP face is at `topY`, centred under the sampling point.
        private GameObject Box(string name, float topY, int layer = 0, bool trigger = false)
        {
            var go = new GameObject(name) { layer = layer };
            _created.Add(go);
            go.transform.position = new Vector3(Origin.x, topY - 0.5f, Origin.z);
            var col = go.AddComponent<BoxCollider>();
            col.size = Vector3.one;
            col.isTrigger = trigger;
            Physics.SyncTransforms();
            return go;
        }

        private static Vector3 Above => Origin + Vector3.up * 2f;

        [Test]
        public void NoGroundEver_ReturnsMinValue_AndHasNoSample()
        {
            var s = new FloorSampler();
            Assert.That(s.Sample(Above, 0.016f), Is.EqualTo(float.MinValue));
            Assert.That(s.HasSample, Is.False);
        }

        [Test]
        public void FirstSample_SnapsToTheGroundTop()
        {
            Box("Floor", 0f);
            var s = new FloorSampler();
            Assert.That(s.Sample(Above, 0.016f), Is.EqualTo(0f).Within(1e-3f));
            Assert.That(s.HasSample, Is.True);
        }

        [Test]
        public void HighestGround_Wins()
        {
            Box("Floor", 0f);
            Box("Crate", 0.5f);
            Assert.That(new FloorSampler().Sample(Above, 0.016f), Is.EqualTo(0.5f).Within(1e-3f));
        }

        [Test]
        public void InjectedIgnore_SkipsThePlayersOwnColliders()
        {
            Box("Floor", 0f);
            var own = Box("OwnBody", 0.5f).GetComponent<Collider>();
            var s = new FloorSampler(c => c == own);
            Assert.That(s.Sample(Above, 0.016f), Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void HandLayerColliders_AreNotGround()
        {
            int hand = LayerMask.NameToLayer("Hand");
            Assume.That(hand, Is.GreaterThanOrEqualTo(0), "AutoHand's 'Hand' layer is not defined in this project");
            Box("Floor", 0f);
            Box("HeldRifle", 0.5f, hand);
            Assert.That(new FloorSampler().Sample(Above, 0.016f), Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void Triggers_AreNotGround()
        {
            Box("Floor", 0f);
            Box("TriggerVolume", 0.5f, trigger: true);
            Assert.That(new FloorSampler().Sample(Above, 0.016f), Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void LostGround_KeepsTheLastGoodValue()
        {
            var floor = Box("Floor", 0f);
            var s = new FloorSampler();
            s.Sample(Above, 0.016f);
            Object.DestroyImmediate(floor);
            Physics.SyncTransforms();
            Assert.That(s.Sample(Above, 0.016f), Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void RisingGround_EasesIn_InsteadOfSnapping()
        {
            var floor = Box("Floor", 0f);
            var s = new FloorSampler();
            s.Sample(Above, 0.016f);

            floor.transform.position += Vector3.up * 1f;   // ground top now at 1.0
            Physics.SyncTransforms();
            float y = s.Sample(Above, 0.05f);

            float expected = 1f - Mathf.Exp(-0.05f / 0.15f);   // ~0.28 after one 50 ms step (SettleTime 0.15 s)
            Assert.That(y, Is.EqualTo(expected).Within(1e-3f));
        }
    }
}
