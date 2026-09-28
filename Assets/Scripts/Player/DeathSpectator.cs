using Autohand;
using Autohand.Demo;
using UnityEngine;
using VRZ.Core;
using VRZ.Network;

namespace VRZ.Player
{
    /// Spectator follow-cam for a dead player (design decision 2026-09-21, follow model 2026-09-25).
    ///
    /// In VR you cannot put the player's eyes inside the partner's head: their head motion is not
    /// yours and it nauseates in seconds. Instead, when the local player dies:
    ///   - locomotion and hands are switched off (no moving, no grabbing, no shooting),
    ///   - the tracking container is parked behind and above the partner's head and follows their
    ///     TRANSLATION only (see SpectatorFollow): the spectator's own head tracking is never
    ///     touched and the partner turning never orbits the view,
    ///   - the player keeps their own head rotation and their own snap turn (re-implemented here
    ///     around the eyes: AutoHand's snap turn pivots on the physics body, which stays where the
    ///     player died),
    ///   - when the partner gets too close, too far or faces the spectator for a while, the view
    ///     jumps to a fresh spot behind them (instant relocations are comfortable, sliding is not).
    /// Pure presentation: reads the partner's avatar from NetworkManager.Rigs, changes nothing on
    /// the network. The match ends (game over / partner left) before anything needs restoring;
    /// End() exists for a future respawn.
    public class DeathSpectator : MonoBehaviour
    {
        [SerializeField] private float followDistance = 2.2f;
        [SerializeField] private float followHeight = 0.7f;
        [SerializeField] private float smoothing = 3f;
        [Tooltip("Fallback head height above the partner's avatar root, only if its head transform is missing.")]
        [SerializeField] private float partnerHeadHeight = 1.6f;
        [Tooltip("Flat eyes-to-partner distance (m) outside which the view jumps to a fresh spot behind them.")]
        [SerializeField] private float minDistance = 0.9f;
        [SerializeField] private float maxDistance = 4.5f;
        [Tooltip("Seconds the partner may face the spectator before the view jumps behind them again.")]
        [SerializeField] private float facingGrace = 2f;
        [SerializeField] private float reanchorCooldown = 1.5f;

        private AutoHandPlayer _player;
        private OpenXRHandPlayerControllerLink _input;   // same turn axis AutoHand reads
        private HeadPhysicsFollower _headFollower;
        private Rigidbody _headFollowerBody;             // its Rigidbody is internal to AutoHand's assembly
        private PlayerBelt _belt;
        private bool _beltWasEnabled, _headFollowerWasEnabled;
        private IPlayerState _me;
        private bool _spectating;
        private bool _prevUseMovement, _prevUseGrounding, _prevKinematic, _prevDetect;
        private bool _turnAxisReset = true;
        private readonly SpectatorFollow _follow = new SpectatorFollow();

        private void Awake()
        {
            _player = GetComponentInChildren<AutoHandPlayer>(true);
            _input = GetComponentInChildren<OpenXRHandPlayerControllerLink>(true);
            _belt = GetComponentInChildren<PlayerBelt>(true);
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
            // container is ours to place. useMovement=false also disables AutoHand's snap turn
            // (it pivots on the body, which stays where the player died) -- we do our own below.
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

            // The physical head (a rigidbody sphere chasing the camera) would travel with the
            // spectator and bump zombies and the partner. Park it. AutoHand creates it at runtime
            // ("Head Follower", sibling of the player), so it is looked up here, not in Awake.
            if (_headFollower == null)
            {
                _headFollower = GetComponentInChildren<HeadPhysicsFollower>(true);
                _headFollowerBody = _headFollower != null ? _headFollower.GetComponent<Rigidbody>() : null;
            }
            if (_headFollower != null)
            {
                _headFollowerWasEnabled = _headFollower.enabled;
                _headFollower.enabled = false;
                if (_headFollowerBody != null) { _headFollowerBody.isKinematic = true; _headFollowerBody.detectCollisions = false; }
            }

            // The belt follows the head: while spectating it would hang right under the camera,
            // grenades included (and the partner would see them float behind them). Leave it where
            // the player died.
            if (_belt != null) { _beltWasEnabled = _belt.enabled; _belt.enabled = false; }

            _follow.FollowDistance = followDistance;
            _follow.FollowHeight = followHeight;
            _follow.Smoothing = smoothing;
            _follow.MinDistance = minDistance;
            _follow.MaxDistance = maxDistance;
            _follow.FacingGrace = facingGrace;
            _follow.ReanchorCooldown = reanchorCooldown;
            _follow.Reset();               // first follow frame anchors behind the partner and jumps there
            _turnAxisReset = true;
            Debug.Log("[DeathSpectator] Local player died: following the partner.");
        }

