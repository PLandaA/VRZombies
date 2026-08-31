using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Autohand;

/// VR-safe damage feedback for the LOCAL player: red edge vignette (never a fullscreen blind,
/// never camera shake -- vestibular no-no in VR) + a hard haptic jolt on both hands.
/// Below the low-health threshold the vignette breathes as a persistent warning.
/// Finds the local NetworkPlayer at runtime and reacts to replicated health drops.
public class DamageVignette : MonoBehaviour
{
    [Header("Vignette")]
    [Tooltip("Peak alpha of the damage pulse (scaled by damage amount)")]
    [SerializeField] private float pulseMaxAlpha = 0.55f;
    [SerializeField] private float pulseFadeTime = 0.7f;
    [SerializeField] private Color vignetteColor = new Color(0.75f, 0.05f, 0.05f);

    [Header("Low Health Warning")]
    [Range(0f, 1f)]
    [SerializeField] private float lowHealthThreshold = 0.3f;
    [SerializeField] private float lowHealthAlpha = 0.28f;
    [SerializeField] private float heartbeatSpeed = 2.2f;

    [Header("Haptics")]
    [Tooltip("Duration / amplitude of the jolt on both hands when damaged")]
    [SerializeField] private Vector2 damageHaptic = new Vector2(0.25f, 0.9f);

    private Image _image;
    private float _pulse;
    private int _lastHealth = int.MinValue;
    private NetworkPlayer _localPlayer;
    private AutoHandPlayer _rig;

    [Header("Directional Hit Indicator")]
    [Tooltip("Peak alpha of the arc that points at whoever just hit you")]
    [SerializeField] private float dirMaxAlpha = 0.7f;
    [SerializeField] private float dirFadeTime = 1.1f;

    private Image _dirImage;
    private Transform _head;
    private Vector3 _attackerPos;
    private float _dirPulse;
    private bool _subscribed;

    private void Start()
    {
        StartCoroutine(BuildWhenReady());
    }

    private IEnumerator BuildWhenReady()
    {
        // Wait for the local rig's head camera
        while (_rig == null || _rig.headCamera == null)
        {
            _rig = FindFirstObjectByType<AutoHandPlayer>();
            yield return new WaitForSeconds(0.25f);
        }
        BuildCanvas(_rig.headCamera.transform);
    }

