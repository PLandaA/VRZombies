using System.Collections.Generic;
using UnityEngine;
using VRZ.Core;
using Fusion;
using UnityEngine.Events;

namespace VRZ.Enemies{
    /// Wave system: spawns zombie rounds, tracks alive count, handles intermissions and player-ready gating.
    public class ZombieSpawner : NetworkBehaviour{
        [Header("Prefab")] [SerializeField] private NetworkObject zombiePrefab;

        [Header("Spawn Points")] [Tooltip("If empty, spawns around the spawner's position")] [SerializeField]
        private Transform[] spawnPoints;

        [Header("Ready Check")]
        [Tooltip("How many players must be Ready (grab a weapon) to start. Production value; for solo testing use the VRZ_SOLO_TEST define (see Core/DevFlags.cs), never edit this in the scene.")]
        [SerializeField]
        private int requiredReadyPlayers = 2;

        /// The gate actually used at runtime: the serialized value, or 1 under VRZ_SOLO_TEST.
        private int RequiredReady => DevFlags.MinPlayers(requiredReadyPlayers);

        [Header("Waves")] [Tooltip("Zombies in wave 1")] [SerializeField]
        private int baseZombiesPerWave = 2;

        [Tooltip("Extra zombies added each wave")] [SerializeField]
        private int zombiesAddedPerWave = 2;

        [Tooltip("Maximum zombies alive at once")] [SerializeField]
        private int maxSimultaneousZombies = 6;

        [Tooltip("Seconds between all players ready and the first wave")] [SerializeField]
        private float firstWaveDelay = 15f;

        [Tooltip("Intermission seconds between waves (reload / reposition)")] [SerializeField]
        private float intermissionTime = 15f;

        [Tooltip("Seconds between spawns within a wave")] [SerializeField]
        private float spawnInterval = 2f;

        [Header("Events (corren en todos los clientes)")]
        public UnityEvent<int> OnWaveStarted;

        public UnityEvent<int> OnIntermissionStarted;
        public UnityEvent OnAllPlayersReady;

        [Networked, OnChangedRender(nameof(OnWaveChanged))]
        public int CurrentWave{ get; private set; }

        [Networked, OnChangedRender(nameof(OnIntermissionChanged))]
        public NetworkBool IsIntermission{ get; private set; }

        [Networked, OnChangedRender(nameof(OnGameOverChanged))]
        public NetworkBool GameOver{ get; private set; }

        /// Fired in Render on every client the frame GameOver turns true (same mechanism as the
        /// other three flags). Static so a listener that outlives scenes (GameOverController) can
        /// subscribe once without ever searching for the spawner. Not raised for the initial
        /// value a late joiner receives; irrelevant here (2-player room, GameOver starts false).
        public static event System.Action<ZombieSpawner> GameOverRaised;

        [Networked, OnChangedRender(nameof(OnWaitingChanged))]
        public NetworkBool WaitingForPlayers{ get; private set; }

        [Networked] private TickTimer IntermissionTimer{ get; set; }
        [Networked] private TickTimer SpawnTimer{ get; set; }
        [Networked] private int ZombiesLeftToSpawn{ get; set; }

        private List<NetworkZombie> _spawnedThisWave = new();
#if VRZ_NET_DIAGNOSTICS
        private TickTimer _waitLogTimer;
#endif

        /// The spawner registers itself so readers (GameOverController, RoundJuice,
        /// PlayerBelt, AmmoSpawner) never need FindFirstObjectByType. Set in Awake,
        /// not Spawned: it is a scene object, and subscribers wire their UnityEvent listeners from
        /// their own Start(), which can run before Fusion attaches scene objects. Null in the lobby.
        public static ZombieSpawner Current { get; private set; }

        private void Awake(){
            Current = this;
            // The arena NavMesh is ~12k triangles over 254x254 m; Unity's default async budget
            // (100 iterations/frame, shared by every agent) made long paths take seconds. Chasing
            // now computes synchronously, but wander/retreat still go through SetDestination.
            UnityEngine.AI.NavMesh.pathfindingIterationsPerFrame = 600;
        }
        private void OnDestroy(){ if (Current == this) Current = null; }

        public override void Spawned(){
            Current = this;

            // Object pool. Runs on EVERY client (the partner recycles the proxies Fusion
            // creates for it). Prewarm = worst case alive + corpses waiting to despawn, so during
            // the match no zombie ever pays an Instantiate (measured 1.4-1.8 ms on PC, more on Quest).
            var pool = VRZ.Network.NetworkManager.instance != null ? VRZ.Network.NetworkManager.instance.ObjectPool : null;
            if (pool != null && zombiePrefab != null)
                pool.EnablePooling(Runner, zombiePrefab, maxSimultaneousZombies * 2);

            if (Object.HasStateAuthority){
                CurrentWave = 0;
                IsIntermission = true;
                WaitingForPlayers = true;
                Debug.Log("[Waves] Waiting for " + RequiredReady + " player(s) to grab a weapon...");
            }

            base.Spawned();
        }

        public override void Despawned(NetworkRunner runner, bool hasState){
            if (Current == this) Current = null;
            base.Despawned(runner, hasState);
        }

