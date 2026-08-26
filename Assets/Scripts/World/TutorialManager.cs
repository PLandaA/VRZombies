using System.Collections;
using UnityEngine;
using Autohand;

/// Progressive lobby tutorial: shows one sign at a time, advancing as the player grabs, loads,
/// fires the practice rifle and throws the practice grenade. Completion flags the local
/// player's networked TutorialDone (the lobby gates the match start on it).
public class TutorialManager : MonoBehaviour
{
    [Tooltip("Signs in order: Grab, Load, RackSlide, Fire, Grenade, Done. Only one is visible at a time.")]
    [SerializeField] private GameObject[] steps;
    [SerializeField] private Grabbable rifleGrabbable;
    [SerializeField] private AutoGun gun;

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

    private Transform _head;
    private int _step = -1;

    private void Start()
    {
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
        Debug.Log("[TutDbg] Event rejected (nothing held) from " + root.name);
        return false;
    }

    private void OnRifleGrab(Hand hand, Grabbable g) { if (!IsMine(g)) return; Debug.Log("[TutDbg] RifleGrab step=" + _step); if (_step == 0) Show(1); }
    private void OnMagPlaced(AutoGun g, AutoAmmo a) { if (!IsMine(g)) return; Debug.Log("[TutDbg] MagPlaced step=" + _step); if (_step <= 1) Show(2); }
    private void OnSlideLoaded(AutoGun g, SlideLoadType t)
    {
        if (!IsMine(g)) return;
        Debug.Log("[TutDbg] SlideLoaded step=" + _step + " type=" + t);
        if (t == SlideLoadType.HandLoaded && _step <= 2) Show(3);
    }
    private void OnShot(AutoGun g) { if (!IsMine(g)) return; Debug.Log("[TutDbg] Shot step=" + _step); if (_step <= 3) Show(4); }

    private void OnGrenadeThrown()
    {
        if (_step > 4) return;
        Show(5);
        StartCoroutine(MarkTutorialDone());
    }

    /// Flags the LOCAL player's networked TutorialDone so the lobby can gate the match start.
    private IEnumerator MarkTutorialDone()
    {
        while (true)
        {
            var nm = NetworkManager.instance;
            if (nm != null && nm.runner != null && nm.runner.IsRunning)
            {
                var np = nm.GetPlayer(nm.runner.LocalPlayer);
                if (np != null && np.Object != null && np.Object.IsValid)
                {
                    np.TutorialDone = true;
                    Debug.Log("[Tutorial] Completed - TutorialDone synced.");
                    yield break;
                }
            }
            yield return new WaitForSeconds(0.5f);
        }
    }

    private void Show(int step)
    {
        Debug.Log("[TutDbg] Show(" + step + ") prev=" + _step + " sign=" + (step < steps.Length && steps[step] != null ? steps[step].name : "NULL"));
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
        if (step == 4 && grenades != null)
            foreach (var gr in grenades)
                if (gr != null && !gr.gameObject.activeSelf) gr.gameObject.SetActive(true);

        // The LOAD step reveals the mags -- can't skip ahead loading from another angle
        if (step >= 1)
            SetAmmoVisible(true);
    }

    private void SetAmmoVisible(bool visible)
    {
        if (ammoObjects == null) return;
        foreach (var ammo in ammoObjects)
        {
            if (ammo == null) continue;
            foreach (var r in ammo.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
            foreach (var col in ammo.GetComponentsInChildren<Collider>(true)) col.enabled = visible;
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
            Debug.Log("[TutDbg] Head acquired: " + _head.name);
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
