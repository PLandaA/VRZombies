using UnityEngine;
using Fusion;
using Autohand;
using VRZ.Core;

namespace VRZ.Weapons
{

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
            // AutoGun raises OnHitEvent only on the client that pulled the trigger, so this IS the
            // shooter. The old "if (!HasStateAuthority) return" also dropped the
            // shooter's own particle for ~one RTT after grabbing a rifle someone else owned.
            SpawnEffect(hit.point, hit.normal);
            if (Object != null && Object.IsValid)
                RPC_HitEffect(hit.point, hit.normal);
        }

        // RpcSources.All (was StateAuthority): the shooter may still be a proxy of the rifle while
        // its authority request is in flight, and Fusion would refuse to send. Same pattern as the
        // damage RPCs; the damage itself is already authoritative on the victim, this is FX only.
        [Rpc(RpcSources.All, RpcTargets.All, InvokeLocal = false)]
        private void RPC_HitEffect(Vector3 point, Vector3 normal)
        {
            SpawnEffect(point, normal);
        }

        private void SpawnEffect(Vector3 point, Vector3 normal)
        {
            if (hitEffectPrefab == null) return;
            PrefabPool.Spawn(hitEffectPrefab, point + normal * 0.01f, Quaternion.LookRotation(normal), effectLifetime);
        }
    }
}