    private void BuildCanvas(Transform head)
    {
        var canvasGo = new GameObject("DamageVignetteCanvas");
        canvasGo.transform.SetParent(head, false);
        canvasGo.transform.localPosition = new Vector3(0f, 0f, 0.35f);

        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 500;

        var rt = canvas.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(1f, 1f);
        rt.localScale = Vector3.one * 1.4f;   // covers the FOV at 0.35m

        var imgGo = new GameObject("Vignette");
        imgGo.transform.SetParent(canvasGo.transform, false);
        _image = imgGo.AddComponent<Image>();
        var imgRt = _image.rectTransform;
        imgRt.anchorMin = Vector2.zero;
        imgRt.anchorMax = Vector2.one;
        imgRt.offsetMin = Vector2.zero;
        imgRt.offsetMax = Vector2.zero;
        _image.sprite = Sprite.Create(MakeVignetteTexture(256),
            new Rect(0, 0, 256, 256), new Vector2(0.5f, 0.5f));
        _image.color = new Color(vignetteColor.r, vignetteColor.g, vignetteColor.b, 0f);
        _image.raycastTarget = false;

        // Directional arc: same canvas, rotated each frame to point at the attacker
        _head = head;
        var dirGo = new GameObject("HitDirection");
        dirGo.transform.SetParent(canvasGo.transform, false);
        _dirImage = dirGo.AddComponent<Image>();
        var dirRt = _dirImage.rectTransform;
        dirRt.anchorMin = Vector2.zero;
        dirRt.anchorMax = Vector2.one;
        dirRt.offsetMin = Vector2.zero;
        dirRt.offsetMax = Vector2.zero;
        _dirImage.sprite = Sprite.Create(MakeDirectionTexture(256),
            new Rect(0, 0, 256, 256), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        _dirImage.color = new Color(1f, 0.25f, 0.15f, 0f);
        _dirImage.raycastTarget = false;
    }

    /// Arc concentrated on ONE edge (points "up" at rest): bright near the rim, fading toward
    /// the centre and toward the sides. Rotating the rect aims it at the attacker.
    /// The canvas plane is far wider than the headset FOV, so the bright band sits at ~0.2-0.5
    /// of the normalized radius -- pushed further out it renders outside what the eye can see.
    private Texture2D MakeDirectionTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half;
                float dy = (y - half) / half;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float radial = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, 0.45f, dist));
                float dot = dist > 0.001f ? dy / dist : 0f;             // 1 = straight up
                float angular = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.1f, 0.8f, dot));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(radial * angular * 255f));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    private void OnDamagedFrom(Vector3 attackerPos)
    {
        _attackerPos = attackerPos;
        _dirPulse = 1f;
    }

    private void OnDestroy()
    {
        if (_localPlayer != null && _subscribed) _localPlayer.OnDamagedFrom -= OnDamagedFrom;
    }

    /// Radial texture: transparent center, opaque edges (the vignette shape).
    private Texture2D MakeVignetteTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        float half = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half;
                float dy = (y - half) / half;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);          // 0 center -> ~1.41 corners
                float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1.15f, dist));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        return tex;
    }

    private void Update()
    {
        // Acquire the local networked player
        if (_localPlayer == null)
        {
            var nm = NetworkManager.instance;
            if (nm != null && nm.runner != null && nm.runner.IsRunning)
                _localPlayer = nm.GetPlayer(nm.runner.LocalPlayer);
            if (_localPlayer == null) return;
        }
        if (_localPlayer.Object == null || !_localPlayer.Object.IsValid || _image == null) return;

        if (!_subscribed)
        {
            _localPlayer.OnDamagedFrom += OnDamagedFrom;
            _subscribed = true;
        }

        UpdateDirectionArc();

        int h = _localPlayer.Health;
        if (_lastHealth == int.MinValue) _lastHealth = h;

        if (h < _lastHealth)
        {
            float dmg01 = Mathf.Clamp01((_lastHealth - h) / 40f);    // 40 dmg = full pulse
            _pulse = Mathf.Max(_pulse, Mathf.Lerp(0.35f, 1f, dmg01));
            Jolt();
        }
        _lastHealth = h;

        // Compose: damage pulse decays + low-health heartbeat floor
        _pulse = Mathf.Max(0f, _pulse - Time.deltaTime / Mathf.Max(0.05f, pulseFadeTime));
        float health01 = _localPlayer.MaxHealth > 0 ? (float)h / _localPlayer.MaxHealth : 1f;

        float baseAlpha = 0f;
        if (!_localPlayer.IsDead && health01 <= lowHealthThreshold && health01 > 0f)
        {
            float beat = (Mathf.Sin(Time.time * heartbeatSpeed * Mathf.PI * 2f) * 0.5f + 0.5f);
            baseAlpha = lowHealthAlpha * Mathf.Lerp(0.55f, 1f, beat);
        }

        float alpha = Mathf.Max(baseAlpha, _pulse * pulseMaxAlpha);
        var c = _image.color;
        c.a = alpha;
        _image.color = c;
    }

    /// Aims the arc at the last attacker, re-computed every frame so it stays correct while
    /// the player turns their head (an arc frozen at hit time would lie the moment you look).
    private void UpdateDirectionArc()
    {
        if (_dirImage == null) return;

        _dirPulse = Mathf.Max(0f, _dirPulse - Time.deltaTime / Mathf.Max(0.05f, dirFadeTime));
        if (_dirPulse <= 0f)
        {
            var off = _dirImage.color; off.a = 0f; _dirImage.color = off;
            return;
        }
        if (_head == null) return;

        Vector3 local = _head.InverseTransformPoint(_attackerPos);
        local.y = 0f;
        if (local.sqrMagnitude > 0.0001f)
        {
            float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;   // 0 = ahead, 180 = behind
            _dirImage.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -angle);
        }

        var c = _dirImage.color;
        c.a = _dirPulse * dirMaxAlpha;
        _dirImage.color = c;
    }

    private void Jolt()
    {
        if (_rig == null) return;
        if (_rig.handRight != null) _rig.handRight.PlayHapticVibration(damageHaptic.x, damageHaptic.y);
        if (_rig.handLeft != null) _rig.handLeft.PlayHapticVibration(damageHaptic.x, damageHaptic.y);
    }
}
