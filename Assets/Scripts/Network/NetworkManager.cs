using Fusion;
using Fusion.Sockets;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using VRZ.Core;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.FX;
using VRZ.World;

namespace VRZ.Network
{
    /// Fusion session management with deterministic per-player spawn-point teleporting. Modified from the course base.
    public class NetworkManager : MonoBehaviour, INetworkRunnerCallbacks, INetworkSession
    {
        // ── INetworkSession ──
        public bool IsRunning => runner != null && runner.IsRunning;
        public PlayerRef LocalPlayer => runner != null ? runner.LocalPlayer : PlayerRef.None;
        public NetworkPlayer GetPlayer() => GetPlayer(default(PlayerRef));   // optional params don't satisfy interfaces
        IReadOnlyCollection<IPlayerState> INetworkSession.Players => NetworkPlayers.Values;   // covariant view
        IPlayerState INetworkSession.GetPlayer() => GetPlayer();
        IPlayerState INetworkSession.GetPlayer(PlayerRef player) => GetPlayer(player);

        public static NetworkManager instance { get; private set; }

        [SerializeField] private GameObject networkRunnerPrefab;
        [SerializeField] private NetworkObject playerPrefab;

        private Dictionary<PlayerRef, NetworkPlayer> NetworkPlayers = new();

        /// Live registry of connected players (maintained on join/leave). Prefer this over
        /// FindObjectsByType: it is already the cached answer.
        public IReadOnlyCollection<NetworkPlayer> Players => NetworkPlayers.Values;
        public NetworkRunner runner;

        public UnityEvent OnConnectionStart;
        public UnityEvent OnConnectionSuccessfull;

        public delegate void OnPlayerSpawn(NetworkRunner runner, PlayerRef playerRef);
        public event OnPlayerSpawn onPlayerSpawn;

        public delegate void OnSceneLoadStartDelegate(NetworkRunner runner);
        public event OnSceneLoadStartDelegate onSceneLoadStart;

        public delegate void OnSceneLoadDoneDelegate(NetworkRunner runner);
        public event OnSceneLoadDoneDelegate onSceneLoadDone;
        private void Awake()
        {

            if (!instance)
            {
                instance = this;
                NetworkSession.Override(this);   // Core ships no default session; the network layer is it
                DontDestroyOnLoad(gameObject);
            }
                    else
            {
                Destroy(gameObject);
                return;
            }
            CreateNetworkRunner();
        }
            private void Start()
        {
            if (instance != this) return;
            if (runner != null && runner.IsRunning) return;
            ConnectGame();
        }
        private void CreateNetworkRunner()
        {
            if (!runner) runner = Instantiate(networkRunnerPrefab, transform).GetComponent<NetworkRunner>();
            runner.AddCallbacks(this);
        }

        public async void ConnectGame()
        {

            OnConnectionStart.Invoke();

            var args = new StartGameArgs()
            {
                GameMode = GameMode.Shared,
                PlayerCount = 2,
                Scene = SceneRef.FromIndex(SceneManager.GetActiveScene().buildIndex),
                SceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>()
            };

            var connectionResult = await runner.StartGame(args);

            if (connectionResult.Ok)
            {
                OnConnectionSuccessfull.Invoke();
                Debug.Log("StartGame successfull");
            }
            else
            {
                Debug.LogError(connectionResult.ErrorMessage);

            }
        }
        public void AddPlayer(PlayerRef player, NetworkPlayer networkPlayer)
        {
            NetworkPlayers[player] = networkPlayer;
            networkPlayer.transform.SetParent(runner.transform);
        }
        public NetworkPlayer GetPlayer(PlayerRef player = default)
        {
            if (!runner) return null;
            if (player == default) player = runner.LocalPlayer;

            NetworkPlayers.TryGetValue(player, out NetworkPlayer networkPlayer);
            return networkPlayer;
        }

        public void RemovePlayer(PlayerRef player)
        {
            if (NetworkPlayers.ContainsKey(player))
            {
                NetworkPlayers.Remove(player);
            }
            else
            {
                Debug.LogWarning("This player: " + player + " not found");
            }

        }

            private void SpawnPlayer(NetworkRunner runner, PlayerRef player)
        {
            if (player == runner.LocalPlayer)
            {
                TeleportLocalRigToSpawnPoint();

                runner.Spawn(playerPrefab, transform.position, transform.rotation, player);
                StartCoroutine(FirePlayerSpawnWhenReady(runner, player));
            }
        }

        /// The avatar is spawned by the scene's map (LobbyMap/GameMap) via onPlayerSpawn -- but
        /// Fusion can deliver OnPlayerJoined in the very frame the scene finished (re)loading,
        /// BEFORE the map's Start() subscribed. Firing into the void meant no avatar. Waiting for
        /// a subscriber makes the handshake deterministic regardless of who wins the frame race.
        private IEnumerator FirePlayerSpawnWhenReady(NetworkRunner runner, PlayerRef player)
        {
            float timeout = 3f;
            while (onPlayerSpawn == null && (timeout -= Time.deltaTime) > 0f)
                yield return null;

            if (onPlayerSpawn != null) onPlayerSpawn.Invoke(runner, player);
            else Debug.LogWarning("[NetworkManager] onPlayerSpawn had no subscribers after 3s -- no map in this scene?");
        }

