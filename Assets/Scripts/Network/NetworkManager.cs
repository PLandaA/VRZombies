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

        // ── Session menu (B2) ─────────────────────────────────────────────────────────────
        // The manager no longer connects on Start. It first joins Photon's session LOBBY
        // (a directory, not a room) so the menu can list open rooms; the menu then asks us to
        // create a room with a random 2-digit code, or to join a listed one.

        public enum SessionState { Offline, BrowsingLobby, Connecting, Connected, Failed }

        public const string SessionPrefix = "VRZ-";

        /// Lobby directory name. Includes the build version so two different builds never see
        /// (or join) each other's rooms.
        public static string LobbyName => "VRZ-" + Application.version;

        public SessionState State { get; private set; } = SessionState.Offline;

        /// Code of the room we created or joined ("" until connected).
        public string CurrentCode { get; private set; } = "";

        /// Last error shown by the menu. Survives the manager (static) so a reload after a
        /// failed connection can still display it.
        public static string LastError { get; private set; } = "";

        /// Open rooms seen in the lobby directory (name, players, capacity). Refreshed by Fusion.
        public IReadOnlyList<SessionInfo> Sessions => _sessions;
        private readonly List<SessionInfo> _sessions = new();

        public event Action OnSessionsChanged;
        public event Action<SessionState> OnStateChanged;

        private void SetState(SessionState s)
        {
            if (State == s) return;
            State = s;
            OnStateChanged?.Invoke(s);
        }
        private void Awake()
        {
            if (instance != null && instance != this)
            {
                // A live session already owns the singleton (normal case: arriving in the arena
                // with the lobby's manager still running). This scene copy is redundant.
                if (instance.runner != null && instance.runner.IsRunning)
                {
                    Destroy(gameObject);
                    return;
                }

                // The old singleton is stale: its runner was shut down (game over, disconnect,
                // failed StartGame) and it will never call ConnectGame again. Let the fresh
                // scene copy take over so returning to the lobby actually reconnects.
                Debug.Log("[NetworkManager] Replacing stale singleton (runner not running).");
                Destroy(instance.gameObject);
                instance = null;
            }

            instance = this;
            NetworkSession.Override(this);   // Core ships no default session; the network layer is it
            DontDestroyOnLoad(gameObject);
            CreateNetworkRunner();
        }
            private void Start()
        {
            if (instance != this) return;
            if (runner != null && runner.IsRunning) return;
            EnterLobby();
        }
        private void CreateNetworkRunner()
        {
            if (!runner) runner = Instantiate(networkRunnerPrefab, transform).GetComponent<NetworkRunner>();
            runner.AddCallbacks(this);
            // Object pool (debt D1): lives on the manager so it is DontDestroyOnLoad like the runner.
            if (ObjectPool == null) ObjectPool = gameObject.AddComponent<PooledObjectProvider>();
        }

        /// The runner's INetworkObjectProvider. Spawners opt their prefabs in (ZombieSpawner does).
        public PooledObjectProvider ObjectPool { get; private set; }

        // ── Avatar registry (debt D3) ────────────────────────────────────────────────────────
        // Zombies re-target every 2 s and grenades scan on explosion; both used to
        // FindObjectsByType<NetworkRig>, an O(scene) walk. Rigs register themselves in Spawned /
        // Despawned instead, so readers get an O(1) list. Local rig included (it is a proxy of
        // nobody, but callers filter by authority as before).
        private readonly List<VRZ.Player.NetworkRig> _rigs = new();
        public IReadOnlyList<VRZ.Player.NetworkRig> Rigs => _rigs;

        public void RegisterRig(VRZ.Player.NetworkRig rig)   { if (rig != null && !_rigs.Contains(rig)) _rigs.Add(rig); }
        public void UnregisterRig(VRZ.Player.NetworkRig rig) { _rigs.Remove(rig); }

        /// Join the session directory so OnSessionListUpdated starts arriving. No room yet.
        public async void EnterLobby()
        {
            if (runner == null) return;
            SetState(SessionState.BrowsingLobby);

            // D2: we are here because our previous create collided. Create again as soon as the
            // room list arrives (we need it to pick a code that is not taken).
            if (RetryCreateAfterReload) { RetryCreateAfterReload = false; _createWhenListArrives = true; }

            var result = await runner.JoinSessionLobby(SessionLobby.Custom, LobbyName);

            if (!result.Ok)
            {
                _createWhenListArrives = false;
                LastError = "Lobby unavailable: " + result.ShutdownReason;
                Debug.LogError("[NetworkManager] JoinSessionLobby failed: " + result.ShutdownReason + " " + result.ErrorMessage);
                SetState(SessionState.Failed);
            }
        }

        /// Menu → "Create Lobby". Picks a 2-digit code not currently in use and opens the room.
        public void CreateSession()
        {
            if (State != SessionState.BrowsingLobby) return;

            var taken = new HashSet<string>();
            foreach (var s in _sessions) taken.Add(s.Name);

            // RoomCodeRules (unit-tested): random 10..99 not in the list, with an ordered scan as
            // fallback, so a free code is always found if one exists.
            string name = RoomCodeRules.Pick(taken, SessionPrefix, UnityEngine.Random.Range);
            if (name == null)
            {
                LastError = "All room codes are in use. Try again.";
                SetState(SessionState.Failed);
                return;
            }
            _creating = true;
            ConnectGame(name);
        }

        /// Menu → tapped a row in the room list.
        public void JoinSession(string sessionName)
        {
            if (State != SessionState.BrowsingLobby) return;
            if (string.IsNullOrEmpty(sessionName)) return;
            ConnectGame(sessionName);
        }

        public async void ConnectGame(string sessionName)
        {
            if (runner == null) return;
            SetState(SessionState.Connecting);
            LastError = "";
            OnConnectionStart.Invoke();

            var args = new StartGameArgs()
            {
                GameMode = GameMode.Shared,
                SessionName = sessionName,
                CustomLobbyName = LobbyName,
                PlayerCount = 2,
                IsVisible = true,
                IsOpen = true,
                Scene = SceneRef.FromIndex(SceneManager.GetActiveScene().buildIndex),
                SceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>(),
                ObjectProvider = ObjectPool
            };

            StartGameResult connectionResult;
            try
            {
                connectionResult = await runner.StartGame(args);
            }
            catch (Exception e)
            {
                LastError = "Connection error: " + e.Message;
                Debug.LogException(e);
                SetState(SessionState.Failed);
                return;
            }

            if (connectionResult.Ok)
            {
                // Debt D2: "Create" with a 2-digit code that somebody else created in the same
                // instant does not fail: Fusion silently JOINS their room. Detect it: a room we
                // just created must contain only us. If it does not, leave and try a new code.
                if (_creating && runner.SessionInfo.IsValid && runner.SessionInfo.PlayerCount > 1)
                {
                    Debug.LogWarning("[NetworkManager] Room code collision on " + sessionName + " (" + runner.SessionInfo.PlayerCount + " players): retrying with a new code.");
                    _creating = false;
                    RetryCreateAfterReload = true;     // survives this manager (static); see EnterLobby
                    LastError = "";
                    var shutdown = runner.Shutdown();
                    while (!shutdown.IsCompleted) await System.Threading.Tasks.Task.Yield();
                    // OnShutdown (reason Ok) destroyed this manager; the flag reloads the lobby.
                    return;
                }
                _creating = false;

                CurrentCode = RoomCodeRules.DisplayCode(sessionName, SessionPrefix);
                SetState(SessionState.Connected);
                OnConnectionSuccessfull.Invoke();
                Debug.Log("StartGame successfull: " + sessionName);
            }
            else
            {
                _creating = false;
                LastError = "Could not join room: " + connectionResult.ShutdownReason;
                Debug.LogError("[NetworkManager] StartGame failed: " + connectionResult.ShutdownReason + " " + connectionResult.ErrorMessage);
                SetState(SessionState.Failed);
                // A failed StartGame shuts the runner down; OnShutdown reloads the lobby so
                // the menu comes back with LastError on screen.
            }
        }

        /// True while a CreateSession() is in flight, so ConnectGame can tell "I created this
        /// room" from "I joined a listed one" (D2 collision check applies only to the former).
        private bool _creating;

        /// D2: set when a create collided; the next NetworkManager (after the lobby reload)
        /// creates again automatically as soon as it has the room list, without a click.
        public static bool RetryCreateAfterReload { get; private set; }
        private bool _createWhenListArrives;
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
                // Netcode debt #5: the marker lookup that used to live here (tag "Respawn" /
                // "PlayerSpawnPoint" names) matched nothing in any scene; MapDefault.SpawnCharacter
                // positions the rig from its SpawnPoints list. Only the grounding pass remains.
                SnapRigToGround(null);

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

        /// Places the local rig on solid ground. With a spawn marker it teleports there first;
        /// without one it just grounds the rig where it already stands. Probing down
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

        // Netcode fix B4 (minimum): the master client owns the wave spawner, the ammo dispenser
        // and every zombie. When it leaves, Fusion orphans or destroys those objects and the
        // survivor is stuck in a frozen, unfinishable arena. We cannot restore the match (the
        // spawner's wave state is not replicated), so we end it cleanly instead.
        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            Debug.Log("[NetworkManager] Player left: " + player);
            RemovePlayer(player);

            bool inArena = SceneManager.GetActiveScene().buildIndex != 0;
            if (inArena) StartCoroutine(EndMatchIfOrphaned());
        }

        private IEnumerator EndMatchIfOrphaned()
        {
            // Give Fusion a moment to apply the departed player's object flags, then ask the spawner
            // itself. This is the only reliable signal: an earlier second check ("I was not master
            // and now I am") depends on when Photon promotes the new master relative to this
            // callback, and in two-build tests it did not fire in time while this one always did.
            yield return new WaitForSeconds(0.25f);

            var spawner = VRZ.Enemies.ZombieSpawner.Current;   // self-registered (fix A8)
            bool spawnerMissing = spawner == null || spawner.Object == null || !spawner.Object.IsValid;
            bool spawnerOrphaned = !spawnerMissing && spawner.Object.StateAuthority == PlayerRef.None;

            if (!spawnerMissing && !spawnerOrphaned) yield break;   // a non-master left: the match goes on

            Debug.LogWarning("[NetworkManager] Wave spawner " + (spawnerMissing ? "gone" : "orphaned") + " after a player left: the host is gone. Ending the match.");
            var go = VRZ.World.GameOverController.Instance;
            if (go != null) go.EndMatch("PARTNER LEFT", "The host disconnected. Returning to the lobby...", 6f);
        }

        public void OnInput(NetworkRunner runner, NetworkInput input)
        {

        }

        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input)
        {

        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            Debug.Log("[NetworkManager] Runner shut down: " + shutdownReason);

            // Every NetworkPlayer died with the runner; drop the registry so nobody reads ghosts.
            NetworkPlayers.Clear();
            _sessions.Clear();
            CurrentCode = "";
            SetState(SessionState.Offline);

            if (instance == this)
            {
                instance = null;
                // Readers keep calling NetworkSession.Current between here and the next
                // NetworkManager.Awake: give them an empty session instead of a null.
                NetworkSession.Override(new NullNetworkSession());
            }
            this.runner = null;

            // This manager can never reconnect on its own. The lobby scene ships its own
            // NetworkManager, which becomes the singleton and re-enters the session lobby.
            Destroy(gameObject);

            // D2: a voluntary shutdown caused by a room-code collision. Nobody else reloads the
            // lobby in that case (GameOverController is not running); do it so the fresh manager
            // can retry the create.
            if (shutdownReason == ShutdownReason.Ok && RetryCreateAfterReload)
            {
                SceneManager.LoadScene(0);
                return;
            }

            // Voluntary shutdown (game over) is followed by LoadScene(0) in GameOverController.
            // Anything else (lost connection, failed StartGame) has nobody to bring the player
            // back: do it ourselves so the menu reappears with LastError.
            if (shutdownReason != ShutdownReason.Ok)
            {
                if (string.IsNullOrEmpty(LastError)) LastError = "Disconnected: " + shutdownReason;

                // Netcode fix A7: in the arena, give the player a few seconds of "CONNECTION LOST"
                // instead of a silent cut to the lobby. The controller finds no live runner, skips
                // Shutdown() and loads scene 0 after the hold. In the lobby, reload straight away.
                bool inArena = SceneManager.GetActiveScene().buildIndex != 0;
                var go = VRZ.World.GameOverController.Instance;
                if (inArena && go != null)
                    go.EndMatch("CONNECTION LOST", LastError + "\nReturning to the lobby...", 5f);
                else
                    SceneManager.LoadScene(0);
            }
        }

        public void OnConnectedToServer(NetworkRunner runner)
        {
            Debug.Log("[NetworkManager] Connected to Photon (region/cloud).");
        }

        public void OnDisconnectedFromServer(NetworkRunner runner)
        {
            // Legacy overload kept by the interface; the reasoned one below does the work.
        }

        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token)
        {

        }

        // Netcode fix A7. Fusion shuts the runner down right after these; OnShutdown then reloads
        // the lobby with LastError on the menu. All we must do here is leave a human-readable
        // reason behind, before OnShutdown's generic "Disconnected: <reason>" fallback.
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
            LastError = "Could not reach Photon (" + reason + "). Check your connection and try again.";
            Debug.LogError("[NetworkManager] Connect failed: " + reason + " @ " + remoteAddress);
            SetState(SessionState.Failed);
        }

        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message)
        {

        }

        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            _sessions.Clear();
            foreach (var s in sessionList)
            {
                // Only rooms a player can actually enter: our prefix, open, visible, with a free seat.
                if (!s.Name.StartsWith(SessionPrefix)) continue;
                if (!s.IsOpen || !s.IsVisible) continue;
                if (s.PlayerCount >= s.MaxPlayers) continue;
                _sessions.Add(s);
            }
            OnSessionsChanged?.Invoke();

            // D2 retry: first list after a collision reload -> create with a fresh code.
            if (_createWhenListArrives && State == SessionState.BrowsingLobby)
            {
                _createWhenListArrives = false;
                CreateSession();
            }
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

            // Once the match starts nobody else may enter: the room stays alive while a player is
            // in it, and with the session menu (B2) a room with a free seat is listed to everyone.
            // Only the master client may change session settings in Shared Mode.
            bool inArena = SceneManager.GetActiveScene().buildIndex != 0;
            if (inArena && runner.IsSharedModeMasterClient && runner.SessionInfo != null && runner.SessionInfo.IsValid)
            {
                runner.SessionInfo.IsOpen = false;
                Debug.Log("[NetworkManager] Room closed for the match.");
            }

            // Scene transitions do NOT re-fire OnPlayerJoined, so nothing re-grounded the rig on
            // arrival. GameMap.SpawnCharacter already moved it to its SpawnPoint; ground it there.
            StartCoroutine(PlaceRigAfterSceneLoad());
        }

        private IEnumerator PlaceRigAfterSceneLoad()
        {
            yield return null;                        // let the new scene's objects wake up
            SnapRigToGround(null);

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
            // Our own link dropped (timeout, kicked, server closed). OnShutdown follows and decides
            // how to bring the player back; here we only record why.
            LastError = "Connection lost (" + reason + ").";
            Debug.LogWarning("[NetworkManager] Disconnected from server: " + reason);
            SetState(SessionState.Failed);
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
