using UnityEngine;
using UnityEngine.XR;

namespace VRZ.Player
{
    /// Guards against a mis-calibrated headset floor (Guardian reset, eye-level origin...) that
    /// spawns the player 0.5-1 m too tall. Uses AutoHandPlayer.heightOffset, which the rig adds to
    /// the tracking container every frame, so nothing about tracking itself is touched and the
    /// avatar calibrator (which reads the camera against the real floor) follows automatically.
    ///
    /// - Learns the player's standing height on good sessions (raw tracked height in a plausible
    ///   range) and stores it in PlayerPrefs.
    /// - At spawn, if the raw height is implausible, offsets it to the stored height (1.70 m fallback).
    /// - Manual recalibration while standing: click BOTH thumbsticks (or H in the editor).
    public class PlayerHeightGuard : MonoBehaviour
    {
        private const string PrefKey = "vrz.standingHeight";
        private const float FallbackHeight = 1.70f;
        private const float MinPlausible = 1.00f;
        private const float MaxPlausible = 2.10f;
        private const float SettleSeconds = 1.5f;     // tracking needs a moment after spawn

        [SerializeField] private Autohand.AutoHandPlayer player;

        private float _spawnTime;
        private bool _checked;
        private bool _sticksWereDown;

        private void Awake()
        {
            if (player == null) player = GetComponentInParent<Autohand.AutoHandPlayer>() ?? GetComponent<Autohand.AutoHandPlayer>();
            _spawnTime = Time.time;
        }

        /// Raw tracked head height: camera relative to the tracking container, BEFORE our offset.
        private float RawHeadHeight =>
            player.trackingContainer.InverseTransformPoint(player.headCamera.transform.position).y;

        /// A real headset is connected and tracking (false in the editor with no HMD on).
        private static bool HmdPresent
        {
            get
            {
                var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                return head.isValid && head.TryGetFeatureValue(CommonUsages.userPresence, out bool present) && present;
            }
        }

        private void Update()
        {
            if (player == null || player.headCamera == null) return;
            if (!HmdPresent) return;    // editor without a headset: the rig camera height is not the player's

            if (!_checked && Time.time - _spawnTime > SettleSeconds)
            {
                _checked = true;
                AutoCheck();
            }

            if (BothSticksClicked()) Recalibrate("thumbsticks");
#if UNITY_EDITOR
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.hKey.wasPressedThisFrame) Recalibrate("H key");
#endif
        }

        private void AutoCheck()
        {
            float raw = RawHeadHeight;
            if (raw >= MinPlausible && raw <= MaxPlausible)
            {
                PlayerPrefs.SetFloat(PrefKey, raw);          // a good session: remember this height
                Debug.Log($"[Height] tracked {raw:F2}m looks right; stored as standing height.");
                return;
            }
            float target = PlayerPrefs.GetFloat(PrefKey, FallbackHeight);
            player.heightOffset = target - raw;
            Debug.LogWarning($"[Height] tracked {raw:F2}m is implausible -> offset {player.heightOffset:+0.00;-0.00}m to reach {target:F2}m (headset floor probably mis-calibrated).");
        }

        /// Player is standing: make the current raw height read as the stored standing height.
        public void Recalibrate(string source)
        {
            float raw = RawHeadHeight;
            float target = PlayerPrefs.GetFloat(PrefKey, FallbackHeight);
            player.heightOffset = target - raw;
            Debug.Log($"[Height] recalibrated ({source}): raw {raw:F2}m -> {target:F2}m, offset {player.heightOffset:+0.00;-0.00}m");
        }

        private bool BothSticksClicked()
        {
            bool l = false, r = false;
            InputDevices.GetDeviceAtXRNode(XRNode.LeftHand).TryGetFeatureValue(CommonUsages.primary2DAxisClick, out l);
            InputDevices.GetDeviceAtXRNode(XRNode.RightHand).TryGetFeatureValue(CommonUsages.primary2DAxisClick, out r);
            bool down = l && r;
            bool pressed = down && !_sticksWereDown;    // rising edge
            _sticksWereDown = down;
            return pressed;
        }
    }
}
