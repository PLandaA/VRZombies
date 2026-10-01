using System.Diagnostics;
using System.IO;
using Autohand;
using UnityEditor;
using UnityEngine;
using VRZ.Core;
using VRZ.Network;
using Debug = UnityEngine.Debug;

namespace VRZ.EditorTools
{
    /// The editor as a stand-in partner for one-headset tests (2026-09-29). Without a headset the
    /// editor's camera sits at the floor and AutoHand ignores locomotion, but for the netcode it is
    /// a full player: it counts in the lobby, has an avatar, gets attacked, and its leaving shows
    /// "PARTNER LEFT" on the headset. Menu VRZ > Dev > Dummy Partner:
    ///   Host   - enter Play, create the room, mark the tutorial done, stand at 1.6 m.
    ///   Walk   - toggle a slow circle by moving AutoHand's physics body (the probe technique).
    ///   Turn   - yaw the head 90 degrees (the avatar's body yaw follows the head).
    ///   Die    - take 999 damage, so the headset can test the spectator from the other side.
    ///   Leave  - exit Play: the headset sees "PARTNER LEFT".
    ///   Pull Quest log - dump the headset's logcat over USB (adb) and print the VRZ lines.
    /// Editor-only: never ships.
    public static class DummyPartner
    {
        private const string Menu = "VRZ/Dev/Dummy Partner/";
        private const string HostFlag = "VRZ.DummyPartner.Host";
        private const float StandingHeight = 1.6f;

        private static bool _hosting, _created, _configured, _walking;
        private static float _walkAngle;
        private static Vector3 _walkCenter;

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += Tick;
        }

        [MenuItem(Menu + "Host (enter Play, create room)")]
        private static void Host()
        {
            if (EditorApplication.isPlaying) { Debug.LogWarning("[DummyPartner] Already in Play Mode."); return; }
            SessionState.SetBool(HostFlag, true);
            EditorApplication.EnterPlaymode();
        }

        private static bool InPlay(string action)
        {
            if (EditorApplication.isPlaying) return true;
            Debug.LogWarning("[DummyPartner] " + action + " only works in Play Mode (the match may have ended).");
            return false;
        }

        [MenuItem(Menu + "Walk (toggle)")]
        private static void Walk()
        {
            if (!InPlay("Walk")) return;
            var ahp = Object.FindFirstObjectByType<AutoHandPlayer>();
            if (ahp == null || ahp.body == null) { Debug.LogWarning("[DummyPartner] No AutoHandPlayer in play."); return; }
            _walking = !_walking;
            if (_walking) { _walkCenter = ahp.body.position; _walkAngle = 0f; }
            Debug.Log("[DummyPartner] Walk " + (_walking ? "ON" : "OFF"));
        }

        [MenuItem(Menu + "Turn 90")]
        private static void Turn()
        {
            if (!InPlay("Turn")) return;
            var ahp = Object.FindFirstObjectByType<AutoHandPlayer>();
            if (ahp == null || ahp.headCamera == null) return;
            ahp.headCamera.transform.Rotate(0f, 90f, 0f, Space.World);
            Debug.Log("[DummyPartner] Turned 90 degrees: head yaw " + ahp.headCamera.transform.eulerAngles.y.ToString("F0"));
        }

        [MenuItem(Menu + "Die (999 damage)")]
        private static void Die()
        {
            if (!InPlay("Die")) return;
            var me = NetworkSession.Current?.GetPlayer();
            if (me == null || !me.IsValid) { Debug.LogWarning("[DummyPartner] No local player."); return; }
            me.ApplyDamage(new DamageInfo(999, me.Position, me.Position + Vector3.forward, false));
            Debug.Log("[DummyPartner] Took 999 damage.");
        }

