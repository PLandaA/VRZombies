using UnityEngine;
using Fusion;

/// Networked full-body avatar driver: camera-anchored body, CALIBRATED (not live) height scale,
/// and replicated IK targets. The local client drives everything in Update() at full framerate;
/// the NetworkTransforms on the body/targets have DisableSharedModeInterpolation enabled so
/// Fusion's tick-rate render interpolation never fights these live writes (official Photon
/// guidance: disable interpolation when controller code moves the object in Update()).
[DefaultExecutionOrder(9999)]
public class NetworkRig : NetworkBehaviour
{
    [System.Serializable]
    public struct IKTarget
    {
        [Tooltip("IK constraint target transform (Unity Animation Rigging)")]
        public Transform targetTransform;

        [Tooltip("Optional position offset (useful to adjust the model pivot)")]
        public Vector3 positionOffset;

        [Tooltip("Rotation offset in euler angles (useful if the model has a different base rotation)")]
        public Vector3 rotationOffset;

        public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
        {
            if (targetTransform == null) return;
            // positionOffset is applied in the TRACKED SPACE (rotates with the hand/head),
            // so e.g. (0,0,-0.075) always means "7.5cm back toward the wrist" regardless of pose.
            targetTransform.SetPositionAndRotation(
                position + rotation * positionOffset,
                rotation * Quaternion.Euler(rotationOffset)
            );
        }
    }

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
    private float _calibratedScale = -1f;   // < 0 means "not calibrated yet"
    private float _scaleVelocity;

    private Vector3 _smoothHandRPos, _smoothHandLPos;
    private Quaternion _smoothHandRRot = Quaternion.identity, _smoothHandLRot = Quaternion.identity;
    private bool _handSmoothingPrimed;

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

        base.Spawned();
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
        if (handSmoothing > 0.0001f && hasRight && hasLeft)
        {
            if (!_handSmoothingPrimed)
            {
                _smoothHandRPos = hrPos; _smoothHandRRot = hrRot;
                _smoothHandLPos = hlPos; _smoothHandLRot = hlRot;
                _handSmoothingPrimed = true;
            }
            float k = 1f - Mathf.Exp(-Time.deltaTime / handSmoothing);
            _smoothHandRPos = Vector3.Lerp(_smoothHandRPos, hrPos, k);
            _smoothHandRRot = Quaternion.Slerp(_smoothHandRRot, hrRot, k);
            _smoothHandLPos = Vector3.Lerp(_smoothHandLPos, hlPos, k);
            _smoothHandLRot = Quaternion.Slerp(_smoothHandLRot, hlRot, k);
            hrPos = _smoothHandRPos; hrRot = _smoothHandRRot;
            hlPos = _smoothHandLPos; hlRot = _smoothHandLRot;
        }

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

#if UNITY_EDITOR
    /// RESEARCH STAGE 1: live wrist-offset tuner (the canonical "eyeball it in play mode"
    /// method, with power tools). Editor-only. Keys: I/K pitch, J/L yaw, U/O roll (+-2 deg) on
    /// the RIGHT hand -- hold LeftShift for the LEFT hand. -/= adjusts arm length +-2%.
    /// P prints all current values so the winners can be baked into the prefab.
    private void LiveTuner()
    {
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        bool leftHand = kb.leftShiftKey.isPressed;

        void Nudge(Vector3 delta)
        {
            if (leftHand) handLeftTarget.rotationOffset += delta;
            else handRightTarget.rotationOffset += delta;
            Debug.Log("[RigTuner] " + (leftHand ? "L" : "R") + " rotOffset -> " +
                (leftHand ? handLeftTarget.rotationOffset : handRightTarget.rotationOffset).ToString("F1"));
        }

        if (kb.iKey.wasPressedThisFrame) Nudge(new Vector3(+2f, 0f, 0f));
        if (kb.kKey.wasPressedThisFrame) Nudge(new Vector3(-2f, 0f, 0f));
        if (kb.jKey.wasPressedThisFrame) Nudge(new Vector3(0f, -2f, 0f));
        if (kb.lKey.wasPressedThisFrame) Nudge(new Vector3(0f, +2f, 0f));
        if (kb.uKey.wasPressedThisFrame) Nudge(new Vector3(0f, 0f, -2f));
        if (kb.oKey.wasPressedThisFrame) Nudge(new Vector3(0f, 0f, +2f));

        // POSITION offset (the lever that actually moved the needle): arrows = X/Y, PgUp/PgDn = Z, 1cm steps
        void NudgePos(Vector3 delta)
        {
            if (leftHand) handLeftTarget.positionOffset += delta;
            else handRightTarget.positionOffset += delta;
            Debug.Log("[RigTuner] " + (leftHand ? "L" : "R") + " posOffset -> " +
                (leftHand ? handLeftTarget.positionOffset : handRightTarget.positionOffset).ToString("F3"));
        }
        if (kb.leftArrowKey.wasPressedThisFrame) NudgePos(new Vector3(-0.01f, 0f, 0f));
        if (kb.rightArrowKey.wasPressedThisFrame) NudgePos(new Vector3(+0.01f, 0f, 0f));
        if (kb.upArrowKey.wasPressedThisFrame) NudgePos(new Vector3(0f, +0.01f, 0f));
        if (kb.downArrowKey.wasPressedThisFrame) NudgePos(new Vector3(0f, -0.01f, 0f));
        if (kb.pageUpKey.wasPressedThisFrame) NudgePos(new Vector3(0f, 0f, +0.01f));
        if (kb.pageDownKey.wasPressedThisFrame) NudgePos(new Vector3(0f, 0f, -0.01f));

        // ELBOW hint offset (shared by both arms, mirrored internally): T/G out, Y/H down, R/F back
        void NudgeElbow(Vector3 delta)
        {
            elbowHintOffset += delta;
            Debug.Log("[RigTuner] elbowHintOffset -> " + elbowHintOffset.ToString("F2"));
        }
        if (kb.tKey.wasPressedThisFrame) NudgeElbow(new Vector3(+0.02f, 0f, 0f));
        if (kb.gKey.wasPressedThisFrame) NudgeElbow(new Vector3(-0.02f, 0f, 0f));
        if (kb.yKey.wasPressedThisFrame) NudgeElbow(new Vector3(0f, +0.02f, 0f));
        if (kb.hKey.wasPressedThisFrame) NudgeElbow(new Vector3(0f, -0.02f, 0f));
        if (kb.rKey.wasPressedThisFrame) NudgeElbow(new Vector3(0f, 0f, +0.02f));
        if (kb.fKey.wasPressedThisFrame) NudgeElbow(new Vector3(0f, 0f, -0.02f));

        if (kb.minusKey.wasPressedThisFrame || kb.equalsKey.wasPressedThisFrame)
        {
            armLengthScale = Mathf.Clamp(armLengthScale + (kb.equalsKey.wasPressedThisFrame ? 0.02f : -0.02f), 0.7f, 1.4f);
            Debug.Log("[RigTuner] armLengthScale -> " + armLengthScale.ToString("F2"));
        }

        if (kb.pKey.wasPressedThisFrame)
            Debug.Log("[RigTuner] ==== ALL VALUES ====" +
                      "\n rotOffsetR=" + handRightTarget.rotationOffset.ToString("F1") +
                      "  rotOffsetL=" + handLeftTarget.rotationOffset.ToString("F1") +
                      "\n posOffsetR=" + handRightTarget.positionOffset.ToString("F3") +
                      "  posOffsetL=" + handLeftTarget.positionOffset.ToString("F3") +
                      "\n elbowHintOffset=" + elbowHintOffset.ToString("F2") +
                      "  armLengthScale=" + armLengthScale.ToString("F2"));
    }
#endif

