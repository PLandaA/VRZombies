using UnityEngine;
using Fusion;
using Autohand;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(AutoGunTarget))]
public class NetworkAutoGunTarget : NetworkBehaviour
{

    [Networked] public int HitCount { get; private set; }

    private AutoGunTarget _gunTarget;
    private Grabbable     _grabbable;

    private void Awake()
    {
        _gunTarget = GetComponent<AutoGunTarget>();
        _grabbable = GetComponent<Grabbable>();
    }

    public override void Spawned()
    {
        if (_grabbable != null)
            _grabbable.OnGrabEvent += OnGrabbed;

        base.Spawned();
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (_grabbable != null)
            _grabbable.OnGrabEvent -= OnGrabbed;

        base.Despawned(runner, hasState);
    }

    public void OnShotReceived(AutoGun gun, RaycastHit hit)
    {
        RPC_BroadcastHit(hit.point, hit.normal);
    }

    [Rpc(RpcSources.All, RpcTargets.All)]
    private void RPC_BroadcastHit(Vector3 hitPoint, Vector3 hitNormal)
    {
        if (Object.HasStateAuthority)
            HitCount++;

        if (!Object.HasStateAuthority)
            PlayHitEffects(hitPoint, hitNormal);
    }

    private void PlayHitEffects(Vector3 hitPoint, Vector3 hitNormal)
    {
        if (_gunTarget.hitParticle != null)
        {
            var particle = Instantiate(_gunTarget.hitParticle);
            particle.transform.position = hitPoint;
            particle.transform.forward  = hitNormal;
            particle.Play();
            Destroy(particle.gameObject, particle.main.duration + 0.5f);
        }

        if (_gunTarget.hitDecal != null)
        {
            var decal = Instantiate(_gunTarget.hitDecal);
            decal.transform.position = hitPoint;
            decal.transform.forward  = hitNormal;

            float lifetime = _gunTarget.hitDecalLifetime > 0f
                ? _gunTarget.hitDecalLifetime
                : 5f;
            Destroy(decal, lifetime);
        }
    }

    private async void OnGrabbed(Hand hand, Grabbable grabbable)
    {
        bool ok = await Object.WaitForStateAuthority();
        if (!ok)
            Debug.LogWarning("[NetworkAutoGunTarget] Could not acquire StateAuthority of the target.");
    }

    public void ResetHitCount()
    {
        if (Object.HasStateAuthority)
            HitCount = 0;
    }
}