        private void End()
        {
            if (!_spectating || _player == null) return;
            _spectating = false;
            _player.useMovement = _prevUseMovement;
            _player.useGrounding = _prevUseGrounding;
            if (_player.body != null) { _player.body.isKinematic = _prevKinematic; _player.body.detectCollisions = _prevDetect; }
            if (_headFollower != null)
            {
                if (_headFollowerBody != null) { _headFollowerBody.isKinematic = false; _headFollowerBody.detectCollisions = true; }
                _headFollower.enabled = _headFollowerWasEnabled;
            }
            if (_belt != null) _belt.enabled = _beltWasEnabled;
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

            var c = _player.trackingContainer;
            var cam = _player.headCamera.transform;

            UpdateSnapTurn(c, cam);

            if (!FindPartner(out var partnerBody, out var partnerHead)) return;   // partner gone: hold position; the match is ending anyway

            // Only the partner's translation moves the container. Rotating the container around
            // the eyes (snap turns, the re-orient after a jump) leaves the eyes where they are, so
            // the follow model never notices it.
            Vector3 delta = _follow.Step(partnerHead, partnerBody.forward, cam.position, Time.deltaTime, out bool jumped);
            c.position += delta;

            // After a jump the partner may be anywhere around us: turn the view to them once,
            // instantly, around the eyes (like a snap turn).
            if (jumped) FacePartner(c, cam, partnerHead);
        }

        /// The player's own snap turn, with AutoHand's settings (angle, deadzone, reset zone) and
        /// the same input action AutoHand reads, pivoting on the eyes instead of the parked body.
        private void UpdateSnapTurn(Transform container, Transform cam)
        {
            if (_input == null || _input.turnAxis.action == null) return;
            float axis = _input.turnAxis.action.ReadValue<Vector2>().x;

            if (Mathf.Abs(axis) < _player.turnResetzone) _turnAxisReset = true;
            if (!_turnAxisReset || Mathf.Abs(axis) < _player.turnDeadzone) return;

            container.RotateAround(cam.position, Vector3.up, axis > 0f ? _player.snapTurnAngle : -_player.snapTurnAngle);
            _turnAxisReset = false;
        }

        private static void FacePartner(Transform container, Transform cam, Vector3 partnerHead)
        {
            Vector3 toPartner = partnerHead - cam.position; toPartner.y = 0f;
            Vector3 look = cam.forward; look.y = 0f;
            if (toPartner.sqrMagnitude < 0.01f || look.sqrMagnitude < 0.001f) return;
            container.RotateAround(cam.position, Vector3.up, Vector3.SignedAngle(look, toPartner, Vector3.up));
        }

        /// The partner's replicated avatar (NetworkRig registered in NetworkManager.Rigs): its body
        /// (facing) and head (height). Cached, since the partner's avatar does not change mid-match.
        private NetworkRig _partnerRig;
        private Transform _partnerBody, _partnerHeadT;

        private bool FindPartner(out Transform body, out Vector3 head)
        {
            body = null; head = default;
            if (_partnerRig == null || _partnerRig.Object == null || !_partnerRig.Object.IsValid)
            {
                _partnerRig = null;
                var nm = NetworkManager.instance;
                if (nm == null || nm.runner == null) return false;
                foreach (var rig in nm.Rigs)
                {
                    if (rig == null || rig.Object == null || !rig.Object.IsValid) continue;
                    bool isLocal = rig.Object.StateAuthority == nm.runner.LocalPlayer || rig.Object.InputAuthority == nm.runner.LocalPlayer;
                    if (!isLocal) { _partnerRig = rig; break; }
                }
                if (_partnerRig == null) return false;
                _partnerBody = _partnerRig.transform.Find("HumanBody");
                _partnerHeadT = _partnerRig.transform.Find("Visuals/Head");
            }
            body = _partnerBody != null ? _partnerBody : _partnerRig.transform;
            head = _partnerHeadT != null ? _partnerHeadT.position : _partnerRig.transform.position + Vector3.up * partnerHeadHeight;
            return true;
        }
    }
}
