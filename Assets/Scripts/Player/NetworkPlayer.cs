using Fusion;
using UnityEngine;
using VRZ.Core;

namespace VRZ.Player
{

    /// Networked player state: health, damage intake from zombies, death and respawn events.
    public class NetworkPlayer : NetworkBehaviour, IPlayerState
    {
        // ── IPlayerState / IDamageable ── (players have no critical zones; the avatar carries no hit colliders)
        public bool IsValid => Object != null && Object.IsValid;
        public bool IsAlive => !IsDead;
        public Vector3 Position => transform.position;
        public bool IsCriticalHit(Vector3 hitPoint) => false;
        public void ApplyDamage(in DamageInfo damage) => RPC_TakeDamage(damage.Amount, damage.SourcePosition);
        bool IPlayerState.Ready { get => Ready; set => Ready = value; }               // NetworkBool <-> bool bridges
        bool IPlayerState.TutorialDone { get => TutorialDone; set => TutorialDone = value; }

        [Networked] public int TotalScore { get; set; }
        [Networked] public int Kills { get; set; }
        [Networked] public int HeadshotKills { get; set; }
        [Networked] public NetworkBool Ready { get; set; }

        /// Set by the local client (state authority over its own player) when it finishes the lobby tutorial.
        [Networked] public NetworkBool TutorialDone { get; set; }

        [Header("Health")]
        [SerializeField] private int maxHealth = 100;

        [Networked, OnChangedRender(nameof(OnHealthChangedRender))]
        public int Health { get; private set; }

        [Networked, OnChangedRender(nameof(OnDeathChanged))]
        public NetworkBool IsDead { get; private set; }

        public int MaxHealth => maxHealth;

        public override void Spawned()
        {
            // Player registry: Fusion's own PlayerRef -> NetworkObject association. In Shared Mode each
            // player may only set its OWN association and must hold State Authority over the object
            // (the owner of this NetworkPlayer does). The association is networked and replicated,
            // so every client resolves any player with Runner.TryGetPlayerObject (NetworkManager.GetPlayer).
            // Registered under Runner.LocalPlayer, not Object.StateAuthority: in Fusion's Single mode
            // (Solo Survival) StateAuthority reads as None even though we hold it, and the registry
            // then found nobody. In Shared Mode holding the authority means the two are the same ref.
            if (Object.HasStateAuthority)
                Runner.SetPlayerObject(Runner.LocalPlayer, Object);

            // On EVERY client: keep the player's stats alive across the Lobby -> Arena load. The scene
            // manager destroys the spawned objects of the scene it unloads; the runner is
            // DontDestroyOnLoad, so parenting under it carries this object into the next scene.
            transform.SetParent(Runner.transform);

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

        /// IPlayerState.OnHealthChanged: raised on every client from Fusion's OnChangedRender
        /// on the replicated Health (readers subscribe instead of polling).
        public event System.Action<int, int> OnHealthChanged;

        /// IPlayerState.OnDied: raised on every client from the IsDead OnChangedRender.
        public event System.Action OnDied;

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RPC_TakeDamage(int amount, Vector3 attackerPos = default)
        {
            if (IsDead) return;
            // Testing without dying is a compile-time switch (Core/DevFlags.cs), not a 10000-HP
            // prefab. Health/MaxHealth stay real so the damage vignette and heartbeat still make sense.
#if VRZ_INVULNERABLE
            amount = 0;
#endif
            Health = Mathf.Max(0, Health - amount);
            if (attackerPos != Vector3.zero)
                OnDamagedFrom?.Invoke(attackerPos);
            if (Health <= 0)
                IsDead = true;
        }

        private void OnHealthChangedRender()
        {
            OnHealthChanged?.Invoke(Health, maxHealth);
        }

        private void OnDeathChanged()
        {
            if (IsDead) OnDied?.Invoke();
        }
    }
}
