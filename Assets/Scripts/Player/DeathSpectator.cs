using Autohand;
using UnityEngine;
using VRZ.Core;
using VRZ.Network;

namespace VRZ.Player
{
    /// Spectator follow-cam for a dead player (design decision 2026-09-21).
    ///
    /// In VR you cannot put the player's eyes inside the partner's head: their head motion is not
    /// yours and it nauseates in seconds. Instead, when the local player dies:
    ///   - locomotion and hands are switched off (no moving, no grabbing, no shooting),
    ///   - the tracking container is parked ~2 m behind and ~0.7 m above the partner's head and
    ///     follows them with smoothing, while the player keeps their OWN head rotation (look
    ///     around freely; no forced yaw, which would feel like smooth turning).
    /// Pure presentation: reads the partner's avatar from NetworkManager.Rigs, changes nothing on
    /// the network. The match ends (game over / partner left) before anything needs restoring;
    /// End() exists for a future respawn.
    public class DeathSpectator : MonoBehaviour
    {
        [SerializeField] private float followDistance = 2.2f;
        [SerializeField] private float followHeight = 0.7f;
        [SerializeField] private float smoothing = 3f;
        [Tooltip("Head height of the partner's avatar above its root, used as the look target.")]
        [SerializeField] private float partnerHeadHeight = 1.6f;

        private AutoHandPlayer _player;
        private IPlayerState _me;
        private bool _spectating;
        private bool _prevUseMovement, _prevUseGrounding, _prevKinematic, _prevDetect;

        private void Awake()
        {
            _player = GetComponentInChildren<AutoHandPlayer>(true);
        }

        private void OnDisable()
        {
            Unbind();
        }

        private void Update()
        {
            // Bind to the local player once it exists (it is created after the scene loads).
            var current = NetworkSession.Current?.GetPlayer();
            if (current != _me) { Unbind(); _me = current; if (_me != null) _me.OnDied += Begin; }

            // Late binder: if we bound after the death already happened, catch up.
            if (!_spectating && _me != null && _me.IsValid && !_me.IsAlive) Begin();
            if (_spectating && _me != null && _me.IsValid && _me.IsAlive) End();
        }

        private void Unbind()
        {
            if (_me != null) _me.OnDied -= Begin;
            _me = null;
        }

        private void Begin()
        {
            if (_spectating || _player == null) return;
            _spectating = true;

            // Drop whatever is held and switch the hands off: a dead player must not shoot.
            SetHand(_player.handRight, false);
            SetHand(_player.handLeft, false);

            // Freeze the body and stop AutoHand from steering the tracking container, so the
            // container is ours to place. useMovement=false also disables snap turning, which is
            // what we want: our yaw stays put and only the head tracks.
            _prevUseMovement = _player.useMovement;
            _prevUseGrounding = _player.useGrounding;
            _player.useMovement = false;
            _player.useGrounding = false;
            if (_player.body != null)
            {
                _prevKinematic = _player.body.isKinematic;
                _prevDetect = _player.body.detectCollisions;
                _player.body.linearVelocity = Vector3.zero;
                _player.body.isKinematic = true;
                _player.body.detectCollisions = false;
            }
            Debug.Log("[DeathSpectator] Local player died: following the partner.");
        }

        private void End()
        {
            if (!_spectating || _player == null) return;
            _spectating = false;
            _player.useMovement = _prevUseMovement;
            _player.useGrounding = _prevUseGrounding;
            if (_player.body != null) { _player.body.isKinematic = _prevKinematic; _player.body.detectCollisions = _prevDetect; }
            SetHand(_player.handRight, true);
            SetHand(_player.handLeft, true);
        }

        private static void SetHand(Hand hand, bool on)
        {
            if (hand == null) return;
            if (!on) hand.ForceReleaseGrab();
            hand.gameObject.SetActive(on);
        }

        private void LateUpdate()
        {
            if (!_spectating || _player == null || _player.trackingContainer == null || _player.headCamera == null) return;

            var partner = FindPartner();
            if (partner == null) return;   // partner gone: hold position; the match is ending anyway

            // Target for the EYES: behind and above the partner's head, opposite to their facing.
            Vector3 head = partner.position + Vector3.up * partnerHeadHeight;
            Vector3 fwd = partner.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward; else fwd.Normalize();
            Vector3 eyesTarget = head - fwd * followDistance + Vector3.up * followHeight;

            // We move the CONTAINER, not the camera (the camera is tracked inside it). Keep the
            // player's own head offset so the eyes land on eyesTarget wherever they are looking.
            Vector3 eyeOffset = _player.headCamera.transform.position - _player.trackingContainer.position;
            Vector3 containerTarget = eyesTarget - eyeOffset;

            var c = _player.trackingContainer;
            c.position = Vector3.Lerp(c.position, containerTarget, 1f - Mathf.Exp(-smoothing * Time.deltaTime));
        }

        /// The partner's replicated avatar root (NetworkRig registered in NetworkManager.Rigs).
        private Transform FindPartner()
        {
            var nm = NetworkManager.instance;
            if (nm == null || nm.runner == null) return null;
            foreach (var rig in nm.Rigs)
            {
                if (rig == null || rig.Object == null || !rig.Object.IsValid) continue;
                bool isLocal = rig.Object.StateAuthority == nm.runner.LocalPlayer || rig.Object.InputAuthority == nm.runner.LocalPlayer;
                if (!isLocal) return rig.transform;
            }
            return null;
        }
    }
}
