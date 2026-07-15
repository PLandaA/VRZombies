using Fusion;
using Fusion.Sockets;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
/// Fusion session management with deterministic per-player spawn-point teleporting. Modified from the course base.
public class NetworkManager : MonoBehaviour, INetworkRunnerCallbacks
{
    public static NetworkManager instance { get; private set; }

    [SerializeField] private GameObject networkRunnerPrefab;
    [SerializeField] private NetworkObject playerPrefab;

    private Dictionary<PlayerRef, NetworkPlayer> NetworkPlayers = new();
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
            SessionName = "Test",
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
            onPlayerSpawn?.Invoke(runner, player);
        }
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
                Debug.LogWarning("[NetworkManager] No PlayerSpawnPoints encontrados.");
                return;
            }
            spawnPoints = list.ToArray();
        }

        System.Array.Sort(spawnPoints, (a, b) => string.CompareOrdinal(a.name, b.name));
        int idx = 0;
        if (runner != null && runner.LocalPlayer.IsRealPlayer)
            idx = Mathf.Abs(runner.LocalPlayer.PlayerId) % spawnPoints.Length;
        var sp = spawnPoints[idx];

        var rig = FindObjectOfType<HardwareRig>();
        if (rig == null) { Debug.LogWarning("[NetworkManager] HardwareRig no encontrado."); return; }

        rig.transform.position = sp.transform.position;
        rig.transform.rotation = sp.transform.rotation;
        Debug.Log("[NetworkManager] Player " + (runner != null ? runner.LocalPlayer.PlayerId.ToString() : "?") + " teleportado a " + sp.name + " en " + sp.transform.position);
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
        onSceneLoadDone?.Invoke(runner);
    }
    public void OnSceneLoadStart(NetworkRunner runner)
    {
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