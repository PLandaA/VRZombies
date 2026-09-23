using UnityEngine;
using Fusion;
using VRZ.Core;
using VRZ.Network;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.FX;
using VRZ.World;

namespace VRZ.Player
{

    /// Networked full-body avatar driver (orchestration only). Owns the network lifecycle, picks
    /// the tracking SOURCES for each target, and runs the single pose pipeline; the actual
    /// sub-problems live in small focused helpers:
    ///   FloorSampler           - where the real ground is (masked, buffered, smoothed)
    ///   AvatarScaleCalibrator  - player-height scale + body root height (pure logic)
    ///   PoseSmoother           - hand buzz filter (pure logic)
    ///   ElbowPole              - elbow bend direction geometry
    ///   NetworkRig.LiveTuner   - editor-only calibration keys (partial)
    /// The local client drives everything in LateUpdate() at full framerate and solves VRIK
    /// explicitly; proxies solve VRIK from the replicated targets. NetworkTransforms disable
    /// Fusion interpolation on the owner only (see Spawned).
    [DefaultExecutionOrder(9999)]
    public partial class NetworkRig : NetworkBehaviour
    {
        [Header("Character Root")]
        [Tooltip("NetworkCharacter root transform. Follows the local player's physical body position (AutoHandPlayer).")]
        [SerializeField] private Transform character;

        [Header("IK Targets")]
        [SerializeField] private IKTarget headTarget;
        [SerializeField] private IKTarget handRightTarget;
        [SerializeField] private IKTarget handLeftTarget;

        [Header("Body Visual")]
        [Tooltip("Avatar body transform. Anchored under the head, smoothly yawing toward where the player looks.")]
        [SerializeField] private Transform body;

        [Tooltip("Body offset relative to the head. Y must be minus the model head height (e.g. 0, -1.55, 0).")]
        [SerializeField] private Vector3 headBodyPositionOffset = new Vector3(0f, -1.55f, 0f);

        [Tooltip("Body yaw smoothing time in seconds (0.1 = snappy, 0.3 = lazy). Framerate independent.")]
        [Range(0.01f, 1f)]
        [SerializeField] private float bodyRotateSmoothness = 0.1f;

        [Tooltip("Vertical offset from the eyes (camera) down to the model's head bone pivot.")]
        [SerializeField] private float eyeToHeadBoneOffset = 0.16f;

        [Tooltip("Compensates a miscalibrated tracking floor (headset reporting lower than reality). Standing, tune until scale reads ~1.00.")]
        [SerializeField] private float trackingHeightOffset = 0f;

        [Tooltip("Extra avatar scale applied only in the game scene (build index >= 1).")]
        [SerializeField] private float gameSceneScaleBoost = 1f;

        [Header("Scale Calibration")]
        [Tooltip("Pushes the body slightly behind the view direction so the chest never clips into the camera.")]
        [SerializeField] private float bodySpineOffset = 0.14f;

        [Tooltip("Minimum head height for the scale to (re)calibrate. Below this (crouching, tracking dips) the scale FREEZES instead of shrinking the avatar.")]
        [SerializeField] private float minScaleCalibrationHeight = 1.2f;

        [Tooltip("Seconds for calibrated scale adjustments (slow = imperceptible, no popping).")]
        [SerializeField] private float scaleSmoothTime = 2f;

        [Tooltip("Below this head height the avatar shows a neutral standing pose (headset resting on a desk).")]
        [SerializeField] private float neutralPoseHeight = 0.35f;

        [Header("Natural Arms")]
        [Tooltip("Upper-arm bones (mixamorig8:RightArm / LeftArm). Used to aim the elbows.")]
        [SerializeField] private Transform rightShoulderBone;
        [SerializeField] private Transform leftShoulderBone;

        [Tooltip("TwoBoneIK hint transforms (RightArmIK_hint / LeftArmIK_hint).")]
        [SerializeField] private Transform rightElbowHint;
        [SerializeField] private Transform leftElbowHint;

        [Tooltip("Where the elbow hangs, in body space, relative to the shoulder-to-hand midpoint. " +
                 "X = outward, Y = down, Z = back. Mirrored automatically for the left arm.")]
        [SerializeField] private Vector3 elbowHintOffset = new Vector3(0.22f, -0.5f, -0.18f);

        [Tooltip("Elbow settle time in seconds. Small values snap, larger ones glide.")]
        [Range(0.01f, 0.4f)]
        [SerializeField] private float elbowSmoothing = 0.08f;

