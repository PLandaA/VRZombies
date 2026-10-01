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

        [Tooltip("Magazines spawned per intermission")]
        [SerializeField] private int magsPerIntermission = 2;

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

        private void SpawnFreshMags()
        {
            if (ammoPrefab == null || spawnPoints == null || spawnPoints.Length == 0)
            {
                Debug.LogWarning("[AmmoSpawner] Missing ammoPrefab or spawnPoints.");
                return;
            }

            int available = 0;
            foreach (var ammo in FindObjectsByType<AutoAmmo>(FindObjectsSortMode.None))
            {
                if (ammo.currentAmmo <= 0) continue;
                if (ammo.transform.parent != null) continue;
                var grab = ammo.GetComponent<Grabbable>();
                if (grab != null && grab.IsHeld()) continue;
                if (ammo.GetComponent<NetworkObject>() == null) continue;
                available++;
            }

            int toSpawn = Mathf.Max(0, magsPerIntermission - available);
            if (toSpawn == 0) return;

            // Spawn only on FREE markers. The old loop always started at marker 0, so a full mag
            // left there from the last intermission got a new one spawned inside it; the physics
            // overlap kick sent one flying off the table (2026-10-01, "random ammo on the ground").
            var mags = FindObjectsByType<AutoAmmo>(FindObjectsSortMode.None);
            foreach (var p in spawnPoints)
            {
                if (toSpawn == 0) break;
                if (p == null) continue;
                bool occupied = false;
                foreach (var m in mags)
                    if ((m.transform.position - p.position).sqrMagnitude < 0.3f * 0.3f) { occupied = true; break; }
                if (occupied) continue;

                Runner.Spawn(ammoPrefab, p.position, p.rotation);
                toSpawn--;

                // The size comes from the PREFAB (its root is already scaled 2.02,
                // same as the scene magazines and the markers), never from the marker: this only
                // runs on the State Authority and the mag's NetworkTransform has SyncScale off, so
                // a marker-driven scale would show one size on the master and another on proxies.
            }
        }
    }
}
