using UnityEngine;
using Fusion;
using TMPro;
using VRZ.Core;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.FX;
using VRZ.World;

namespace VRZ.Network
{

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
            SetText("Connecting...");
                return;
            }

            var runner = nm.runner;

            int playerCount = 0;
            foreach (var p in runner.ActivePlayers) playerCount++;

            if (playerCount < requiredPlayers)
            {
                _countdown = -1f;
            SetText("WAITING FOR PLAYER...\n(" + playerCount + "/" + requiredPlayers + ")");
                return;
            }

            // Gate: every connected player must have finished the lobby tutorial
            int tutorialDone = 0;
            foreach (var p in runner.ActivePlayers)
            {
                var np = nm.GetPlayer(p);
                if (np != null && np.Object != null && np.Object.IsValid && np.TutorialDone)
                    tutorialDone++;
            }
            if (tutorialDone < playerCount)
            {
                _countdown = -1f;
                SetText("COMPLETE THE TUTORIAL!\n(" + tutorialDone + "/" + playerCount + " ready)");
                return;
            }

            if (_countdown < 0f)
                _countdown = startCountdown;

            _countdown -= Time.deltaTime;

            if (_countdown > 0f)
            {
            SetText("PLAYER CONNECTED!\nStarting in " + Mathf.CeilToInt(_countdown) + "...");
            }
            else if (!_loading)
            {
            SetText("LOADING GAME...");
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
}
