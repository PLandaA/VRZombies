using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// Minimal VR HUD that lazily follows the player's gaze slightly below eye line, showing the current wave and health without cluttering the view.
public class PlayerHUD : MonoBehaviour
{
    [SerializeField] private TMP_Text roundText;
    [SerializeField] private TMP_Text healthText;
    [SerializeField] private Image healthFill;

    [Tooltip("Distance in front of the head")]
    [SerializeField] private float distance = 1.2f;

    [Tooltip("Vertical offset below eye line (negative = lower)")]
    [SerializeField] private float heightOffset = -0.45f;

    [Tooltip("Lazy follow speed (lower = smoother/lazier)")]
    [SerializeField] private float followSpeed = 4f;

    private Transform _head;
    private NetworkPlayer _player;
    private ZombieSpawner _waves;

    private void LateUpdate()
    {
        if (_head == null)
        {
            var ahp = FindFirstObjectByType<Autohand.AutoHandPlayer>();
            if (ahp != null && ahp.headCamera != null) _head = ahp.headCamera.transform;
            if (_head == null) return;
        }

        Vector3 fwd = _head.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.01f) fwd = transform.forward;
        fwd.Normalize();

        Vector3 target = _head.position + fwd * distance + Vector3.up * heightOffset;
        transform.position = Vector3.Lerp(transform.position, target, Time.deltaTime * followSpeed);

        Vector3 look = transform.position - _head.position;
        if (look.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(look), Time.deltaTime * followSpeed);

        UpdateTexts();
    }

    private void UpdateTexts()
    {
        if (_waves == null) _waves = FindFirstObjectByType<ZombieSpawner>();
        if (_player == null)
        {
            foreach (var p in FindObjectsByType<NetworkPlayer>(FindObjectsSortMode.None))
                if (p.Object != null && p.Object.IsValid && p.Object.HasStateAuthority) { _player = p; break; }
        }

        if (roundText != null)
        {
            if (_waves == null || _waves.Object == null || !_waves.Object.IsValid)
                roundText.text = "";
            else if (_waves.IsIntermission)
                roundText.text = "GET READY";
            else if (_waves.CurrentWave <= 0)
                roundText.text = "GRAB A WEAPON";
            else
                roundText.text = "ROUND " + _waves.CurrentWave;
        }

        if (_player != null && _player.Object != null && _player.Object.IsValid)
        {
            float hp = _player.Health;
            float max = Mathf.Max(1, _player.MaxHealth);
            if (healthText != null) healthText.text = Mathf.CeilToInt(hp).ToString();
            if (healthFill != null)
            {
                healthFill.fillAmount = Mathf.Clamp01(hp / max);
                healthFill.color = Color.Lerp(Color.red, new Color(0.2f, 0.9f, 0.3f), hp / max);
            }
        }
    }
}
