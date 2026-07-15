using Fusion;
using UnityEngine;
using UnityEngine.Events;

/// Networked player state: health, damage intake from zombies, death and respawn events.
public class NetworkPlayer : NetworkBehaviour
{
    [Networked] public int TotalScore { get; set; }
    [Networked] public NetworkBool Ready { get; set; }

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
        NetworkManager.instance.AddPlayer(Runner.LocalPlayer, this);
        if (Object.HasStateAuthority)
        {
            Health = maxHealth;
            IsDead = false;
        }
        base.Spawned();
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_TakeDamage(int amount)
    {
        if (IsDead) return;
        Health = Mathf.Max(0, Health - amount);
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
