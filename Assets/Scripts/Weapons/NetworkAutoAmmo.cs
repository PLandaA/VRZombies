using UnityEngine;
using Fusion;
using Autohand;

[RequireComponent(typeof(AutoAmmo))]
[RequireComponent(typeof(NetworkObject))]
/// Syncs AutoHand magazine state (ammo count, insertion into the rifle) across clients.
public class NetworkAutoAmmo : NetworkBehaviour
{

    [Networked, OnChangedRender(nameof(OnAmmoChanged))]
    public int NetworkedAmmo { get; private set; }

    private AutoAmmo  _autoAmmo;
    private Grabbable _grabbable;

    private void Awake()
    {
        _autoAmmo  = GetComponent<AutoAmmo>();
        _grabbable = GetComponent<Grabbable>();
    }

    public override void Spawned()
    {
        if (Object.HasStateAuthority)
            NetworkedAmmo = _autoAmmo.currentAmmo;
        else
            _autoAmmo.SetAmmo(NetworkedAmmo);

        _grabbable.OnBeforeGrabEvent += OnBeforeGrabbed;
        _grabbable.OnGrabEvent += OnGrabbed;
        base.Spawned();
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        _grabbable.OnBeforeGrabEvent -= OnBeforeGrabbed;
        _grabbable.OnGrabEvent -= OnGrabbed;
        base.Despawned(runner, hasState);
    }

    public override void FixedUpdateNetwork()
    {
        if (Object.HasStateAuthority && NetworkedAmmo != _autoAmmo.currentAmmo)
            NetworkedAmmo = _autoAmmo.currentAmmo;

        base.FixedUpdateNetwork();
    }

    private void OnAmmoChanged()
    {
        if (Object.HasStateAuthority) return;
        _autoAmmo.SetAmmo(NetworkedAmmo);
    }

private void OnBeforeGrabbed(Hand hand, Grabbable grabbable)
    {
        Object.RequestStateAuthority();
        if (_grabbable.body != null)
            _grabbable.body.isKinematic = false;
    }

    private void OnGrabbed(Hand hand, Grabbable grabbable)
    {
        Object.RequestStateAuthority();
    }
}