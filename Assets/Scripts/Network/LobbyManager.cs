using UnityEngine;
using Fusion;
using TMPro;

/// Lobby flow: player ready checks and networked transition into the game scene.
public class LobbyManager : MonoBehaviour
{
    [Header("Config")]
    [Tooltip("Players required to start the match")]
    [SerializeField] private int requiredPlayers = 2;
    [Tooltip("Segundos de cuenta atras una vez todos conectados")]
    [SerializeField] private float startCountdown = 5f;
    [Tooltip("Build index of the game scene")]
    [SerializeField] private int gameSceneIndex = 1;

    [Header("UI (world space)")]
    [SerializeField] private TextMeshPro statusText;

    private float _countdown = -1f;
    private bool _loading;

    private void Update()
    {
        var nm = NetworkManager.instance;
        if (nm == null || nm.runner == null || !nm.runner.IsRunning)
        {
            SetText("Conectando...");
            return;
        }

        var runner = nm.runner;

        int playerCount = 0;
        foreach (var p in runner.ActivePlayers) playerCount++;

        if (playerCount < requiredPlayers)
        {
            _countdown = -1f;
            SetText("ESPERANDO JUGADOR...\n(" + playerCount + "/" + requiredPlayers + ")");
            return;
        }

        if (_countdown < 0f)
            _countdown = startCountdown;

        _countdown -= Time.deltaTime;

        if (_countdown > 0f)
        {
            SetText("JUGADOR CONECTADO!\nIniciando en " + Mathf.CeilToInt(_countdown) + "...");
        }
        else if (!_loading)
        {
            SetText("CARGANDO PARTIDA...");
            if (runner.IsSharedModeMasterClient)
            {
                _loading = true;
                runner.LoadScene(SceneRef.FromIndex(gameSceneIndex));
            }
        }
    }

    private void SetText(string msg)
    {
        if (statusText != null && statusText.text != msg)
            statusText.text = msg;
    }
}
