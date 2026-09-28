using UnityEngine;
using VRZ.Core;
using VRZ.Network;

namespace VRZ.Player
{
    /// Hides a player's avatar on EVERY client while that player is dead (spectating), so the dead
    /// player's own body and hands are not in the way of the spectator camera, and the partner does
    /// not see an avatar floating behind them (the avatar follows its owner's camera through IK).
    ///
    /// Presentation only: toggles Renderer.enabled and restores each renderer's original state, so
    /// it never touches the pose pipeline. (LocalAvatarHider shrinks the owner's head/hand bones
    /// instead of disabling renderers, so the two do not interfere.)
    ///
    /// Event-driven: binds once to the avatar owner's IPlayerState and reacts to OnDied (hide) and to
    /// OnHealthChanged while alive (show again, for a future revive). No per-frame polling once bound.
    [RequireComponent(typeof(NetworkRig))]
    public class DeadAvatarHider : MonoBehaviour
    {
        private NetworkRig _rig;
        private Renderer[] _renderers;
        private bool[] _original;
        private bool _hidden;
        private IPlayerState _owner;
        private float _nextBindAttempt;

        private void Awake()
        {
            _rig = GetComponent<NetworkRig>();
            _renderers = GetComponentsInChildren<Renderer>(true);
        }

        private void OnDisable()
        {
            Unbind();
            SetHidden(false);
        }

        private void Update()
        {
            // Only until bound: the owner's NetworkPlayer can appear a moment after the avatar.
            if (_owner != null || Time.time < _nextBindAttempt) return;
            _nextBindAttempt = Time.time + 0.5f;
            TryBind();
        }

        private void TryBind()
        {
            var nm = NetworkManager.instance;
            if (nm == null || _rig == null || _rig.Object == null || !_rig.Object.IsValid) return;
            var owner = nm.GetPlayer(_rig.Object.StateAuthority);   // the avatar's owner is its State Authority
            if (owner == null) return;

            _owner = owner;
            _owner.OnDied += OnOwnerDied;
            _owner.OnHealthChanged += OnOwnerHealthChanged;
            if (!_owner.IsAlive) SetHidden(true);                   // bound after the death already happened
        }

        private void Unbind()
        {
            if (_owner == null) return;
            _owner.OnDied -= OnOwnerDied;
            _owner.OnHealthChanged -= OnOwnerHealthChanged;
            _owner = null;
        }

        private void OnOwnerDied() => SetHidden(true);

        private void OnOwnerHealthChanged(int health, int max)
        {
            if (_hidden && _owner != null && _owner.IsAlive) SetHidden(false);
        }

        private void SetHidden(bool hide)
        {
            if (hide == _hidden || _renderers == null) return;
            _hidden = hide;
            if (hide)
            {
                _original = new bool[_renderers.Length];
                for (int i = 0; i < _renderers.Length; i++)
                {
                    if (_renderers[i] == null) continue;
                    _original[i] = _renderers[i].enabled;
                    _renderers[i].enabled = false;
                }
            }
            else if (_original != null)
            {
                for (int i = 0; i < _renderers.Length; i++)
                    if (_renderers[i] != null) _renderers[i].enabled = _original[i];
            }
        }
    }
}
