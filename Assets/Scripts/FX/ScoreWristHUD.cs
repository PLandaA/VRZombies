using System.Collections;
using UnityEngine;
using TMPro;
using Autohand;

/// Floating gameplay HUD with tutorial-sign-style LAZY FOLLOW: a small panel that drifts
/// smoothly to sit low in the player's view. Health BAR + score + round.
public class ScoreWristHUD : MonoBehaviour
{
    [SerializeField] private ZombieSpawner spawner;

    [Header("Lazy follow")]
    [SerializeField] private float distance = 1.15f;
    [SerializeField] private float heightOffset = -0.42f;
    [SerializeField] private float followSpeed = 3.5f;

    private Transform _panel;
    private Transform _barFill;
    private Renderer _barFillRend;
    private TextMeshPro _text;
    private int _round;
    private float _nextTextRefresh;

    private void Start()
    {
        if (spawner == null) spawner = FindFirstObjectByType<ZombieSpawner>();
        if (spawner != null) spawner.OnWaveStarted.AddListener(w => _round = w);
        BuildPanel();
    }

    private void BuildPanel()
    {
        _panel = new GameObject("GameHUD").transform;
        _panel.SetParent(transform, false);

        var mat = new Material(Shader.Find("Sprites/Default"));

        Transform MakeQuad(string name, Vector3 pos, Vector3 scale, Color color, out Renderer rend)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad).transform;
            Destroy(q.GetComponent<Collider>());
            q.name = name;
            q.SetParent(_panel, false);
            q.localPosition = pos;
            q.localScale = scale;
            rend = q.GetComponent<Renderer>();
            rend.sharedMaterial = new Material(mat) { color = color };
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return q;
        }

        MakeQuad("BarBG", Vector3.zero, new Vector3(0.26f, 0.032f, 1f), new Color(0.08f, 0.08f, 0.08f, 0.75f), out _);
        _barFill = MakeQuad("BarFill", new Vector3(0f, 0f, -0.001f), new Vector3(0.25f, 0.024f, 1f), new Color(0.75f, 0.15f, 0.12f, 0.95f), out _barFillRend);

        var tGo = new GameObject("HUDText");
        tGo.transform.SetParent(_panel, false);
        tGo.transform.localPosition = new Vector3(0f, -0.045f, 0f);
        _text = tGo.AddComponent<TextMeshPro>();
        _text.fontSize = 0.5f;
        _text.alignment = TextAlignmentOptions.Center;
        _text.color = new Color(0.95f, 0.92f, 0.75f, 0.9f);
        _text.rectTransform.sizeDelta = new Vector2(0.6f, 0.1f);
        _text.text = "";
    }

    private void LateUpdate()
    {
        var cam = Camera.main;
        if (cam == null || _panel == null) return;

        // Lazy follow: drift toward a spot low in front of the eyes (tutorial-sign feel)
        Vector3 target = cam.transform.position + cam.transform.forward * distance + Vector3.up * heightOffset;
        _panel.position = Vector3.Lerp(_panel.position, target, Time.deltaTime * followSpeed);
        _panel.rotation = Quaternion.Slerp(_panel.rotation,
            Quaternion.LookRotation(_panel.position - cam.transform.position), Time.deltaTime * followSpeed);

        if (Time.time >= _nextTextRefresh)
        {
            _nextTextRefresh = Time.time + 0.25f;
            var nm = NetworkManager.instance;
            var np = nm != null ? nm.GetPlayer() : null;
            if (np != null && np.Object != null && np.Object.IsValid)
            {
                float pct = Mathf.Clamp01(np.Health / 100f);
                var s = _barFill.localScale; s.x = 0.25f * pct; _barFill.localScale = s;
                var p = _barFill.localPosition; p.x = -0.125f * (1f - pct); _barFill.localPosition = p;
                if (_barFillRend != null)
                    _barFillRend.sharedMaterial.color = Color.Lerp(
                        new Color(0.75f, 0.12f, 0.1f, 0.95f), new Color(0.25f, 0.7f, 0.25f, 0.95f), pct);

                _text.text = np.TotalScore + " PTS" + (_round > 0 ? "    R" + _round : "");
            }
        }
    }
}
