using System.Collections;
using UnityEngine;
using VRZ.Core;
using Autohand;

namespace VRZ.World
{

    /// Progressive lobby tutorial: shows one sign at a time, advancing as the player grabs, loads,
    /// fires the practice rifle and throws the practice grenade. Completion flags the local
    /// player's networked TutorialDone (the lobby gates the match start on it).
    public class TutorialManager : MonoBehaviour
    {
        [Tooltip("Signs in order: TriggerGrip, Support, Load, RackSlide, Fire, Grenade, Done. Only one is visible at a time.")]
        [SerializeField] private GameObject[] steps;
        [Tooltip("Rifle BODY grabbable (Core): the front-grip / support hand. Advances the SUPPORT step.")]
        [SerializeField] private Grabbable rifleGrabbable;
        [SerializeField] private AutoGun gun;

        [Header("Trigger Grip Step")]
        [Tooltip("Rifle HANDLE grabbables (the pistol grip with the trigger). Grabbing any advances step 1.")]
        [SerializeField] private Grabbable[] triggerGrabbables;

        [Header("Second Station (optional)")]
        [SerializeField] private Grabbable[] extraRifles;
        [SerializeField] private AutoGun[] extraGuns;

        [Header("Grenade Step")]
        [Tooltip("Throwing ANY of these advances the grenade step")]
        [SerializeField] private PracticeGrenade[] grenades;

        [Tooltip("Lobby mags: hidden until the LOAD step so nobody loads the rifle ahead of the "
            + "steps. Networked scene objects: only renderers/colliders are toggled, locally.")]
        [SerializeField] private GameObject[] ammoObjects;

        [Header("Lazy follow")]
        [SerializeField] private float distance = 1.4f;
        [SerializeField] private float heightOffset = 0.05f;
        [SerializeField] private float followSpeed = 3.5f;

        [Header("Done sign")]
        [Tooltip("Body of the final sign in co-op: the lobby waits for the partner's tutorial.")]
        [SerializeField, TextArea] private string doneTextCoop = "Wait for your partner to finish training";
        [Tooltip("Body of the final sign in Solo: the lobby starts its countdown on its own (no portal, nothing to do).")]
        [SerializeField, TextArea] private string doneTextSolo = "Get ready: the match starts in a few seconds";

        private Transform _head;
        private int _step = -1;

        [Header("Skip")]
        [Tooltip("A on the right controller completes the tutorial instantly (every build: returning players skip the training). The A key does the same in the editor.")]
        [SerializeField] private bool allowSkipWithA = true;
        private bool _aWasDown;

        private void Update()
        {
            // Support-hand step: complete it if the front grip is already held (see IsFrontGripHeld).
            if (_step == 3 && IsFrontGripHeld()) { Show(4); return; }

            if (!allowSkipWithA) return;
            if (_step < 0 || _step >= 6) return;                         // already done (or not started)

            // Keyboard A, editor only (Input System package; the project runs Input System only, no legacy Input)
            if (Application.isEditor)
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null && kb.aKey.wasPressedThisFrame) { SkipTutorial(); return; }
            }

