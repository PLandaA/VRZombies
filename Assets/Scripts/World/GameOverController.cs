using System.Collections;
using UnityEngine;
using VRZ.Core;
using UnityEngine.UI;
using TMPro;
using Fusion;
using VRZ.Enemies;

namespace VRZ.World
{

    /// Watches the networked GameOver flag; on defeat it fades the local view to black,
    /// shows GAME OVER while zombies retreat, then shuts down the session and returns to the lobby.
    public class GameOverController : MonoBehaviour
    {
        private const float FadeSeconds = 3f;
        private const float HoldSeconds = 12f;

        public static GameOverController Instance { get; private set; }

        private bool _running;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("GameOverController");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<GameOverController>();
        }

        // No Update at all: the spawner raises a static event from
        // its OnChangedRender the frame GameOver turns true, on every client. The controller is
        // DDOL and the event is static, so one subscription in OnEnable covers every scene and
        // every spawner instance without a single lookup.
        private void OnEnable()  { ZombieSpawner.GameOverRaised += OnGameOver; }
        private void OnDisable() { ZombieSpawner.GameOverRaised -= OnGameOver; }

        private void OnGameOver(ZombieSpawner spawner)
        {
            if (_running) return;
            _running = true;
            StartCoroutine(Sequence("GAME OVER", BuildTeamRecap(), HoldSeconds, new Color(0.75f, 0.1f, 0.1f)));
        }

        /// End the match from outside the GameOver flag (the master client left
        /// and took the wave spawner and every zombie with it). Same fade + shutdown + return to
        /// lobby, with a custom title and message and a shorter hold.
        public void EndMatch(string title, string message, float holdSeconds)
        {
            if (_running) return;
            _running = true;
            StartCoroutine(Sequence(title, message, holdSeconds, new Color(0.9f, 0.6f, 0.15f)));
        }

        /// Every NetworkPlayer's score/kills already travel by snapshot; nobody
        /// was reading the partner's. One line per player (YOU first) plus the team total, so the
        /// end screen finally shows the co-op result instead of a solo recap.
        private static string BuildTeamRecap()
        {
            var session = NetworkSession.Current;
            if (session == null) return "";

            var me = session.GetPlayer();
            var sb = new System.Text.StringBuilder();
            int teamScore = 0, teamKills = 0;

            // Local player first, then the rest in registry order
            void Line(string label, IPlayerState p)
            {
                sb.Append(label).Append("   SCORE ").Append(p.TotalScore)
                  .Append("   \u00b7   KILLS ").Append(p.Kills)
                  .Append("   \u00b7   HEADSHOTS ").Append(p.HeadshotKills).Append('\n');
                teamScore += p.TotalScore; teamKills += p.Kills;
            }

            if (me != null && me.IsValid) Line("YOU", me);
            int partnerIndex = 0;
            foreach (var p in session.Players)
            {
                if (p == null || !p.IsValid || p == me) continue;
                partnerIndex++;
                Line(partnerIndex == 1 && session.Players.Count <= 2 ? "PARTNER" : "PARTNER " + partnerIndex, p);
            }

            if (partnerIndex > 0)
                sb.Append("TEAM   SCORE ").Append(teamScore).Append("   \u00b7   KILLS ").Append(teamKills);

            return sb.ToString().TrimEnd('\n');
        }

        private IEnumerator Sequence(string title, string message, float holdSeconds, Color titleColor)
        {
            var cam = Camera.main;
            if (cam == null) yield break;

            // VR readability (2026-09-20): 1.2 m in front of the eyes (0.5 m was inside the comfortable
            // focus range and the text filled the whole view). 2000 units * 0.0016 = 3.2 m wide so the
            // black fade still covers the headset FOV at that distance; text sizes below are chosen
            // for ~5 deg (title) / ~2.5 deg (lines) of vertical angle, which reads cleanly on Quest.
            var canvasGo = new GameObject("GameOverCanvas");
            canvasGo.transform.SetParent(cam.transform, false);
            canvasGo.transform.localPosition = new Vector3(0f, 0f, 1.2f);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(2000, 2000);
            canvasGo.transform.localScale = Vector3.one * 0.0016f;

            var fadeGo = new GameObject("Fade");
            fadeGo.transform.SetParent(canvasGo.transform, false);
            var fade = fadeGo.AddComponent<Image>();
            fade.color = new Color(0f, 0f, 0f, 0f);
            var frt = fadeGo.GetComponent<RectTransform>();
            frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;

            var txtGo = new GameObject("GameOverText");
            txtGo.transform.SetParent(canvasGo.transform, false);
            txtGo.transform.localPosition = new Vector3(0f, 90f, 0f);
            var txt = txtGo.AddComponent<TextMeshProUGUI>();
            txt.text = title;
            txt.fontSize = 64;
            txt.alignment = TextAlignmentOptions.Center;
            txt.fontStyle = FontStyles.Bold;
            txt.color = new Color(titleColor.r, titleColor.g, titleColor.b, 0f);
            txtGo.GetComponent<RectTransform>().sizeDelta = new Vector2(1400, 120);

            // Second line: run recap on defeat, or the reason the match ended
            var statsGo = new GameObject("StatsText");
            statsGo.transform.SetParent(canvasGo.transform, false);
            statsGo.transform.localPosition = new Vector3(0f, -60f, 0f);
            var stats = statsGo.AddComponent<TextMeshProUGUI>();
            stats.text = message;
            stats.fontSize = 30;
            stats.lineSpacing = 12f;
            stats.alignment = TextAlignmentOptions.Center;
            stats.color = new Color(0.85f, 0.8f, 0.7f, 0f);
            // Tall enough for YOU / PARTNER / TEAM (three lines) without clipping
            statsGo.GetComponent<RectTransform>().sizeDelta = new Vector2(1400, 200);

            float t = 0f;
            while (t < FadeSeconds)
            {
                t += Time.deltaTime;
                float a = Mathf.Clamp01(t / FadeSeconds);
                fade.color = new Color(0f, 0f, 0f, a * 0.92f);
                txt.color = new Color(titleColor.r, titleColor.g, titleColor.b, a);
                stats.color = new Color(0.85f, 0.8f, 0.7f, a);
                yield return null;
            }

            yield return new WaitForSeconds(Mathf.Max(0f, holdSeconds - FadeSeconds));

            var runner = FindFirstObjectByType<NetworkRunner>();
            if (runner != null && runner.IsRunning)   // already dead after a lost connection: skip
            {
                // Shutdown() returns a Task. A coroutine does NOT await a Task: "yield return task"
                // just skips one frame, so LoadScene(0) could run with the runner half-dead and the
                // lobby's NetworkManager would find a stale singleton. Poll until it really finishes.
                var shutdown = runner.Shutdown();
                while (!shutdown.IsCompleted) yield return null;
            }

            Destroy(canvasGo);
            _running = false;
            UnityEngine.SceneManagement.SceneManager.LoadScene(0);
        }
    }
}
