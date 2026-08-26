using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Fusion;

/// Watches the networked GameOver flag; on defeat it fades the local view to black,
/// shows GAME OVER while zombies retreat, then shuts down the session and returns to the lobby.
public class GameOverController : MonoBehaviour
{
    private const float FadeSeconds = 3f;
    private const float HoldSeconds = 12f;

    private bool _running;
    private ZombieSpawner _spawner;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("GameOverController");
        DontDestroyOnLoad(go);
        go.AddComponent<GameOverController>();
    }

    private void Update()
    {
        if (_running) return;
        if (_spawner == null || _spawner.Object == null || !_spawner.Object.IsValid)
        {
            _spawner = FindFirstObjectByType<ZombieSpawner>();
            return;
        }
        if (_spawner.GameOver)
        {
            _running = true;
            StartCoroutine(Sequence());
        }
    }

    private IEnumerator Sequence()
    {
        var cam = Camera.main;
        if (cam == null) yield break;

        var canvasGo = new GameObject("GameOverCanvas");
        canvasGo.transform.SetParent(cam.transform, false);
        canvasGo.transform.localPosition = new Vector3(0f, 0f, 0.5f);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(2000, 2000);
        canvasGo.transform.localScale = Vector3.one * 0.001f;

        var fadeGo = new GameObject("Fade");
        fadeGo.transform.SetParent(canvasGo.transform, false);
        var fade = fadeGo.AddComponent<Image>();
        fade.color = new Color(0f, 0f, 0f, 0f);
        var frt = fadeGo.GetComponent<RectTransform>();
        frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
        frt.offsetMin = Vector2.zero; frt.offsetMax = Vector2.zero;

        var txtGo = new GameObject("GameOverText");
        txtGo.transform.SetParent(canvasGo.transform, false);
        var txt = txtGo.AddComponent<TextMeshProUGUI>();
        txt.text = "GAME OVER";
        txt.fontSize = 120;
        txt.alignment = TextAlignmentOptions.Center;
        txt.fontStyle = FontStyles.Bold;
        txt.color = new Color(0.75f, 0.1f, 0.1f, 0f);
        txtGo.GetComponent<RectTransform>().sizeDelta = new Vector2(1600, 300);

        // Run recap: the local player's stats under the title
        var statsGo = new GameObject("StatsText");
        statsGo.transform.SetParent(canvasGo.transform, false);
        statsGo.transform.localPosition = new Vector3(0f, -220f, 0f);
        var stats = statsGo.AddComponent<TextMeshProUGUI>();
        var np = NetworkManager.instance != null ? NetworkManager.instance.GetPlayer() : null;
        stats.text = np != null
            ? "SCORE " + np.TotalScore + "   \u00b7   KILLS " + np.Kills + "   \u00b7   HEADSHOTS " + np.HeadshotKills
            : "";
        stats.fontSize = 44;
        stats.alignment = TextAlignmentOptions.Center;
        stats.color = new Color(0.85f, 0.8f, 0.7f, 0f);
        statsGo.GetComponent<RectTransform>().sizeDelta = new Vector2(1600, 120);

        float t = 0f;
        while (t < FadeSeconds)
        {
            t += Time.deltaTime;
            float a = Mathf.Clamp01(t / FadeSeconds);
            fade.color = new Color(0f, 0f, 0f, a * 0.92f);
            txt.color = new Color(0.75f, 0.1f, 0.1f, a);
            stats.color = new Color(0.85f, 0.8f, 0.7f, a);
            yield return null;
        }

        yield return new WaitForSeconds(HoldSeconds - FadeSeconds);

        var runner = FindFirstObjectByType<NetworkRunner>();
        if (runner != null) yield return runner.Shutdown();

        Destroy(canvasGo);
        _running = false;
        _spawner = null;
        UnityEngine.SceneManagement.SceneManager.LoadScene(0);
    }
}