    /// Places each TwoBoneIK hint so the elbow hangs below and slightly behind the line from
    /// shoulder to hand. A hint parented statically to the chest (the prefab default) points
    /// the elbow the same way no matter where the hand is, which is what reads as robotic.
    private void UpdateElbowHints()
    {
        if (body == null) return;
        float scale = body.localScale.x <= 0.01f ? 1f : body.localScale.x;
        float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, elbowSmoothing));

        AimElbow(rightShoulderBone, handRightTarget.targetTransform, rightElbowHint, 1f, scale, k);
        AimElbow(leftShoulderBone, handLeftTarget.targetTransform, leftElbowHint, -1f, scale, k);
    }

    private void AimElbow(Transform shoulder, Transform handTarget, Transform hint, float side, float scale, float k)
    {
        if (shoulder == null || handTarget == null || hint == null) return;

        Vector3 shoulderPos = shoulder.position;
        Vector3 handPos = handTarget.position;
        Vector3 axis = handPos - shoulderPos;
        float armLen = axis.magnitude;
        if (armLen < 0.05f) return;
        axis /= armLen;

        // The pole (bend direction) must stay PERPENDICULAR to the shoulder->hand axis, or
        // deep folds (hand pulled to the chest) leave the solver without a valid bend plane
        // and the forearm slices through the hand. Project the body-space bias onto the
        // perpendicular plane so the elbow keeps pointing out-down-back at ANY fold depth.
        Vector3 bias =
            body.right * (elbowHintOffset.x * side) +
            Vector3.up * elbowHintOffset.y +
            body.forward * elbowHintOffset.z;
        Vector3 pole = Vector3.ProjectOnPlane(bias, axis);
        if (pole.sqrMagnitude < 0.0004f)
            pole = Vector3.ProjectOnPlane(body.right * side - body.forward, axis);
        pole.Normalize();

        Vector3 goal = Vector3.Lerp(shoulderPos, handPos, 0.5f) + pole * (0.35f * scale);
        hint.position = Vector3.Lerp(hint.position, goal, k);
    }

    private static readonly RaycastHit[] _floorHits = new RaycastHit[16];
    private static int _floorMask = -1;
    private float _smoothFloorY = float.MinValue;

    /// Highest real ground under the player, SMOOTHED and REMEMBERED. Hand/grab layers are
    /// masked out of the ray: with a rifle in hand the ray column crossed dozens of hand and
    /// weapon colliders, overflowed the hit buffer, dropped the ground, and fell back to the
    /// player transform (~1m off) for a frame -- the body popped in sync with every step.
    private float SampleFloorHeight(Vector3 from)
    {
        if (_floorMask < 0)
        {
            int mask = ~0;
            foreach (var ln in new[] { "Hand", "Grabbing", "Grabbable", "HandPlayer" })
            {
                int l = LayerMask.NameToLayer(ln);
                if (l >= 0) mask &= ~(1 << l);
            }
            _floorMask = mask;
        }

        int n = Physics.RaycastNonAlloc(from, Vector3.down, _floorHits, 6f, _floorMask, QueryTriggerInteraction.Ignore);
        float best = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var h = _floorHits[i];
            if (h.collider == null) continue;
            if (h.collider.GetComponentInParent<Autohand.AutoHandPlayer>() != null) continue;
            if (h.collider.GetComponentInParent<NetworkRig>() != null) continue;
            if (h.point.y > best) best = h.point.y;
        }

        if (best > float.MinValue)
        {
            // Terrain bumps and prop edges must not twitch the body: settle toward the new
            // ground over ~0.15s. First sample snaps.
            if (_smoothFloorY <= float.MinValue) _smoothFloorY = best;
            else _smoothFloorY = Mathf.Lerp(_smoothFloorY, best, 1f - Mathf.Exp(-Time.deltaTime / 0.15f));
        }
        // No ground this frame (buffer overflow, gap): keep the last good value instead of
        // falling back to a poisoned reference.
        return _smoothFloorY;
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

        // THE FLOOR TRUTH: the AutoHandPlayer transform is NOT at ground level (the rig root
        // is authored ~1m up), so using it as floor reference poisoned everything downstream:
        // head height read ~0.6m -> scale never calibrated -> body anchored a meter under the
        // visible floor (the sunken feet), and clamping that up crushed the arms' reach.
        // The raycast ground is the real reference for the WHOLE height pipeline.
        float sampledGround = SampleFloorHeight(headPos);
        if (sampledGround > float.MinValue) floorY = sampledGround;

        float modelHeadHeight = Mathf.Max(0.1f, -headBodyPositionOffset.y);
        float headH = headPos.y - floorY;

        float scale;
        float bodyY;
        if (headH < neutralPoseHeight)
        {
            // Headset resting on a desk: neutral standing pose, keep whatever scale we had.
            scale = _calibratedScale > 0f ? _calibratedScale : 1f;
            headPos.y = floorY + modelHeadHeight * scale;
            bodyY = floorY;
        }
        else
        {
            // CALIBRATED scale: adjusts slowly and only while standing tall enough.
            // Crouching or tracking dips freeze it -> the body ducks (Y follows the head)
            // but NEVER shrinks. This removes the "suddenly tiny avatar" artifact.
            if (headH >= minScaleCalibrationHeight)
            {
                float targetScale = Mathf.Clamp(headH / modelHeadHeight, 0.55f, 1.3f);
                if (_calibratedScale <= 0f)
                    _calibratedScale = targetScale;   // first valid reading: snap
                else
                    _calibratedScale = Mathf.SmoothDamp(_calibratedScale, targetScale, ref _scaleVelocity, scaleSmoothTime, Mathf.Infinity, dt);
            }
            scale = _calibratedScale > 0f ? _calibratedScale : 1f;
            bodyY = headPos.y - modelHeadHeight * scale;
        }

        // FLOOR CLAMP (safety net): with the real ground as reference the calibration should
        // land feet exactly on the floor; this only catches transient mismatches.
        bodyY = Mathf.Max(bodyY, floorY);

        headTarget.SetPositionAndRotation(headPos, headRot);
        if (applyRight) handRightTarget.SetPositionAndRotation(handRPos, handRRot);
        if (applyLeft) handLeftTarget.SetPositionAndRotation(handLPos, handLRot);

        float sceneBoost = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex >= 1 ? gameSceneScaleBoost : 1f;
        body.localScale = Vector3.one * scale * sceneBoost;

        Vector3 yawFwd = Quaternion.Euler(0f, headRot.eulerAngles.y, 0f) * Vector3.forward;
        body.position = new Vector3(
            headPos.x + headBodyPositionOffset.x - yawFwd.x * bodySpineOffset,
            bodyY,
            headPos.z + headBodyPositionOffset.z - yawFwd.z * bodySpineOffset);

        // Framerate-independent yaw smoothing (exponential damp).
        float t = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, bodyRotateSmoothness));
        body.rotation = Quaternion.Slerp(
            body.rotation,
            Quaternion.Euler(body.rotation.eulerAngles.x, headRot.eulerAngles.y, body.rotation.eulerAngles.z),
            t);
    }
}
