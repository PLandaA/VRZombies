using UnityEngine;

/// Central kill accounting for the LOCAL player: credits networked score/kill stats and
/// tracks multi-kill combos (kills within a rolling window spawn escalating combo banners).
public static class ScoreEvents
{
    private const float ComboWindow = 2.2f;
    private static float _lastKillTime = -99f;
    private static int _comboCount;

    public static void RegisterKill(bool headshot, Vector3 worldPos)
    {
        // Credit the local player's networked stats (we own our player object)
        var nm = NetworkManager.instance;
        var np = nm != null ? nm.GetPlayer() : null;
        if (np != null && np.Object != null && np.Object.IsValid)
        {
            np.TotalScore += headshot ? 25 : 10;
            np.Kills++;
            if (headshot) np.HeadshotKills++;
        }

        // Combo tracking
        if (Time.time - _lastKillTime <= ComboWindow) _comboCount++;
        else _comboCount = 1;
        _lastKillTime = Time.time;

        if (_comboCount >= 2)
        {
            string label = _comboCount == 2 ? "DOUBLE KILL!"
                         : _comboCount == 3 ? "TRIPLE KILL!"
                         : "MEGA KILL x" + _comboCount;
            float size = Mathf.Min(3.2f + _comboCount * 0.35f, 5f);
            PopupText.Spawn(worldPos + Vector3.up * 2.3f, label,
                new Color(1f, 0.45f, 0.1f), size, 1.5f, 1.2f);
        }
    }
}
