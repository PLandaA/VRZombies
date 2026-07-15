using System.Collections.Generic;
using UnityEngine;
using Fusion;
using UnityEngine.Events;

/// Wave system: spawns zombie rounds, tracks alive count, handles intermissions and player-ready gating.
public class ZombieSpawner : NetworkBehaviour
{
    [Header("Prefab")]
    [SerializeField] private NetworkObject zombiePrefab;

    [Header("Spawn Points")]
    [Tooltip("If empty, spawns around the spawner's position")]
    [SerializeField] private Transform[] spawnPoints;

    [Header("Ready Check")]
    [Tooltip("How many players must be Ready (grab a weapon) to start. Set 1 for solo testing.")]
    [SerializeField] private int requiredReadyPlayers = 2;

    [Header("Waves")]
    [Tooltip("Zombies in wave 1")]
    [SerializeField] private int baseZombiesPerWave = 2;
    [Tooltip("Extra zombies added each wave")]
    [SerializeField] private int zombiesAddedPerWave = 1;
    [Tooltip("Maximum zombies alive at once")]
    [SerializeField] private int maxSimultaneousZombies = 4;
    [Tooltip("Seconds between all players ready and the first wave")]
    [SerializeField] private float firstWaveDelay = 30f;
    [Tooltip("Intermission seconds between waves (reload / reposition)")]
    [SerializeField] private float intermissionTime = 15f;
    [Tooltip("Seconds between spawns within a wave")]
    [SerializeField] private float spawnInterval = 2f;

    [Header("Events (corren en todos los clientes)")]
    public UnityEvent<int> OnWaveStarted;
    public UnityEvent<int> OnIntermissionStarted;
    public UnityEvent OnAllPlayersReady;

    [Networked, OnChangedRender(nameof(OnWaveChanged))]
    public int CurrentWave { get; private set; }

    [Networked, OnChangedRender(nameof(OnIntermissionChanged))]
    public NetworkBool IsIntermission { get; private set; }

    [Networked, OnChangedRender(nameof(OnWaitingChanged))]
    public NetworkBool WaitingForPlayers { get; private set; }

    [Networked] private TickTimer IntermissionTimer { get; set; }
    [Networked] private TickTimer SpawnTimer { get; set; }
    [Networked] private int ZombiesLeftToSpawn { get; set; }

    private List<NetworkZombie> _spawnedThisWave = new();
    private TickTimer _waitLogTimer;

    public override void Spawned()
    {
        if (Object.HasStateAuthority)
        {
            CurrentWave = 0;
            IsIntermission = true;
            WaitingForPlayers = true;
            Debug.Log("[Waves] Waiting for " + requiredReadyPlayers + " player(s) to grab a weapon...");
        }
        base.Spawned();
    }

    public override void FixedUpdateNetwork()
    {
        if (!Object.HasStateAuthority) return;

        if (WaitingForPlayers)
        {
            int ready = CountReadyPlayers();

            if (_waitLogTimer.ExpiredOrNotRunning(Runner))
            {
                Debug.Log("[Waves] Players ready: " + ready + "/" + requiredReadyPlayers);
                _waitLogTimer = TickTimer.CreateFromSeconds(Runner, 3f);
            }

            if (ready >= requiredReadyPlayers)
            {
                WaitingForPlayers = false;
                IntermissionTimer = TickTimer.CreateFromSeconds(Runner, firstWaveDelay);
                Debug.Log("[Waves] All ready! First wave in " + firstWaveDelay + "s. Ready your weapons!");
            }
            return;
        }

        _spawnedThisWave.RemoveAll(z => z == null);

        if (IsIntermission)
        {
            if (IntermissionTimer.Expired(Runner))
                StartNextWave();
            return;
        }

        if (ZombiesLeftToSpawn > 0 && AliveCount() < maxSimultaneousZombies)
        {
            if (SpawnTimer.ExpiredOrNotRunning(Runner))
            {
                SpawnZombie();
                ZombiesLeftToSpawn--;
                SpawnTimer = TickTimer.CreateFromSeconds(Runner, spawnInterval);
            }
        }

        if (ZombiesLeftToSpawn <= 0 && AliveCount() == 0)
        {
            IsIntermission = true;
            IntermissionTimer = TickTimer.CreateFromSeconds(Runner, intermissionTime);
            Debug.Log("[Waves] Wave " + CurrentWave + " completed! Intermission of " + intermissionTime + "s. Reload!");
        }
    }

    private int CountReadyPlayers()
    {
        var players = UnityEngine.Object.FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None);
        int ready = 0;
        foreach (var p in players)
            if (p != null && p.Object != null && p.Object.IsValid && p.Ready) ready++;
        return ready;
    }

    private void StartNextWave()
    {
        CurrentWave++;
        IsIntermission = false;
        ZombiesLeftToSpawn = baseZombiesPerWave + (CurrentWave - 1) * zombiesAddedPerWave;
        SpawnTimer = TickTimer.None;
        _spawnedThisWave.Clear();
        Debug.Log("[Waves] WAVE " + CurrentWave + " started! Zombies: " + ZombiesLeftToSpawn);
    }

    private int AliveCount()
    {
        int count = 0;
        foreach (var z in UnityEngine.Object.FindObjectsByType<NetworkZombie>(FindObjectsSortMode.None))
        {
            if (z == null || z.Object == null || !z.Object.IsValid) { count++; continue; }
            if (!z.IsDead) count++;
        }
        return count;
    }

    private void SpawnZombie()
    {
        if (zombiePrefab == null)
        {
            Debug.LogWarning("[Waves] zombiePrefab not assigned");
            return;
        }

        Vector3 pos = transform.position;
        Quaternion rot = transform.rotation;
        if (spawnPoints != null && spawnPoints.Length > 0)
        {
            var sp = spawnPoints[Random.Range(0, spawnPoints.Length)];
            if (sp != null) { pos = sp.position; rot = sp.rotation; }
        }

        Vector2 offset = Random.insideUnitCircle * 1.5f;
        pos += new Vector3(offset.x, 0f, offset.y);

        if (UnityEngine.AI.NavMesh.SamplePosition(pos, out UnityEngine.AI.NavMeshHit hit, 3f, UnityEngine.AI.NavMesh.AllAreas))
            pos = hit.position;

        var zombieObj = Runner.Spawn(zombiePrefab, pos, rot, Runner.LocalPlayer);
        if (zombieObj != null)
        {
            var zombie = zombieObj.GetComponent<NetworkZombie>();
            if (zombie != null) _spawnedThisWave.Add(zombie);
            Debug.Log("[Waves] Zombie spawned (" + _spawnedThisWave.Count + " this wave, " + (ZombiesLeftToSpawn - 1) + " left to spawn)");
        }
    }

    private void OnWaveChanged()
    {
        if (CurrentWave > 0)
            OnWaveStarted?.Invoke(CurrentWave);
    }

    private void OnIntermissionChanged()
    {
        if (IsIntermission && CurrentWave > 0)
            OnIntermissionStarted?.Invoke(CurrentWave + 1);
    }

    private void OnWaitingChanged()
    {
        if (!WaitingForPlayers)
            OnAllPlayersReady?.Invoke();
    }
}