        private void TeleportLocalRigToSpawnPoint()
        {
            var spawnPoints = GameObject.FindGameObjectsWithTag("Respawn");
            if (spawnPoints == null || spawnPoints.Length == 0)
            {
                var sp1 = GameObject.Find("PlayerSpawnPoint");
                var sp2 = GameObject.Find("PlayerSpawnPoint (1)");
                var list = new List<GameObject>();
                if (sp1 != null) list.Add(sp1);
                if (sp2 != null) list.Add(sp2);
                if (list.Count == 0)
                {
                    // No markers (the lobby): keep the authored spot, but still drop the rig onto
                    // the floor -- this early return used to skip grounding entirely.
                    Debug.Log("[NetworkManager] No PlayerSpawnPoints found (lobby): grounding the rig in place.");
                    SnapRigToGround(null);
                    return;
                }
                spawnPoints = list.ToArray();
            }

            System.Array.Sort(spawnPoints, (a, b) => string.CompareOrdinal(a.name, b.name));
            int idx = 0;
            if (runner != null && runner.LocalPlayer.IsRealPlayer)
                idx = Mathf.Abs(runner.LocalPlayer.PlayerId) % spawnPoints.Length;
            var sp = spawnPoints[idx];

            SnapRigToGround(sp.transform);
            Debug.Log("[NetworkManager] Player " + (runner != null ? runner.LocalPlayer.PlayerId.ToString() : "?") + " teleportado a " + sp.name);
        }

        /// Places the local rig on solid ground. With a spawn marker it teleports there first;
        /// without one it just grounds the rig where it already stands (lobby). Probing down
        /// avoids the "spawn slightly airborne then fall" opening, which feels terrible in VR.
        private void SnapRigToGround(Transform spawn)
        {
            var rig = FindFirstObjectByType<HardwareRig>();
            if (rig == null) { Debug.LogWarning("[NetworkManager] HardwareRig no encontrado."); return; }

            var ahp = FindFirstObjectByType<Autohand.AutoHandPlayer>();
            Vector3 pos = spawn != null ? spawn.position : rig.transform.position;

            int mask = (ahp != null && ahp.groundLayerMask.value != 0) ? ahp.groundLayerMask.value : ~0;
            if (Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out RaycastHit ground, 30f,
                    mask, QueryTriggerInteraction.Ignore))
            {
                // In-place grounding (spawn == null) only corrects a real drop, so a player who is
                // already walking never gets nudged by the safety pass.
                float drop = pos.y - ground.point.y;
                if (spawn == null && drop < 0.35f) return;
                pos.y = ground.point.y + 0.02f;
            }

            rig.transform.position = pos;
            if (spawn != null) rig.transform.rotation = spawn.rotation;

            // The physics body may sit outside the tracking rig hierarchy: move and calm it too,
            // otherwise it keeps any falling velocity and drifts after the teleport.
            if (ahp != null)
            {
                if (!ahp.transform.IsChildOf(rig.transform))
                    ahp.transform.position = pos;
                if (ahp.body != null)
                {
                    ahp.body.linearVelocity = Vector3.zero;
                    ahp.body.angularVelocity = Vector3.zero;
                }
            }
        }

        #region NetworkRunnerCallbacks

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            Debug.Log("NewPlayer Joined" + player);
            SpawnPlayer(runner, player);
        }
        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {

        }

        public void OnInput(NetworkRunner runner, NetworkInput input)
        {

        }

        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input)
        {

        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {

        }

        public void OnConnectedToServer(NetworkRunner runner)
        {

        }

        public void OnDisconnectedFromServer(NetworkRunner runner)
        {

        }

        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
        {

        }

        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {

        }

        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message)
        {

        }

        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {

        }

        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data)
        {

        }

        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken)
        {

        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ArraySegment<byte> data)
        {

        }

        public void OnSceneLoadDone(NetworkRunner runner)
        {
            if (_sceneLoadStartTime > 0f)
                Debug.Log("[SceneLoad] DONE in " + (Time.realtimeSinceStartup - _sceneLoadStartTime).ToString("F1") + "s");
            onSceneLoadDone?.Invoke(runner);

            // Scene transitions do NOT re-fire OnPlayerJoined, so nothing repositioned the rig on
            // arrival: it kept its lobby coordinates and free-fell into the arena. Place it now.
            StartCoroutine(PlaceRigAfterSceneLoad());
        }

        private IEnumerator PlaceRigAfterSceneLoad()
        {
            yield return null;                        // let the new scene's objects wake up
            TeleportLocalRigToSpawnPoint();

            // Second pass: terrain and streamed colliders can initialise a frame or two late, in
            // which case the first probe finds nothing. Re-ground IN PLACE (no teleport) so the
            // player is never yanked back after they start moving.
            yield return new WaitForSeconds(0.4f);
            SnapRigToGround(null);
        }
        private float _sceneLoadStartTime;

        public void OnSceneLoadStart(NetworkRunner runner)
        {
            _sceneLoadStartTime = Time.realtimeSinceStartup;
            Debug.Log("[SceneLoad] START");
            onSceneLoadStart?.Invoke(runner);
        }

        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
        {

        }

        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player)
        {

        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {

        }

        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data)
        {

        }

        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress)
        {

        }
        #endregion
    }
}
