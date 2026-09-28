using System.Collections.Generic;
using UnityEngine;
using MoreMountains.Feedbacks;
using VRZ.Enemies;

namespace VRZ.FX
{

    /// Feel-powered zombie juice: scale-punch on hit and a bigger pop on death. Event-driven:
    /// NetworkZombie.OnHealthChanged (hit) and OnDiedRender (death) fire on every client from the
    /// replicated state, so both players see identical feedback with zero network cost and no
    /// per-frame polling. Default MMF_Player stacks are built at runtime; drop hand-authored
    /// players into the override slots to replace them from the Feel editor without touching code.
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
        private bool _deathPlayed;

        private MMF_Player HitPlayer => hitPlayerOverride != null ? hitPlayerOverride : _hitPlayer;
        private MMF_Player DeathPlayer => deathPlayerOverride != null ? deathPlayerOverride : _deathPlayer;

        private void Awake()
        {
            _zombie = GetComponent<NetworkZombie>();
        }

        private void OnEnable()
        {
            if (_zombie == null) return;
            _zombie.OnLocalReset += ResetForNewLife;
            _zombie.OnHealthChanged += OnHealthChanged;
            _zombie.OnDiedRender += OnDied;
        }

        private void OnDisable()
        {
            if (_zombie == null) return;
            _zombie.OnLocalReset -= ResetForNewLife;
            _zombie.OnHealthChanged -= OnHealthChanged;
            _zombie.OnDiedRender -= OnDied;
        }

        /// Pool readiness: a re-spawned zombie must be able to play its death pop again.
        private void ResetForNewLife()
        {
            _deathPlayed = false;
            HitPlayer?.StopFeedbacks();
            DeathPlayer?.StopFeedbacks();
        }

        /// Hit punch on a health drop, except on the killing blow (the death pop plays instead).
        private void OnHealthChanged(int previous, int current)
        {
            if (current < previous && _zombie.State != NetworkZombie.ZombieState.Dead)
                HitPlayer?.PlayFeedbacks();
        }

        private void OnDied(bool headshot)
        {
            if (_deathPlayed) return;
            _deathPlayed = true;
            DeathPlayer?.PlayFeedbacks();
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
    }
}
