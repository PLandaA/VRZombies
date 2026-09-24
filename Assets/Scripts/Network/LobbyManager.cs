using UnityEngine;
using Fusion;
using TMPro;
using VRZ.Core;

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
        private bool _fadeStarted;
        private const float FadeOutSeconds = 1f;

        private void Update()
        {
            var nm = NetworkManager.instance;
            if (nm == null || nm.runner == null || !nm.runner.IsRunning)
            {
                // Before a room exists the SessionMenu owns the status text (menu, "Connecting...",
                // errors). Only show our own message when nobody else can.
                SetText(nm != null && nm.State != NetworkManager.SessionState.Offline ? "" : "Connecting...");
                return;
            }

            var runner = nm.runner;

            int playerCount = 0;
            foreach (var p in runner.ActivePlayers) playerCount++;

            // Production gate from the scene, or 1 under VRZ_SOLO_TEST (Core/DevFlags.cs).
            int required = DevFlags.MinPlayers(requiredPlayers);
            if (playerCount < required)
            {
                CancelCountdown();
                SetText("WAITING FOR PLAYER...\n(" + playerCount + "/" + required + ")");
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
                CancelCountdown();
                SetText("COMPLETE THE TUTORIAL!\n(" + tutorialDone + "/" + playerCount + " ready)");
                return;
            }

            if (_countdown < 0f)
                _countdown = startCountdown;

            _countdown -= Time.deltaTime;

            // Fade every client to black over the last second so the arena's synchronous scene
            // integration (unload lobby + GC, ~0.5 s in editor) happens behind a black screen.
            if (_countdown <= FadeOutSeconds && !_fadeStarted)
            {
                _fadeStarted = true;
                VRZ.World.ScreenFader.FadeOut(FadeOutSeconds);
            }

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

        /// A player left or un-readied mid-countdown: reset the timer and lift the fade if it started.
        private void CancelCountdown()
        {
            _countdown = -1f;
            if (_fadeStarted)
            {
                _fadeStarted = false;
                VRZ.World.ScreenFader.FadeIn(0.3f);
            }
        }

        private void SetText(string msg)
        {
            if (statusText != null && statusText.text != msg)
                statusText.text = msg;
        }
    }
}
