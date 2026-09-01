using UnityEngine;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.FX;

namespace VRZ.World
{

    /// Ignores physics collisions between this object's colliders and EVERY collider under the
    /// listed target roots (resolved at Start, so it survives collider count changes). Used by the
    /// lobby practice grenades so the rifles can't bump them around the bench.
    public class IgnoreCollisionsWithRoots : MonoBehaviour
    {
        [Tooltip("All colliders under these roots will ignore collisions with this object")]
        [SerializeField] private Transform[] targetRoots;

        private void Start()
        {
            var ownCols = GetComponentsInChildren<Collider>(true);
            foreach (var root in targetRoots)
            {
                if (root == null) continue;
                foreach (var target in root.GetComponentsInChildren<Collider>(true))
                    foreach (var own in ownCols)
                        if (own != null && target != null)
                            Physics.IgnoreCollision(own, target, true);
            }
        }
    }
}