        [MenuItem(Menu + "Leave (exit Play)")]
        private static void Leave()
        {
            EditorApplication.isPlaying = false;
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode)
            {
                _hosting = SessionState.GetBool(HostFlag, false);
                SessionState.SetBool(HostFlag, false);
                _created = _configured = _walking = false;
                if (_hosting) Debug.Log("[DummyPartner] Hosting: waiting for the lobby directory...");
            }
            else if (change == PlayModeStateChange.ExitingPlayMode)
            {
                _hosting = _walking = false;
            }
        }

        private static void Tick()
        {
            if (!EditorApplication.isPlaying) return;
            var ahp = Object.FindFirstObjectByType<AutoHandPlayer>();

            // Stand up: without a headset the camera is at the floor, and the avatar (head-driven)
            // would lie flat. Keep the camera at a standing height every frame.
            if (ahp != null && ahp.headCamera != null)
            {
                var cam = ahp.headCamera.transform;
                if (Mathf.Abs(cam.localPosition.y - StandingHeight) > 0.01f)
                    cam.localPosition = new Vector3(cam.localPosition.x, StandingHeight, cam.localPosition.z);
            }

            if (_hosting && !_created)
            {
                var nm = NetworkManager.instance;
                if (nm != null && nm.State == NetworkManager.SessionState.BrowsingLobby)
                {
                    _created = true;
                    nm.CreateSession();
                    Debug.Log("[DummyPartner] Room requested. Join it from the headset.");
                }
            }

            if (_hosting && _created && !_configured)
            {
                var me = NetworkSession.Current?.GetPlayer();
                if (me != null && me.IsValid)
                {
                    me.TutorialDone = true;
                    me.Ready = true;
                    _configured = true;
                    Debug.Log("[DummyPartner] Tutorial done + Ready. Code: " + NetworkManager.instance.CurrentCode);
                }
            }

            if (_walking && ahp != null && ahp.body != null)
            {
                _walkAngle += Time.deltaTime * 0.5f;   // one lap every ~12 s
                var pos = _walkCenter + new Vector3(Mathf.Cos(_walkAngle), 0f, Mathf.Sin(_walkAngle)) * 2f;
                ahp.body.position = pos;
            }
        }

        // ── Quest log over USB ────────────────────────────────────────────────────────────

        [MenuItem(Menu + "Pull Quest log (adb)")]
        private static void PullQuestLog()
        {
            string adb = FindAdb();
            if (adb == null) { Debug.LogError("[DummyPartner] adb not found. Set VRZ.AdbPath in EditorPrefs or install Meta Quest Developer Hub / Android platform tools."); return; }
            string output = Run(adb, "logcat -d -v time -s Unity");
            if (output == null) return;
            var keep = new System.Text.StringBuilder();
            int lines = 0;
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("[NetGun]") || line.Contains("[DevFlags]") || line.Contains("[NetworkManager]") || line.Contains("[DeathSpectator]") || line.Contains("[PlayerBelt]") || line.Contains("[FPS]") || line.Contains("Exception") || line.Contains("Error"))
                { keep.AppendLine(line.TrimEnd()); lines++; }
            }
            string file = Path.Combine(Application.dataPath, "../Library/quest-logcat.txt");
            File.WriteAllText(file, output);
            Debug.Log("[DummyPartner] Quest log: " + lines + " relevant line(s) (full dump in Library/quest-logcat.txt)\n" + keep);
        }

        [MenuItem(Menu + "Quest FPS (VrApi)")]
        private static void QuestFps()
        {
            // The Quest runtime logs one "FPS=rendered/target" line per second under the VrApi tag.
            string adb = FindAdb();
            if (adb == null) { Debug.LogError("[DummyPartner] adb not found."); return; }
            string output = Run(adb, "logcat -d -s VrApi");
            if (output == null) return;
            var rx = new System.Text.RegularExpressions.Regex(@"FPS=(\d+)/(\d+)");
            int samples = 0, min = int.MaxValue, target = 0; long sum = 0; int dropped = 0;
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(output))
            {
                int fps = int.Parse(m.Groups[1].Value); target = int.Parse(m.Groups[2].Value);
                samples++; sum += fps; if (fps < min) min = fps; if (fps < target - 1) dropped++;
            }
            if (samples == 0) { Debug.LogWarning("[DummyPartner] No VrApi FPS lines in the log (is the app running on the headset?)."); return; }
            Debug.Log("[DummyPartner] Quest FPS over " + samples + " s: min=" + min + " avg=" + (sum / (float)samples).ToString("F1") + " target=" + target + " | seconds below target: " + dropped + " (" + (100f * dropped / samples).ToString("F0") + "%)");
        }

        [MenuItem(Menu + "Clear Quest log (adb)")]
        private static void ClearQuestLog()
        {
            string adb = FindAdb();
            if (adb != null && Run(adb, "logcat -c") != null) Debug.Log("[DummyPartner] Quest log cleared.");
        }

        private static string FindAdb()
        {
            string custom = EditorPrefs.GetString("VRZ.AdbPath", "");
            if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;
            string local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            string[] candidates =
            {
                Path.Combine(local, "Android/Sdk/platform-tools/adb.exe"),
                Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"),
                "adb"
            };
            foreach (var c in candidates) if (c == "adb" || File.Exists(c)) return c;
            return null;
        }

        /// Runs adb with a hard timeout. adb blocks forever when the headset is asleep or unplugged
        /// (it waits for a device), and that froze the editor's main thread once (2026-09-30);
        /// now the process is killed after `timeoutMs` and the caller gets null.
        private static string Run(string exe, string args, int timeoutMs = 15000)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                var output = new System.Text.StringBuilder(); var err = new System.Text.StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    Debug.LogError("[DummyPartner] adb did not answer in " + timeoutMs / 1000 + " s (headset asleep or unplugged?). Killed.");
                    return null;
                }
                p.WaitForExit();   // flush the async readers
                if (err.Length > 0 && output.Length == 0) { Debug.LogError("[DummyPartner] adb: " + err.ToString().Trim()); return null; }
                return output.ToString();
            }
            catch (System.Exception e) { Debug.LogError("[DummyPartner] adb failed: " + e.Message); return null; }
        }
    }
}
