using Fusion;
using UnityEngine;
using UnityEngine.Events;

/// Networked player state: health, damage intake from zombies, death and respawn events.
public class NetworkPlayer : NetworkBehaviour
{
    [Networked] public int TotalScore { get; set; }
    [Networked] public int Kills { get; set; }
    [Networked] public int HeadshotKills { get; set; }
    [Networked] public NetworkBool Ready { get; set; }

    /// Set by the local client (state authority over its own player) when it finishes the lobby tutorial.
    [Networked] public NetworkBool TutorialDone { get; set; }

    [Header("Health")]
    [SerializeField] private int maxHealth = 100;

    [Networked, OnChangedRender(nameof(OnHealthChanged))]
    public int Health { get; private set; }

    [Networked, OnChangedRender(nameof(OnDeathChanged))]
    public NetworkBool IsDead { get; private set; }

    public UnityEvent<int, int> OnHealthChangedEvent;
    public UnityEvent OnDiedEvent;

    public int MaxHealth => maxHealth;

    public override void Spawned()
    {
        // Register under the ACTUAL owner of this player object. Runner.LocalPlayer here was a
        // critical bug: every remote player's object registered under the LOCAL ref, corrupting
        // the registry on every client (GetPlayer(remoteRef) == null forever).
        var owner = Object.StateAuthority != PlayerRef.None ? Object.StateAuthority
                  : (Object.InputAuthority != PlayerRef.None ? Object.InputAuthority : Runner.LocalPlayer);
        NetworkManager.instance.AddPlayer(owner, this);

        if (Object.HasStateAuthority)
        {
            Health = maxHealth;
            IsDead = false;
        }
        base.Spawned();
    }

    /// Fired on the victim's own client with the attacker's world position (the RPC below
    /// targets StateAuthority, which in Shared Mode is the victim). Drives the directional
    /// damage indicator -- in VR you have no rear peripheral vision to tell you who bit you.
    public event System.Action<Vector3> OnDamagedFrom;

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_TakeDamage(int amount, Vector3 attackerPos = default)
    {
        if (IsDead) return;
        Health = Mathf.Max(0, Health - amount);
        if (attackerPos != Vector3.zero)
            OnDamagedFrom?.Invoke(attackerPos);
        if (Health <= 0)
            IsDead = true;
    }

    private void OnHealthChanged()
    {
        OnHealthChangedEvent?.Invoke(Health, maxHealth);
    }

    private void OnDeathChanged()
    {
        if (IsDead) OnDiedEvent?.Invoke();
    }
}
