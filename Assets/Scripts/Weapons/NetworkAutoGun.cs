using UnityEngine;
using System.Reflection;
using Fusion;
using Autohand;

[RequireComponent(typeof(AutoGun))]
[RequireComponent(typeof(NetworkObject))]
/// Networked rifle: tick-synced shot effects and shooter-authoritative hit damage over Fusion 2 Shared Mode.
public class NetworkAutoGun : NetworkBehaviour
{
    [Networked] public int NetworkedAmmo { get; private set; }
    [Networked] private int LastShootTick { get; set; }
    [Networked] private Vector3 SlideLocalPosition { get; set; }
    [Networked, OnChangedRender(nameof(OnLoadedMagChanged))]
    public NetworkBehaviourId LoadedMagId { get; private set; }

    [Header("Slide")]
    [SerializeField] private Transform slideTransform;
    [SerializeField] private float slideInterpolationSpeed = 25f;

    [Header("Damage")]
    [SerializeField] private int bulletDamage = 25;

    private AutoGun _gun;
    private AutoGunEffects _effects;
    private Grabbable _grabbable;
    private ChangeDetector _changes;

    public int GetNetworkedAmmo() => NetworkedAmmo;

    private void Awake()
    {
        _gun = GetComponent<AutoGun>();
        _effects = GetComponent<AutoGunEffects>();
        _grabbable = GetComponent<Grabbable>();
    }

