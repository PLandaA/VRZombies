using UnityEngine;
using Fusion;
using Autohand;

/// Spawns bullet impact particles for the shooter and replicates them to remote clients via RPC.
public class NetworkGunHitEffect : NetworkBehaviour
{
    [Tooltip("Particle prefab instantiated at the bullet impact point")]
    [SerializeField] private GameObject hitEffectPrefab;

    [Tooltip("Seconds before the impact effect instance is destroyed")]
    [SerializeField] private float effectLifetime = 2f;

    private AutoGun _gun;

    public override void Spawned()
    {
        _gun = GetComponent<AutoGun>();
        if (_gun != null)
            _gun.OnHitEvent.AddListener(OnLocalHit);
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (_gun != null)
            _gun.OnHitEvent.RemoveListener(OnLocalHit);
    }

    private void OnLocalHit(AutoGun gun, RaycastHit hit)
    {
        if (Object == null || !Object.HasStateAuthority) return;

        SpawnEffect(hit.point, hit.normal);
        RPC_HitEffect(hit.point, hit.normal);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
    private void RPC_HitEffect(Vector3 point, Vector3 normal)
    {
        SpawnEffect(point, normal);
    }

    private void SpawnEffect(Vector3 point, Vector3 normal)
    {
        if (hitEffectPrefab == null) return;
        var fx = Instantiate(hitEffectPrefab, point + normal * 0.01f, Quaternion.LookRotation(normal));
        Destroy(fx, effectLifetime);
    }
}
