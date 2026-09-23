using System;
using UnityEngine;
using VRZ.Core;

namespace VRZ.Tests.Fakes
{
    /// Hand-rolled test double for the player contract. Every property is a plain settable field
    /// so a test can describe a player in one line: new FakePlayerState { Ready = true, Health = 0 }.
    public class FakePlayerState : IPlayerState
    {
        public bool IsValid { get; set; } = true;
        public bool Ready { get; set; }
        public bool TutorialDone { get; set; }
        public int MaxHealth { get; set; } = 100;
        public int Health { get; set; } = 100;
        public int TotalScore { get; set; }
        public int Kills { get; set; }
        public int HeadshotKills { get; set; }
        public Vector3 Position { get; set; }

        public bool IsAlive => Health > 0;

        public event Action<Vector3> OnDamagedFrom;
        public event Action<int, int> OnHealthChanged;
        public event Action OnDied;

        public bool IsCriticalHit(Vector3 hitPoint) => false;

        public void ApplyDamage(in DamageInfo damage)
        {
            Health = Mathf.Max(0, Health - damage.Amount);
            OnDamagedFrom?.Invoke(damage.SourcePosition);
            OnHealthChanged?.Invoke(Health, MaxHealth);
            if (Health <= 0) OnDied?.Invoke();
        }
    }
}