        [Tooltip("Smoothing applied to the LOCAL hand targets in seconds. AutoHand's physical hands " +
                 "vibrate against colliders and the IK copies that faithfully; 0.03 removes the buzz " +
                 "without any felt lag. Set to 0 for a raw 1:1 match.")]
        [Range(0f, 0.15f)]
        [SerializeField] private float handSmoothing = 0.03f;

        [Header("Arm Length (research stage 2)")]
        [Tooltip("Scales the upper-arm bones so the avatar's reach matches the player's real arms. " +
                 "Height-only calibration cannot capture arm span (VRChat exposes the same knob). " +
                 ">1 = longer arms. Tune live with -/= keys in the editor, P prints values.")]
        [Range(0.7f, 1.4f)]
        [SerializeField] private float armLengthScale = 1f;
        private float _appliedArmScale = -1f;

        [Header("Visual Anchors")]
        [Tooltip("Visuals/RightHand, LeftHand and Head: the networked anchors carrying the avatar " +
                 "hand meshes. NOBODY drove them (they floated at prefab-local offsets); the rig " +
                 "writes them with the RAW tracked poses so the visible hands sit exactly on the " +
                 "player's real hands, while the arm IK targets keep their wrist offsets.")]
        [SerializeField] private Transform handRightVisual;
        [SerializeField] private Transform handLeftVisual;
        [SerializeField] private Transform headVisual;

        private Autohand.AutoHandPlayer _localPlayer;
        private RootMotion.FinalIK.VRIK _vrik;

        // Helpers are created lazily on first use rather than by field initializers: a woven
        // NetworkBehaviour instance was observed with its initializers skipped (nulls at runtime),
        // and lazy creation is immune to whatever the spawn path does with constructors.
        private FloorSampler _floor;
        private PoseSmoother _smoothRight, _smoothLeft;
        private AvatarScaleCalibrator _calibrator;

        // The rig knows what is NOT ground (its own body + the AutoHand player capsule); the sampler doesn't.
        private FloorSampler Floor => _floor ??= new FloorSampler(IsOwnBodyCollider);
        private static bool IsOwnBodyCollider(Collider c) =>
            c.GetComponentInParent<Autohand.AutoHandPlayer>() != null ||
            c.GetComponentInParent<NetworkRig>() != null;
        private PoseSmoother SmoothRight => _smoothRight ??= new PoseSmoother();
        private PoseSmoother SmoothLeft => _smoothLeft ??= new PoseSmoother();
        private AvatarScaleCalibrator Calibrator =>
            _calibrator ??= new AvatarScaleCalibrator(-headBodyPositionOffset.y, minScaleCalibrationHeight, scaleSmoothTime, neutralPoseHeight);

        public override void Spawned()
        {
            // Interpolation must be decided PER CLIENT, not per prefab. The owner writes these
            // transforms live in Update(), so Fusion's interpolation would fight it -> disabled.
            // Proxies receive nothing but tick snapshots, so without interpolation the IK targets
            // step at ~30hz and the arms jitter -> enabled. One flag, opposite answers.
            bool isOwner = Object != null && Object.HasStateAuthority;
            foreach (var nt in GetComponentsInChildren<NetworkTransform>(true))
                nt.DisableSharedModeInterpolation = isOwner;

            // VRIK (Final IK) writes bone transforms directly in LateUpdate -- no playable stream
            // handles to go stale on Fusion's spawn choreography. It initiates itself on its first
            // Update, sampling the prefab rest pose. We only cache it and log.
            _vrik = GetComponentInChildren<RootMotion.FinalIK.VRIK>(true);
            if (_vrik == null)
                Debug.LogWarning("[NetworkRig] No VRIK component found on the avatar!");
            // Debt D3: register so zombies/grenades never scan the scene for avatars. Registry
            // only; does not touch the pose pipeline (the tick layer below is the committed one).
            if (VRZ.Network.NetworkManager.instance != null)
                VRZ.Network.NetworkManager.instance.RegisterRig(this);

            base.Spawned();
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            if (VRZ.Network.NetworkManager.instance != null)
                VRZ.Network.NetworkManager.instance.UnregisterRig(this);
            base.Despawned(runner, hasState);
        }