            // Controller A (right Touch primary button), rising edge only. Shipped feature since
            // 2026-10-07 (was development-builds only): the signs announce it.
            var right = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.RightHand);
            if (!right.isValid) return;
            right.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool aDown);
            bool pressed = aDown && !_aWasDown;
            _aWasDown = aDown;
            if (pressed) SkipTutorial();
        }

        /// Jump straight to the DONE sign and flag TutorialDone, exactly as throwing the grenade would.
        public void SkipTutorial()
        {
            if (_step >= 6) return;
            Debug.Log("[Tutorial] Skipped with A.");
            Show(6);
            StartCoroutine(MarkTutorialDone());
        }

        private void Start()
        {
            if (triggerGrabbables != null)
                foreach (var t in triggerGrabbables)
                    if (t != null) t.OnGrabEvent += OnTriggerGripGrab;
            if (rifleGrabbable != null) rifleGrabbable.OnGrabEvent += OnRifleGrab;
            if (gun != null)
            {
                gun.OnAmmoPlaceEvent.AddListener(OnMagPlaced);
                gun.OnSlideEvent.AddListener(OnSlideLoaded);
                gun.OnShoot.AddListener(OnShot);
            }
            if (extraRifles != null)
                foreach (var r in extraRifles)
                    if (r != null) r.OnGrabEvent += OnRifleGrab;
            if (extraGuns != null)
                foreach (var g in extraGuns)
                    if (g != null) { g.OnAmmoPlaceEvent.AddListener(OnMagPlaced); g.OnSlideEvent.AddListener(OnSlideLoaded); g.OnShoot.AddListener(OnShot); }
            if (grenades != null)
                foreach (var gr in grenades)
                    if (gr != null) gr.OnThrown.AddListener(OnGrenadeThrown);

            // Order of appearance: the grenades stay hidden until their tutorial step (no early throws)
            if (grenades != null)
                foreach (var gr in grenades)
                    if (gr != null) gr.gameObject.SetActive(false);

            // Mags too: revealed at the LOAD step (they are networked, so hide visuals/colliders only)
            SetAmmoVisible(false);

            Show(0);
        }

        /// TRUE when MY physical hands hold ANY grabbable part of this object's rig (body, grip,
        /// slide...). AutoHand hands are LOCAL-only, so IsHeld() is ground truth for "my action".
        /// Checking only the direct parent grabbable missed racking: you rack while gripping the
        /// HANDLE, which is a separate grabbable from the Core the event reports.
        private bool IsMine(Component c)
        {
            if (c == null) return false;
            var root = c.transform.root;
            foreach (var g in root.GetComponentsInChildren<Grabbable>(true))
                if (g != null && g.IsHeld()) return true;
            return false;
        }

        // Step 0: hold the rifle by the TRIGGER GRIP (Handle grabbable) -> 1: LOAD the mag ->
        // 2: RACK the slide -> 3: support hand on the front grip (Core grabbable) -> 4 fire -> 5 grenade.
        // No IsMine filter on grab events: Grabbable.OnGrabEvent only ever fires for LOCAL hands,
        // and it fires BEFORE the hand registers as holding -- an IsHeld() check at this instant
        // would reject the player's own grab.
        private void OnTriggerGripGrab(Hand hand, Grabbable g) { if (_step == 0) Show(1); }
        private void OnMagPlaced(AutoGun g, AutoAmmo a) { if (!IsMine(g)) return; if (_step <= 1) Show(2); }
        private void OnSlideLoaded(AutoGun g, SlideLoadType t)
        {
            if (!IsMine(g)) return;
            if (t == SlideLoadType.HandLoaded && _step <= 2) Show(3);
        }
        private void OnRifleGrab(Hand hand, Grabbable g) { if (_step == 3) Show(4); }
        private void OnShot(AutoGun g) { if (!IsMine(g)) return; if (_step <= 4) Show(5); }

        /// The support-hand step can already be satisfied when it appears (players often keep the
        /// second hand on the front grip while loading). OnGrabEvent won't re-fire, so poll it.
        private bool IsFrontGripHeld()
        {
            if (rifleGrabbable != null && rifleGrabbable.IsHeld()) return true;
            if (extraRifles != null)
                foreach (var r in extraRifles)
                    if (r != null && r.IsHeld()) return true;
            return false;
        }

        private void OnGrenadeThrown()
        {
            if (_step > 5) return;
            Show(6);
            StartCoroutine(MarkTutorialDone());
        }

        /// Flags the LOCAL player's networked TutorialDone so the lobby can gate the match start.
        ///
        /// Waiting is normal here: since the session menu the tutorial can be
        /// finished before any room exists, so we must keep the flag pending until the player
        /// connects. What is NOT normal is a running session whose local NetworkPlayer never
        /// shows up (spawn failed): that used to stall the lobby forever with no clue. We keep
        /// waiting (giving up would silently break the gate) but say why, loudly and periodically.
        private IEnumerator MarkTutorialDone()
        {
            const float pollSeconds = 0.5f;
            const float firstWarnAfter = 10f;   // seconds of "running but no player" before the first warning
            const float warnEvery = 30f;

            float runningWithoutPlayer = 0f;
            float nextWarnAt = firstWarnAfter;

            while (true)
            {
                var nm = NetworkSession.Current;
                bool running = nm != null && nm.IsRunning;

                if (running)
                {
                    var np = nm.GetPlayer();
                    if (np != null && np.IsValid)
                    {
                        np.TutorialDone = true;
                        Debug.Log("[Tutorial] Completed - TutorialDone synced.");
                        yield break;
                    }

                    runningWithoutPlayer += pollSeconds;
                    if (runningWithoutPlayer >= nextWarnAt)
                    {
                        Debug.LogWarning("[Tutorial] Session is running but the local NetworkPlayer is missing after "
                                         + runningWithoutPlayer.ToString("F0") + " s. TutorialDone cannot be set; the lobby will "
                                         + "stay on 'COMPLETE THE TUTORIAL'. Check NetworkManager.SpawnPlayer / playerPrefab.");
                        nextWarnAt += warnEvery;
                    }
                }
                else
                {
                    // Not connected yet (menu open, or reconnecting): counting restarts when a
                    // session appears, so a long menu wait never produces a false alarm.
                    runningWithoutPlayer = 0f;
                    nextWarnAt = firstWarnAfter;
                }

                yield return new WaitForSeconds(pollSeconds);
            }
        }

        private void Show(int step)
        {
            Vector3? carryPos = null;
            Quaternion? carryRot = null;
            if (_step >= 0 && _step < steps.Length && steps[_step] != null)
            {
                carryPos = steps[_step].transform.position;
                carryRot = steps[_step].transform.rotation;
            }
            _step = step;
            for (int i = 0; i < steps.Length; i++)
                if (steps[i] != null) steps[i].SetActive(i == step);
            if (carryPos.HasValue && step < steps.Length && steps[step] != null)
                steps[step].transform.SetPositionAndRotation(carryPos.Value, carryRot.Value);

            // The grenade step reveals the grenades: appearing right on cue IS the instruction
            if (step == 5 && grenades != null)
                foreach (var gr in grenades)
                    if (gr != null && !gr.gameObject.activeSelf) gr.gameObject.SetActive(true);

            // The LOAD step (now step 1) reveals the mags -- can't skip ahead loading from another angle
            if (step >= 1)
                SetAmmoVisible(true);

            // Final sign: what happens next differs per mode (co-op waits for the partner, Solo
            // counts down by itself), so the body is picked here; the heading on the sign stays.
            if (step == 6 && steps[6] != null)
            {
                var tmp = steps[6].GetComponent<TMPro.TMP_Text>();
                if (tmp != null)
                {
                    bool solo = NetworkSession.Current != null && NetworkSession.Current.IsSolo;
                    int nl = tmp.text.IndexOf('\n');
                    string heading = nl >= 0 ? tmp.text.Substring(0, nl) : tmp.text;
                    tmp.text = heading + "\n" + (solo ? doneTextSolo : doneTextCoop);
                }
            }
        }

        private void SetAmmoVisible(bool visible)
        {
            // Renderers + grabbability ONLY. Colliders and physics stay untouched: disabling
            // colliders let gravity drop the mags through the bench, and re-enabling them while
            // embedded in geometry made PhysX eject them skyward (the "flying ammo" bug).
            if (ammoObjects == null) return;
            foreach (var ammo in ammoObjects)
            {
                if (ammo == null) continue;
                foreach (var r in ammo.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
                foreach (var g in ammo.GetComponentsInChildren<Grabbable>(true)) g.enabled = visible;
            }
        }

        private void LateUpdate()
        {
            if (_step < 0 || _step >= steps.Length || steps[_step] == null) return;
            if (_head == null)
            {
                var ahp = FindFirstObjectByType<AutoHandPlayer>();
                if (ahp != null && ahp.headCamera != null) _head = ahp.headCamera.transform;
                if (_head == null) return;
            }
            var sign = steps[_step].transform;
            Vector3 fwd = _head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) return;
            fwd.Normalize();
            Vector3 target = _head.position + fwd * distance + Vector3.up * heightOffset;
            sign.position = Vector3.Lerp(sign.position, target, Time.deltaTime * followSpeed);
            Vector3 look = sign.position - _head.position;
            look.y *= 0.5f;
            if (look.sqrMagnitude > 0.001f)
                sign.rotation = Quaternion.Slerp(sign.rotation, Quaternion.LookRotation(look), Time.deltaTime * followSpeed);
        }
    }
}
