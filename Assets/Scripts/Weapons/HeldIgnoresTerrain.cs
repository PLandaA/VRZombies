using System.Collections.Generic;
using UnityEngine;
using Autohand;

namespace VRZ.Weapons
{
    /// While this object is held, its colliders ignore every TerrainCollider; on release they collide
    /// again (a dropped rifle must still land on the ground).
    ///
    /// Why: walking up a slope brings the ground toward the barrel; a held-object collider touching
    /// terrain makes the grab joint (pulling up) and the ground (pushing back) fight in the solver,
    /// and the weapon shakes until the contact clears. AutoHand's heldIgnoreColliders covers the
    /// hand-vs-object case, not object-vs-world, so this does it for the terrain specifically.
    public class HeldIgnoresTerrain : MonoBehaviour
    {
        private Grabbable[] _grabbables;
        private Collider[] _colliders;
        private readonly List<TerrainCollider> _terrains = new();
        private bool _ignoring;

        private void Awake()
        {
            _grabbables = GetComponentsInChildren<Grabbable>(true);
            _colliders = GetComponentsInChildren<Collider>(true);
        }

        private void Update()
        {
            bool held = false;
            foreach (var g in _grabbables)
                if (g != null && g.IsHeld()) { held = true; break; }
            if (held == _ignoring) return;
            SetIgnore(held);
        }

        private void OnDisable()
        {
            if (_ignoring) SetIgnore(false);
        }

        private void SetIgnore(bool ignore)
        {
            _ignoring = ignore;
            if (ignore)
            {
                _terrains.Clear();
                _terrains.AddRange(FindObjectsByType<TerrainCollider>(FindObjectsSortMode.None));   // rare: once per grab
            }
            foreach (var t in _terrains)
            {
                if (t == null) continue;
                foreach (var c in _colliders)
                    if (c != null) Physics.IgnoreCollision(c, t, ignore);
            }
        }
    }
}