        public override void FixedUpdateNetwork()
        {
            // Tick-aligned pose from the replicated input struct. Only yields input on the
            // owning client; remote clients render purely from interpolated NetworkTransforms.
            if (GetInput<CharacterInputData>(out var inputData))
            {
                character.SetPositionAndRotation(inputData.characterPosition, inputData.characterRotation);

                ApplyPose(
                    inputData.headPosition, inputData.headRotation,
                    inputData.handRightPosition, inputData.handRightRotation,
                    inputData.handLeftPosition, inputData.handLeftRotation,
                    inputData.characterPosition.y, Runner.DeltaTime);
            }
            base.FixedUpdateNetwork();
        }

        /// LATE update, deliberately: AutoHandPlayer moves the tracking container (camera + hand
        /// follow targets) in ITS LateUpdate (order 1). Reading the camera in Update fed the rig a
        /// frame-old position -- while walking, physics advances 0/1/2 steps per frame so that lag
        /// fluctuated and the body/arms juddered against a smooth world. Order 9999 puts us after
        /// AutoHand; VRIK is then solved explicitly with fresh targets (see SolveIK).
        private void LateUpdate()
        {
            if (Object == null || !Object.IsValid) return;

            // Elbows are solved LOCALLY on every client (the hints are not networked), so this
            // must run for proxies too -- otherwise the partner's elbows stay frozen in the pose
            // the prefab shipped with.
            UpdateElbowHints();

            // Local live layer: runs at full framerate AFTER Fusion's update (execution order
            // 9999) and BEFORE Animation Rigging evaluates, feeding the IK fresh tracking data
            // every frame. This is what makes the arms as smooth as the AutoHand hands: the
            // avatar reads the same 72-120hz tracking they do, instead of 32hz network ticks.
            // Arm-length scale applies on EVERY client (the partner's avatar needs the same reach).
            ApplyArmScale();

            if (!Object.HasStateAuthority) { SolveIK(); return; }
            if (_localPlayer == null)
            {
                _localPlayer = FindFirstObjectByType<Autohand.AutoHandPlayer>();
                if (_localPlayer == null || _localPlayer.headCamera == null) return;
            }

            // RESEARCH STAGE 3 -- dual source (AutoHand's own documented VRIK pattern): the ARM IK
            // reads the CONTROLLER frame (hand.follow: zero physics lag or overshoot), while the
            // visible hand meshes read the PHYSICS hands (what the player actually sees and grabs
            // with). Physics wobble stops reaching the arm; grab fidelity stays intact.
            bool hasRight = _localPlayer.handRight != null;
            bool hasLeft = _localPlayer.handLeft != null;

            Vector3 physRPos = Vector3.zero, physLPos = Vector3.zero;
            Quaternion physRRot = Quaternion.identity, physLRot = Quaternion.identity;
            if (hasRight) { physRPos = _localPlayer.handRight.transform.position; physRRot = _localPlayer.handRight.transform.rotation; }
            if (hasLeft) { physLPos = _localPlayer.handLeft.transform.position; physLRot = _localPlayer.handLeft.transform.rotation; }

            // Borrowed from AutoHand's own AutoHandVRIK: WHILE HOLDING, the physical hand sits at
            // the object's grab pose -- which can be several cm from the controller frame. Aiming
            // the arm at the controller then would leave the sleeve floating off the hand exactly
            // when it matters most (rifle in hand 90% of the game). So: holding -> the hand's grab
            // point is the truth; free -> the controller frame (stable, lag-free).
            bool holdR = hasRight && _localPlayer.handRight.holdingObj != null && _localPlayer.handRight.handGrabPoint != null;
            bool holdL = hasLeft && _localPlayer.handLeft.holdingObj != null && _localPlayer.handLeft.handGrabPoint != null;
            Transform followR = hasRight ? _localPlayer.handRight.follow : null;
            Transform followL = hasLeft ? _localPlayer.handLeft.follow : null;
            Vector3 hrPos = holdR ? physRPos : (followR != null ? followR.position : physRPos);
            Quaternion hrRot = holdR ? physRRot : (followR != null ? followR.rotation : physRRot);
            Vector3 hlPos = holdL ? physLPos : (followL != null ? followL.position : physLPos);
            Quaternion hlRot = holdL ? physLRot : (followL != null ? followL.rotation : physLRot);

            // Physical hands buzz when they press against geometry; a tiny exponential filter
            // takes the buzz out of the arm without any perceptible lag behind the real hand.
            if (hasRight) SmoothRight.Filter(ref hrPos, ref hrRot, handSmoothing, Time.deltaTime);
            if (hasLeft) SmoothLeft.Filter(ref hlPos, ref hlRot, handSmoothing, Time.deltaTime);

            ApplyPose(
                _localPlayer.headCamera.transform.position, _localPlayer.headCamera.transform.rotation,
                hrPos, hrRot, hlPos, hlRot,
                _localPlayer.transform.position.y, Time.deltaTime,
                hasRight, hasLeft);

            // Visual anchors: RAW physics poses -- the avatar hand meshes sit 1:1 on the hands the
            // player actually sees, decoupled from the (controller-driven) arm targets above.
            if (hasRight && handRightVisual != null) handRightVisual.SetPositionAndRotation(physRPos, physRRot);
            if (hasLeft && handLeftVisual != null) handLeftVisual.SetPositionAndRotation(physLPos, physLRot);
            if (headVisual != null) headVisual.SetPositionAndRotation(
                _localPlayer.headCamera.transform.position, _localPlayer.headCamera.transform.rotation);

            SolveIK();

    #if UNITY_EDITOR
            LiveTuner();
    #endif
        }

