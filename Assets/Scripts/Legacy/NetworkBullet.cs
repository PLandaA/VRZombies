using UnityEngine;
using Fusion;
using Autohand;

[RequireComponent(typeof(NetworkObject))]
public class NetworkBullet : NetworkBehaviour
{

    [Header("Lifetime")]
    [Tooltip("Seconds until the shell casing is despawned from the network.\n" +
             "Mantener bajo (3-5s) para no acumular NetworkObjects innecesarios.")]
    [SerializeField] private float networkLifetime = 4f;

    [Networked] private TickTimer LifetimeTimer { get; set; }

    public override void Spawned()
    {
        if (Object.HasStateAuthority)
            LifetimeTimer = TickTimer.CreateFromSeconds(Runner, networkLifetime);

        TryIgnorePlayerCollision();

        base.Spawned();
    }

    public override void FixedUpdateNetwork()
    {
        if (Object.HasStateAuthority && LifetimeTimer.Expired(Runner))
            Runner.Despawn(Object);

        base.FixedUpdateNetwork();
    }

    private void TryIgnorePlayerCollision()
    {
        if (AutoHandPlayer.Instance == null) return;

        if (TryGetComponent<IgnoreHandPlayerCollision>(out var ignoreScript))
            ignoreScript.ActivateIgnoreCollision();
    }
}
