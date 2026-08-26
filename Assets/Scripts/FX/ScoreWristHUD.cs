using System.Collections;
using UnityEngine;
using TMPro;
using Autohand;

/// Minimal wrist HUD on the local player's LEFT hand: current score and round number.
/// Attaches itself once the AutoHand player exists; updates a few times per second.
public class ScoreWristHUD : MonoBehaviour
{
    [SerializeField] private ZombieSpawner spawner;
    [SerializeField] private Vector3 wristOffset = new Vector3(0f, 0.06f, -0.05f);

    private TextMeshPro _tmp;
    private int _round;

    private void Start()
    {
        if (spawner == null) spawner = FindFirstObjectByType<ZombieSpawner>();
        if (spawner != null) spawner.OnWaveStarted.AddListener(w => _round = w);
        StartCoroutine(Attach());
    }

    private IEnumerator Attach()
    {
        AutoHandPlayer player = null;
        while (player == null)
        {
            player = FindFirstObjectByType<AutoHandPlayer>();
            yield return new WaitForSeconds(0.5f);
        }
        var hand = player.handLeft != null ? player.handLeft.transform : player.transform;

        var go = new GameObject("WristHUD");
        go.transform.SetParent(hand, false);
        go.transform.localPosition = wristOffset;
        _tmp = go.AddComponent<TextMeshPro>();
        _tmp.fontSize = 0.55f;
        _tmp.alignment = TextAlignmentOptions.Center;
        _tmp.color = new Color(0.95f, 0.92f, 0.75f, 0.9f);
        _tmp.rectTransform.sizeDelta = new Vector2(0.5f, 0.2f);

        StartCoroutine(UpdateLoop());
    }

    private IEnumerator UpdateLoop()
    {
        var wait = new WaitForSeconds(0.25f);
        while (true)
        {
            var nm = NetworkManager.instance;
            var np = nm != null ? nm.GetPlayer() : null;
            if (_tmp != null && np != null && np.Object != null && np.Object.IsValid)
                _tmp.text = np.TotalScore + " PTS" + (_round > 0 ? "   R" + _round : "");
            yield return wait;
        }
    }

    private void LateUpdate()
    {
        // Billboard EVERY frame: the hand rotates constantly -- a 4hz snapshot leaves the
        // text sideways or mirrored most of the time
        var cam = Camera.main;
        if (_tmp != null && cam != null)
            _tmp.transform.rotation = Quaternion.LookRotation(_tmp.transform.position - cam.transform.position);
    }
}
