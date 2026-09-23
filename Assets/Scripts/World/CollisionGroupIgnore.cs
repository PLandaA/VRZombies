using System.Collections.Generic;
using UnityEngine;

namespace VRZ.World
{
    /// Runtime collision ignores between GROUPS of objects, for prefabs that are spawned/pooled
    /// and therefore cannot be wired by scene reference (see IgnoreCollisionsWithRoots for that case).
    ///
    /// Why: belt grenades are kinematic and follow the body; a held rifle brushing one while walking
    /// gets shoved by the solver every physics step, which reads as intermittent weapon jitter.
    /// The two never need to collide, so they simply don't.
    ///
    /// Usage: put this on the Rifle prefab (group Rifle, ignores Grenade) and on the Grenade prefab
    /// (group Grenade, ignores Rifle). Ignores are symmetric: declaring the rule on one side is
    /// enough, both sides is fine. Physics.IgnoreCollision is lost when a collider is disabled, so
    /// the rules are re-applied on every OnEnable.
    public class CollisionGroupIgnore : MonoBehaviour
    {
        public enum Group { Rifle, Grenade, Magazine }

        [Tooltip("Which group this object belongs to")]
        [SerializeField] private Group group;

        [Tooltip("Groups this object must never collide with")]
        [SerializeField] private Group[] ignores;

        private static readonly Dictionary<Group, List<CollisionGroupIgnore>> Registry = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Registry.Clear();

        private Collider[] _colliders;

        private void OnEnable()
        {
            _colliders = GetComponentsInChildren<Collider>(true);

            if (!Registry.TryGetValue(group, out var members))
                Registry[group] = members = new List<CollisionGroupIgnore>();
            members.Add(this);

            // Ignore everyone already alive that this object should not touch...
            foreach (var g in ignores)
                if (Registry.TryGetValue(g, out var others))
                    foreach (var other in others)
                        IgnorePair(this, other);

            // ...and everyone alive who declared it should not touch THIS group.
            foreach (var kv in Registry)
                foreach (var other in kv.Value)
                    if (other != this && other.Ignores(group))
                        IgnorePair(this, other);
        }

        private void OnDisable()
        {
            if (Registry.TryGetValue(group, out var members))
                members.Remove(this);
        }

        private bool Ignores(Group g)
        {
            foreach (var x in ignores) if (x == g) return true;
            return false;
        }

        private static void IgnorePair(CollisionGroupIgnore a, CollisionGroupIgnore b)
        {
            if (a._colliders == null || b._colliders == null) return;
            foreach (var ca in a._colliders)
            {
                if (ca == null) continue;
                foreach (var cb in b._colliders)
                    if (cb != null) Physics.IgnoreCollision(ca, cb, true);
            }
        }
    }
}