    public override void Spawned()
    {
        _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);
        _gun.OnShoot.AddListener(OnLocalShoot);
        _gun.OnHitEvent.AddListener(OnLocalHit);
        _gun.OnAmmoPlaceEvent.AddListener(OnLocalAmmoPlace);
        _gun.OnAmmoRemoveEvent.AddListener(OnLocalAmmoRemove);
                _grabbable.OnBeforeGrabEvent += OnBeforeGrabbed;
        _grabbable.OnGrabEvent += OnGrabbed;
        foreach (var childGrab in GetComponentsInChildren<Grabbable>(true))
        {
            if (childGrab != _grabbable)
                childGrab.OnBeforeGrabEvent += OnChildGrabbed;
        }
        _grabbable.OnGrabEvent += OnGrabbed;
        if (Object.HasStateAuthority)
        {
            NetworkedAmmo = _gun.GetAmmo();
            if (slideTransform != null)
                SlideLocalPosition = slideTransform.localPosition;
        }
        OnLoadedMagChanged();
        base.Spawned();
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        _gun.OnShoot.RemoveListener(OnLocalShoot);
        _gun.OnHitEvent.RemoveListener(OnLocalHit);
        _gun.OnAmmoPlaceEvent.RemoveListener(OnLocalAmmoPlace);
        _gun.OnAmmoRemoveEvent.RemoveListener(OnLocalAmmoRemove);
        _grabbable.OnBeforeGrabEvent -= OnBeforeGrabbed;
        _grabbable.OnGrabEvent -= OnGrabbed;
        foreach (var childGrab in GetComponentsInChildren<Grabbable>(true))
        {
            if (childGrab != _grabbable)
                childGrab.OnBeforeGrabEvent -= OnChildGrabbed;
        }
        base.Despawned(runner, hasState);
    }

    public override void FixedUpdateNetwork()
    {
        if (Object.HasStateAuthority)
        {
            if (NetworkedAmmo != _gun.GetAmmo())
                NetworkedAmmo = _gun.GetAmmo();
            if (slideTransform != null && SlideLocalPosition != slideTransform.localPosition)
                SlideLocalPosition = slideTransform.localPosition;
        }
        base.FixedUpdateNetwork();
    }

    public override void Render()
    {
        foreach (var change in _changes.DetectChanges(this, out _, out _))
        {
            if (change == nameof(LastShootTick) && !Object.HasStateAuthority)
                PlayRemoteShootEffects();
        }
        if (!Object.HasStateAuthority && slideTransform != null)
        {
            slideTransform.localPosition = Vector3.Lerp(slideTransform.localPosition, SlideLocalPosition, Time.deltaTime * slideInterpolationSpeed);
        }

        if (LoadedMagId.IsValid && _gun != null && _gun.magazinePoint != null && Runner != null &&
            Runner.TryFindBehaviour(LoadedMagId, out NetworkBehaviour magBeh2))
        {
            if (magBeh2.transform.parent != _gun.magazinePoint.transform)
                magBeh2.transform.SetParent(_gun.magazinePoint.transform, true);
            magBeh2.transform.localPosition = Vector3.zero;
            magBeh2.transform.localRotation = Quaternion.identity;
        }
    }

    private void OnBeforeGrabbed(Hand hand, Grabbable grabbable)
    {
        Object.RequestStateAuthority();
        if (_grabbable.body != null)
            _grabbable.body.isKinematic = false;
        if (LoadedMagId.IsValid && Runner.TryFindBehaviour(LoadedMagId, out NetworkBehaviour magBeh))
            magBeh.Object.RequestStateAuthority();
    }

    private void OnChildGrabbed(Hand hand, Grabbable childGrabbable)
    {
        Object.RequestStateAuthority();
        MarkLocalPlayerReady();
        if (_grabbable.body != null)
            _grabbable.body.isKinematic = false;
        if (LoadedMagId.IsValid && Runner.TryFindBehaviour(LoadedMagId, out NetworkBehaviour magBeh))
            magBeh.Object.RequestStateAuthority();
    }

        private void OnGrabbed(Hand hand, Grabbable grabbable)
    {
        Object.RequestStateAuthority();
        MarkLocalPlayerReady();
    }

    private void MarkLocalPlayerReady()
    {
        if (NetworkManager.instance == null) return;
        var np = NetworkManager.instance.GetPlayer();
        if (np != null && !np.Ready)
        {
            np.Ready = true;
            Debug.Log("[NetGun] Local player marked READY (grabbed a weapon)");
        }
    }

    private void OnLocalAmmoPlace(AutoGun gun, AutoAmmo ammo)
    {
        string ammoName = ammo != null ? ammo.name : "null";
        Debug.Log("[NetGun] OnLocalAmmoPlace ammo=" + ammoName + " HasGunAuth=" + Object.HasStateAuthority);
        if (ammo == null) return;
        var netAmmo = ammo.GetComponent<NetworkAutoAmmo>();
        if (netAmmo == null) { Debug.Log("[NetGun] No NetworkAutoAmmo on ammo"); return; }
        netAmmo.Object.RequestStateAuthority();
        if (Object.HasStateAuthority)
        {
            LoadedMagId = netAmmo.Id;
            Debug.Log("[NetGun] LoadedMagId set");
        }
    }

    private void OnLocalAmmoRemove(AutoGun gun, AutoAmmo ammo)
    {
        if (Object.HasStateAuthority)
            LoadedMagId = default;
    }

    private void OnLoadedMagChanged()
    {
        Transform magPP = (_gun != null && _gun.magazinePoint != null) ? _gun.magazinePoint.transform : null;
        Debug.Log("[NetGun] OnLoadedMagChanged IsValid=" + LoadedMagId.IsValid + " magPP=" + (magPP!=null?magPP.name:"NULL"));
        if (!LoadedMagId.IsValid || Runner == null || magPP == null) return;
        if (!Runner.TryFindBehaviour(LoadedMagId, out NetworkBehaviour magBeh)) { Debug.Log("[NetGun] TryFindBehaviour FAILED"); return; }
        Debug.Log("[NetGun] Parenting mag " + magBeh.name + " to " + magPP.name);
        magBeh.transform.SetParent(magPP, true);
        magBeh.transform.localPosition = Vector3.zero;
        magBeh.transform.localRotation = Quaternion.identity;
        var autoAmmo = magBeh.GetComponent<AutoAmmo>();
        if (autoAmmo != null && _gun != null)
        {
            var field = typeof(AutoGun).GetField("loadedAmmo", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null) field.SetValue(_gun, autoAmmo);
        }
    }

    private void OnLocalHit(AutoGun gun, RaycastHit hit)
    {
        if (hit.collider == null) return;

        NetworkZombie zombie = hit.collider.GetComponentInParent<NetworkZombie>();
        if (zombie != null && zombie.State != NetworkZombie.ZombieState.Dead)
        {
            Debug.Log("[NetGun] Hit zombie: " + zombie.name + " damage=" + bulletDamage);
            zombie.RPC_TakeDamage(bulletDamage);
        }
    }

    private void OnLocalShoot(AutoGun gun)
    {
        if (!Object.HasStateAuthority) return;
        LastShootTick = Runner.Tick;
    }

    private void PlayRemoteShootEffects()
    {
        if (_effects == null) return;
        if (_effects.shootSound != null && _effects.shootSound.clip != null)
            _effects.shootSound.PlayOneShot(_effects.shootSound.clip);
        if (_effects.shootParticle != null)
        {
            var p = Instantiate(_effects.shootParticle);
            p.transform.SetPositionAndRotation(_gun.shootForward.position, _gun.shootForward.rotation);
            p.Play();
            Destroy(p.gameObject, p.main.duration + 0.5f);
        }
        _effects.EjectShell();
    }
}
