using UnityEngine;
using Fusion;
using Autohand;

[RequireComponent(typeof(NetworkObject))]
public class NetworkAmmoDispenser : NetworkBehaviour
{

    [Header("Prefab y Spawn")]
    [Tooltip("Prefab AR_Ammo_Gold con NetworkObject + NetworkRigidbody3D + AutoAmmo + NetworkAutoAmmo")]
    [SerializeField] private NetworkObject ammoNetworkPrefab;

    [Tooltip("Transform donde aparece el magazine al dispensar. " +
             "Normalmente el mismo transform del Dispenser o un hijo vacío.")]
    [SerializeField] private Transform spawnPoint;

    [Header("Cooldown")]
    [Tooltip("Segundos de espera entre que se toma un magazine y aparece el siguiente.")]
    [SerializeField] private float refillCooldown = 3f;

    [Networked] private TickTimer RefillTimer { get; set; }

    [Networked] private NetworkBool HasMagazine { get; set; }

    public override void Spawned()
    {
        if (Object.HasStateAuthority)
        {
            SpawnMagazine();
            HasMagazine = true;
        }

        base.Spawned();
    }

    public override void FixedUpdateNetwork()
    {
        if (!Object.HasStateAuthority) return;

        if (!HasMagazine && RefillTimer.Expired(Runner))
        {
            RefillTimer = TickTimer.None;
            SpawnMagazine();
            HasMagazine = true;
        }

        base.FixedUpdateNetwork();
    }

    public void OnMagazineTaken()
    {
        if (Object.HasStateAuthority)
            StartRefill();
        else
            RPC_RequestRefill();
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    private void RPC_RequestRefill()
    {
        StartRefill();
    }

    private void StartRefill()
    {
        HasMagazine = false;
        RefillTimer = TickTimer.CreateFromSeconds(Runner, refillCooldown);
    }

    private void SpawnMagazine()
    {
        if (ammoNetworkPrefab == null)
        {
            Debug.LogError("[NetworkAmmoDispenser] ammoNetworkPrefab not assigned.", this);
            return;
        }

        if (spawnPoint == null)
        {
            Debug.LogError("[NetworkAmmoDispenser] spawnPoint not assigned.", this);
            return;
        }

        Runner.Spawn(
            ammoNetworkPrefab,
            spawnPoint.position,
            spawnPoint.rotation,
            Runner.LocalPlayer
        );
    }

    private void OnDrawGizmosSelected()
    {
        if (spawnPoint == null) return;
        Gizmos.color = HasMagazine ? Color.green : Color.red;
        Gizmos.DrawWireCube(spawnPoint.position, Vector3.one * 0.15f);
    }
}
