using UnityEngine;
using Fusion;
using Autohand;
using VRZ.Enemies;

namespace VRZ.Weapons
{

    /// Spawns fresh magazines between waves and despawns empty loose ones (state authority only).
    public class AmmoSpawner : NetworkBehaviour
    {
        [Tooltip("Networked magazine prefab")]
        [SerializeField] private NetworkObject ammoPrefab;

        [Tooltip("Spawn points for fresh magazines")]
        [SerializeField] private Transform[] spawnPoints;

        [Tooltip("A magazine sitting on its marker with this fraction of its ammo or less counts as spent: it is removed and a fresh one takes its place at the next intermission")]
        [SerializeField, Range(0f, 0.9f)] private float spentFraction = 0.25f;

        // A magazine within this distance of a marker "belongs" to that marker.
        private const float MarkerRadius = 0.3f;

        // AutoAmmo has no capacity field: a fresh magazine's capacity is the prefab's starting currentAmmo.
        private int _fullAmmo = -1;
        private int FullAmmo()
        {
            if (_fullAmmo < 0)
            {
                var a = ammoPrefab != null ? ammoPrefab.GetComponent<AutoAmmo>() : null;
                _fullAmmo = a != null ? Mathf.Max(1, a.currentAmmo) : 1;
            }
            return _fullAmmo;
        }

        private ZombieSpawner _waveSystem;

        public override void Spawned()
        {
            _waveSystem = ZombieSpawner.Current;   // self-registered, see ZombieSpawner.Current
            if (_waveSystem != null)
                _waveSystem.OnIntermissionStarted.AddListener(OnIntermission);
            else
                Debug.LogWarning("[AmmoSpawner] ZombieSpawner not found.");
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (_waveSystem != null)
                _waveSystem.OnIntermissionStarted.RemoveListener(OnIntermission);
        }

        private void OnIntermission(int nextWave)
        {
            if (Object == null || !Object.HasStateAuthority) return;

            CleanupEmptyMags();
            SpawnFreshMags();
        }

        private void CleanupEmptyMags()
        {
            foreach (var ammo in FindObjectsByType<AutoAmmo>(FindObjectsSortMode.None))
            {
                if (ammo.currentAmmo > 0) continue;
                if (ammo.transform.parent != null) continue;
                var grab = ammo.GetComponent<Grabbable>();
                if (grab != null && grab.IsHeld()) continue;

                var no = ammo.GetComponent<NetworkObject>();
                if (no != null && no.IsValid)
                    Runner.Despawn(no);
            }
        }

        /// One magazine per marker, always (2026-10-07). Each marker is judged on its own: empty
        /// marker (someone took the mag) or a mag on it that is spent (<= spentFraction of its
        /// ammo) -> a fresh one appears there. A marker holding a mag with ammo to spare is left
        /// alone. The old rule was a GLOBAL count (2 full loose mags anywhere in the map), filled
        /// always from marker 1 first, so marker 2 only ever got a mag when the whole map had none.
        private void SpawnFreshMags()
        {
            if (ammoPrefab == null || spawnPoints == null || spawnPoints.Length == 0)
            {
                Debug.LogWarning("[AmmoSpawner] Missing ammoPrefab or spawnPoints.");
                return;
            }

            var mags = FindObjectsByType<AutoAmmo>(FindObjectsSortMode.None);
            foreach (var p in spawnPoints)
            {
                if (p == null) continue;

                // The mag sitting on this marker, if any (ignore held ones and ones inside a weapon).
                AutoAmmo onMarker = null;
                foreach (var m in mags)
                {
                    if (m == null || m.transform.parent != null) continue;
                    var grab = m.GetComponent<Grabbable>();
                    if (grab != null && grab.IsHeld()) continue;
                    if ((m.transform.position - p.position).sqrMagnitude < MarkerRadius * MarkerRadius) { onMarker = m; break; }
                }

                if (onMarker != null)
                {
                    if (onMarker.currentAmmo > FullAmmo() * spentFraction) continue;   // still useful: keep it

                    // Spent: remove it first, otherwise the new one spawns inside it and the physics
                    // overlap kick sends one flying off the table (seen 2026-10-01).
                    var no = onMarker.GetComponent<NetworkObject>();
                    if (no != null && no.IsValid) Runner.Despawn(no);
                }

                Runner.Spawn(ammoPrefab, p.position, p.rotation);

                // The size comes from the PREFAB (its root is already scaled 2.02,
                // same as the scene magazines and the markers), never from the marker: this only
                // runs on the State Authority and the mag's NetworkTransform has SyncScale off, so
                // a marker-driven scale would show one size on the master and another on proxies.
            }
        }
    }
}
