using UnityEngine;
using Fusion;
using Autohand;

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
        _waveSystem = FindFirstObjectByType<ZombieSpawner>();
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
        Debug.Log("[AmmoSpawner] Intermission (wave " + nextWave + "): fresh magazines spawned.");
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

        for (int i = 0; i < magsPerIntermission; i++)
        {
            var p = spawnPoints[i % spawnPoints.Length];
            Runner.Spawn(ammoPrefab, p.position, p.rotation);
        }
    }
}
