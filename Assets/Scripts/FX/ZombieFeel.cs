using System.Collections.Generic;
using UnityEngine;
using MoreMountains.Feedbacks;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.World;

namespace VRZ.FX
{

    /// Feel-powered zombie juice: scale-punch on hit and a bigger pop on death, triggered by
    /// polling networked Health/State (same pattern as ZombieHitFlash — every client reacts
    /// to replicated state, so both players see identical feedback with zero network cost).
    /// Default MMF_Player stacks are built at runtime; drop hand-authored players into the
    /// override slots to replace them from the Feel editor without touching code.
    public class ZombieFeel : MonoBehaviour
    {
        [Header("Feel Overrides (optional, author in the Feel editor)")]
        [SerializeField] private MMF_Player hitPlayerOverride;
        [SerializeField] private MMF_Player deathPlayerOverride;

        [Header("Default Punch Tuning")]
        [Tooltip("Peak scale multiplier of the hit punch")]
        [SerializeField] private float hitPunch = 1.09f;
        [SerializeField] private float hitDuration = 0.12f;
        [Tooltip("Peak scale multiplier of the death pop")]
        [SerializeField] private float deathPunch = 1.16f;
        [SerializeField] private float deathDuration = 0.25f;

        private NetworkZombie _zombie;
        private MMF_Player _hitPlayer;
        private MMF_Player _deathPlayer;
        private int _lastHealth = int.MinValue;
        private bool _deathPlayed;

        private void Awake()
        {
            _zombie = GetComponent<NetworkZombie>();
        }

        private void Start()
        {
            if (hitPlayerOverride == null)
                _hitPlayer = BuildPunchPlayer("Feel_HitPunch", hitPunch, hitDuration);
            if (deathPlayerOverride == null)
                _deathPlayer = BuildPunchPlayer("Feel_DeathPunch", deathPunch, deathDuration);
        }

        private MMF_Player BuildPunchPlayer(string playerName, float peak, float duration)
        {
            // MMF_Player is [DisallowMultipleComponent] — each one gets its own child GameObject.
            var host = new GameObject(playerName);
            host.transform.SetParent(transform, false);
            var player = host.AddComponent<MMF_Player>();
            if (player.FeedbacksList == null)
                player.FeedbacksList = new List<MMF_Feedback>();   // runtime-added players start with a null list
            var scale = new MMF_Scale();
            scale.AnimateScaleTarget = transform;
            scale.RemapCurveZero = 1f;
            scale.RemapCurveOne = peak;
            scale.AnimateScaleDuration = duration;
            scale.UniformScaling = true;
            scale.AllowAdditivePlays = false;
            scale.DetermineScaleOnPlay = true;
            player.AddFeedback(scale);
            player.Initialization();
            return player;
        }

        private void Update()
        {
            if (_zombie == null || _zombie.Object == null || !_zombie.Object.IsValid) return;

            int h = _zombie.Health;
            if (_lastHealth == int.MinValue)
                _lastHealth = h;
            else if (h < _lastHealth && _zombie.State != NetworkZombie.ZombieState.Dead)
                (hitPlayerOverride != null ? hitPlayerOverride : _hitPlayer)?.PlayFeedbacks();
            _lastHealth = h;

            if (_zombie.State == NetworkZombie.ZombieState.Dead && !_deathPlayed)
            {
                _deathPlayed = true;
                (deathPlayerOverride != null ? deathPlayerOverride : _deathPlayer)?.PlayFeedbacks();
            }
        }
    }
}