        public override void FixedUpdateNetwork(){
            if (Object.HasStateAuthority && !GameOver && CurrentWave > 0
                && WaveRules.AllPlayersDead(NetworkSession.Current.Players)){
                GameOver = true;
                Debug.Log("[Waves] GAME OVER - all players dead.");
            }

            if (GameOver) return;

            if (WaitingForPlayers){
                int ready = CountReadyPlayers();

#if VRZ_NET_DIAGNOSTICS
                if (_waitLogTimer.ExpiredOrNotRunning(Runner)){
                    Debug.Log("[Waves] Players ready: " + ready + "/" + RequiredReady);
                    _waitLogTimer = TickTimer.CreateFromSeconds(Runner, 3f);
                }
#endif

                if (ready >= RequiredReady){
                    WaitingForPlayers = false;
                    IntermissionTimer = TickTimer.CreateFromSeconds(Runner, firstWaveDelay);
                    Debug.Log("[Waves] All ready! First wave in " + firstWaveDelay + "s. Ready your weapons!");
                }

                return;
            }

            if (IsIntermission){
                if (IntermissionTimer.Expired(Runner))
                    StartNextWave();
                return;
            }

            if (ZombiesLeftToSpawn > 0 && AliveCount() < maxSimultaneousZombies){
                if (SpawnTimer.ExpiredOrNotRunning(Runner)){
                    SpawnZombie();
                    ZombiesLeftToSpawn--;
                    SpawnTimer = TickTimer.CreateFromSeconds(Runner, spawnInterval);
                }
            }

            if (ZombiesLeftToSpawn <= 0 && AliveCount() == 0){
                IsIntermission = true;
                IntermissionTimer = TickTimer.CreateFromSeconds(Runner, intermissionTime);
                Debug.Log("[Waves] Wave " + CurrentWave + " completed! Intermission of " + intermissionTime +
                          "s. Reload!");
            }
        }

        private int CountReadyPlayers() => WaveRules.CountReady(NetworkSession.Current.Players);

        private void StartNextWave(){
            CurrentWave++;
            IsIntermission = false;
            ZombiesLeftToSpawn = WaveRules.ZombiesForWave(CurrentWave, baseZombiesPerWave, zombiesAddedPerWave);
            SpawnTimer = TickTimer.None;
            _spawnedThisWave.Clear();
            _aliveDirty = true;
            Debug.Log("[Waves] WAVE " + CurrentWave + " started! Zombies: " + ZombiesLeftToSpawn);
        }

        // ── Alive count: DIRTY FLAG ──
        // The count only changes when a zombie spawns or dies, yet it used to be recomputed twice
        // per tick with FindObjectsByType (a full scene scan each). Now it is cached and
        // recomputed only after those two events flip the flag.
        private int _aliveCache;
        private bool _aliveDirty = true;

        private void OnEnable(){
            NetworkZombie.OnAnyDied += OnZombieDied;
        }

        private void OnDisable(){
            NetworkZombie.OnAnyDied -= OnZombieDied;
        }

        private void OnZombieDied(NetworkZombie z){
            _aliveDirty = true;
        }

        private int AliveCount(){
            if (!_aliveDirty) return _aliveCache;

            _spawnedThisWave.RemoveAll(z => z == null);
            int count = 0;
            foreach (var z in _spawnedThisWave){
                // The old "spawn in flight" branch (Object not yet valid counts as alive)
                // came from Host Mode, where a client's Spawn is deferred. In Shared Mode
                // Runner.Spawn attaches synchronously on the authority, so an invalid Object here
                // only means "already despawned": not alive.
                if (z.Object == null || !z.Object.IsValid) continue;
                if (!z.IsDead) count++;
            }

            _aliveCache = count;
            _aliveDirty = false;
            return count;
        }

        private void SpawnZombie(){
            if (zombiePrefab == null){
                Debug.LogWarning("[Waves] zombiePrefab not assigned");
                return;
            }

            Vector3 pos = transform.position;
            Quaternion rot = transform.rotation;
            if (spawnPoints != null && spawnPoints.Length > 0){
                var sp = spawnPoints[Random.Range(0, spawnPoints.Length)];
                if (sp != null){
                    pos = sp.position;
                    rot = sp.rotation;
                }
            }

            Vector2 offset = Random.insideUnitCircle * 1.5f;
            pos += new Vector3(offset.x, 0f, offset.y);

            if (UnityEngine.AI.NavMesh.SamplePosition(pos, out UnityEngine.AI.NavMeshHit hit, 3f,
                    UnityEngine.AI.NavMesh.AllAreas))
                pos = hit.position;

            // Measurement for the pooling decision (journal): how long the full prefab Instantiate
            // + Fusion attach takes on this device. Editor/dev builds only; compiled out otherwise.
#if VRZ_NET_DIAGNOSTICS
            var sw = System.Diagnostics.Stopwatch.StartNew();
#endif
            // No inputAuthority argument. In Shared Mode the spawning client is the
            // State Authority; the PlayerRef the old call passed was never read by the zombie.
            var zombieObj = Runner.Spawn(zombiePrefab, pos, rot);
#if VRZ_NET_DIAGNOSTICS
            sw.Stop();
            Debug.Log("[Waves] Spawn took " + sw.Elapsed.TotalMilliseconds.ToString("F2") + " ms");
#endif
            if (zombieObj != null){
                var zombie = zombieObj.GetComponent<NetworkZombie>();
                if (zombie != null){
                    _spawnedThisWave.Add(zombie);
                    _aliveDirty = true;
                }

#if VRZ_NET_DIAGNOSTICS
                Debug.Log("[Waves] Zombie spawned (" + _spawnedThisWave.Count + " this wave, " +
                          (ZombiesLeftToSpawn - 1) + " left to spawn)");
#endif
            }
        }

        private void OnWaveChanged(){
            if (CurrentWave > 0)
                OnWaveStarted?.Invoke(CurrentWave);
        }

        private void OnIntermissionChanged(){
            if (IsIntermission && CurrentWave > 0)
                OnIntermissionStarted?.Invoke(CurrentWave + 1);
        }

        private void OnWaitingChanged(){
            if (!WaitingForPlayers)
                OnAllPlayersReady?.Invoke();
        }

        private void OnGameOverChanged(){
            if (GameOver)
                GameOverRaised?.Invoke(this);
        }
    }
}