        /// Solves VRIK NOW, with the targets written this frame. UpdateSolverExternal is Final IK's
        /// official hook for driving the solve from another script; it also flags the component to
        /// skip its own LateUpdate solve so we never pay for two per frame.
        private void SolveIK()
        {
            if (_vrik != null) _vrik.UpdateSolverExternal();
        }

        /// RESEARCH STAGE 2 via VRIK: arm reach calibration through the solver's own
        /// armLengthMlp (it displaces forearm+hand outward -- what the bone-scale hack emulated).
        /// VRIK's default stretchCurve adds a little extra reach only at full extension.
        private void ApplyArmScale()
        {
            if (_vrik == null || Mathf.Approximately(_appliedArmScale, armLengthScale)) return;
            _vrik.solver.rightArm.armLengthMlp = armLengthScale;
            _vrik.solver.leftArm.armLengthMlp = armLengthScale;
            _appliedArmScale = armLengthScale;
        }

        /// Elbow hints are solved LOCALLY on every client (they are not networked).
        private void UpdateElbowHints()
        {
            if (body == null) return;
            float scale = body.localScale.x <= 0.01f ? 1f : body.localScale.x;
            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, elbowSmoothing));

            ElbowPole.Aim(rightShoulderBone, handRightTarget.targetTransform, rightElbowHint, body, elbowHintOffset, +1f, scale, k);
            ElbowPole.Aim(leftShoulderBone, handLeftTarget.targetTransform, leftElbowHint, body, elbowHintOffset, -1f, scale, k);
        }

        /// Single pose pipeline shared by the tick layer and the live layer.
        private void ApplyPose(
            Vector3 rawHeadPos, Quaternion headRot,
            Vector3 handRPos, Quaternion handRRot,
            Vector3 handLPos, Quaternion handLRot,
            float floorY, float dt,
            bool applyRight = true, bool applyLeft = true)
        {
            Vector3 headPos = rawHeadPos;
            headPos.y += trackingHeightOffset;
            headPos.y -= eyeToHeadBoneOffset;

            // The AutoHandPlayer transform is NOT at ground level (the rig root is authored ~1m up);
            // the raycast ground is the real reference for the whole height pipeline.
            float sampledGround = Floor.Sample(headPos, dt);
            if (sampledGround > float.MinValue) floorY = sampledGround;

            var fit = Calibrator.Evaluate(headPos.y, floorY, dt);
            headPos.y = fit.HeadY;

            headTarget.SetPositionAndRotation(headPos, headRot);
            if (applyRight) handRightTarget.SetPositionAndRotation(handRPos, handRRot);
            if (applyLeft) handLeftTarget.SetPositionAndRotation(handLPos, handLRot);

            float sceneBoost = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex >= 1 ? gameSceneScaleBoost : 1f;
            body.localScale = Vector3.one * fit.Scale * sceneBoost;

            // Body anchored under the head, pushed slightly behind the view direction
            Vector3 yawFwd = Quaternion.Euler(0f, headRot.eulerAngles.y, 0f) * Vector3.forward;
            body.position = new Vector3(
                headPos.x + headBodyPositionOffset.x - yawFwd.x * bodySpineOffset,
                fit.BodyY,
                headPos.z + headBodyPositionOffset.z - yawFwd.z * bodySpineOffset);

            // Framerate-independent yaw smoothing (exponential damp).
            float t = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, bodyRotateSmoothness));
            body.rotation = Quaternion.Slerp(
                body.rotation,
                Quaternion.Euler(body.rotation.eulerAngles.x, headRot.eulerAngles.y, body.rotation.eulerAngles.z),
                t);
        }
    }
}
