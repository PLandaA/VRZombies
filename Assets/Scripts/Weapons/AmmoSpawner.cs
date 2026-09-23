using UnityEngine;
using Fusion;
using Autohand;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Enemies;
using VRZ.FX;
using VRZ.World;

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
            _waveSystem = ZombieSpawner.Current;   // self-registered (fix A8)
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
            for (int i = 0; i < toSpawn; i++)
            {
                var p = spawnPoints[i % spawnPoints.Length];
                Runner.Spawn(ammoPrefab, p.position, p.rotation);

                // Netcode fix A2. The size comes from the PREFAB (its root is already scaled 2.02,
                // same as the scene magazines and the markers), never from the marker: this only
                // runs on the State Authority and the mag's NetworkTransform has SyncScale off, so
                // a marker-driven scale would show one size on the master and another on proxies.
            }
        }
    }
}
