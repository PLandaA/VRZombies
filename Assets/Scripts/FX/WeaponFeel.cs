using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Autohand;
using MoreMountains.Feedbacks;

[RequireComponent(typeof(AutoGun))]
/// Feel-powered gunplay juice: muzzle light flash + Feel scale-punch on the slide + haptics
/// on every shot, and a hitmarker tick (audio + micro-haptic) when YOUR bullet lands.
/// Local shots hook AutoGun.OnShoot directly; remote shots arrive via NetworkAutoGun.
/// Drop a hand-authored MMF_Player in the override slot to replace the default punch.
public class WeaponFeel : MonoBehaviour
{
    [Header("Feel Override (optional, author in the Feel editor)")]
    [SerializeField] private MMF_Player shotPlayerOverride;

    [Header("Shot Punch")]
    [Tooltip("Transform punched on every shot (the slide). Auto-safe: no punch if empty.")]
    [SerializeField] private Transform punchTarget;
    [SerializeField] private float punchScale = 1.12f;
    [SerializeField] private float punchDuration = 0.09f;

    [Header("Muzzle Flash Light")]
    [SerializeField] private Color flashColor = new Color(1f, 0.72f, 0.35f);
    [SerializeField] private float flashIntensity = 5f;
    [SerializeField] private float flashTime = 0.05f;
    [SerializeField] private float flashRange = 5f;

    [Header("Haptics")]
    [Tooltip("Rumble on the holding hand(s) per shot: duration / amplitude")]
    [SerializeField] private Vector2 shotHaptic = new Vector2(0.08f, 0.7f);
    [Tooltip("Micro-rumble on hit confirm: duration / amplitude")]
    [SerializeField] private Vector2 hitmarkerHaptic = new Vector2(0.03f, 0.4f);

    [Header("Hitmarker")]
    [SerializeField] private AudioClip hitmarkerClip;
    [SerializeField] private float hitmarkerVolume = 0.55f;
    [SerializeField] private Vector2 hitmarkerPitchRange = new Vector2(1.25f, 1.45f);

    private AutoGun _gun;
    private Grabbable _grabbable;
    private MMF_Player _shotPlayer;
    private Light _muzzleLight;
    private AudioSource _markerAudio;
    private Coroutine _flashRoutine;

    private void Awake()
    {
        _gun = GetComponent<AutoGun>();
        _grabbable = GetComponent<Grabbable>();

        // Self-wire: find the slide if not assigned (any child whose name starts with "Slide")
        if (punchTarget == null || punchTarget == transform.root)
        {
            foreach (var t in transform.root.GetComponentsInChildren<Transform>(true))
                if (t.name.Trim().StartsWith("Slide")) { punchTarget = t; break; }
        }

        if (_gun.shootForward != null)
        {
            var go = new GameObject("MuzzleFlashLight");
            go.transform.SetParent(_gun.shootForward, false);
            _muzzleLight = go.AddComponent<Light>();
            _muzzleLight.type = LightType.Point;
            _muzzleLight.color = flashColor;
            _muzzleLight.range = flashRange;
            _muzzleLight.intensity = 0f;
            _muzzleLight.shadows = LightShadows.None;
        }

        _markerAudio = gameObject.AddComponent<AudioSource>();
        _markerAudio.playOnAwake = false;
        _markerAudio.spatialBlend = 1f;
        _markerAudio.maxDistance = 8f;
    }

    private void Start()
    {
        if (shotPlayerOverride == null && punchTarget != null)
        {
            // MMF_Player is [DisallowMultipleComponent] — host it on its own child GameObject.
            var host = new GameObject("Feel_ShotPunch");
            host.transform.SetParent(transform, false);
            _shotPlayer = host.AddComponent<MMF_Player>();
            if (_shotPlayer.FeedbacksList == null)
                _shotPlayer.FeedbacksList = new List<MMF_Feedback>();   // runtime-added players start with a null list
            var scale = new MMF_Scale();
            scale.AnimateScaleTarget = punchTarget;
            scale.RemapCurveZero = 1f;
            scale.RemapCurveOne = punchScale;
            scale.AnimateScaleDuration = punchDuration;
            scale.UniformScaling = true;
            scale.AllowAdditivePlays = false;
            scale.DetermineScaleOnPlay = true;
            _shotPlayer.AddFeedback(scale);
            _shotPlayer.Initialization();
        }
    }

    private void OnEnable()
    {
        if (_gun != null) _gun.OnShoot.AddListener(OnLocalShoot);
    }

    private void OnDisable()
    {
        if (_gun != null) _gun.OnShoot.RemoveListener(OnLocalShoot);
    }

    private void OnLocalShoot(AutoGun gun)
    {
        PlayShotVisuals();
        RumbleHoldingHands(shotHaptic.x, shotHaptic.y);
    }

    /// Visual-only layer, also called by NetworkAutoGun for the remote player's shots.
    public void PlayShotVisuals()
    {
        var player = shotPlayerOverride != null ? shotPlayerOverride : _shotPlayer;
        if (player != null) player.PlayFeedbacks();

        if (_muzzleLight != null)
        {
            if (_flashRoutine != null) StopCoroutine(_flashRoutine);
            _flashRoutine = StartCoroutine(FlashRoutine());
        }
    }

    /// Called by NetworkAutoGun when the local shooter confirms a zombie hit.
    /// Headshots get a sharper, higher-pitched tick and a stronger rumble.
    public void PlayHitmarker(bool headshot = false)
    {
        if (hitmarkerClip != null && _markerAudio != null)
        {
            _markerAudio.pitch = headshot
                ? Random.Range(1.65f, 1.85f)
                : Random.Range(hitmarkerPitchRange.x, hitmarkerPitchRange.y);
            _markerAudio.PlayOneShot(hitmarkerClip, headshot ? hitmarkerVolume * 1.25f : hitmarkerVolume);
        }
        RumbleHoldingHands(
            headshot ? hitmarkerHaptic.x * 1.8f : hitmarkerHaptic.x,
            headshot ? Mathf.Min(1f, hitmarkerHaptic.y * 1.6f) : hitmarkerHaptic.y);
    }

    private void RumbleHoldingHands(float duration, float amplitude)
    {
        if (_grabbable == null) return;
        foreach (var hand in _grabbable.GetHeldBy())
            if (hand != null) hand.PlayHapticVibration(duration, amplitude);
    }

    private IEnumerator FlashRoutine()
    {
        _muzzleLight.intensity = flashIntensity;
        float t = 0f;
        while (t < flashTime)
        {
            t += Time.deltaTime;
            _muzzleLight.intensity = Mathf.Lerp(flashIntensity, 0f, t / flashTime);
            yield return null;
        }
        _muzzleLight.intensity = 0f;
        _flashRoutine = null;
    }
}
