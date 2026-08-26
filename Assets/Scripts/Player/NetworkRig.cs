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

    private Autohand.AutoHandPlayer _localPlayer;
    private float _calibratedScale = -1f;   // < 0 means "not calibrated yet"
    private float _scaleVelocity;

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

    private void Update()
    {
        // Local live layer: runs at full framerate AFTER Fusion's update (execution order
        // 9999) and BEFORE Animation Rigging evaluates, feeding the IK fresh tracking data
        // every frame. This is what makes the arms as smooth as the AutoHand hands: the
        // avatar reads the same 72-120hz tracking they do, instead of 32hz network ticks.
        if (Object == null || !Object.IsValid || !Object.HasStateAuthority) return;
        if (_localPlayer == null)
        {
            _localPlayer = FindFirstObjectByType<Autohand.AutoHandPlayer>();
            if (_localPlayer == null || _localPlayer.headCamera == null) return;
        }

        Vector3 hrPos = Vector3.zero, hlPos = Vector3.zero;
        Quaternion hrRot = Quaternion.identity, hlRot = Quaternion.identity;
        bool hasRight = _localPlayer.handRight != null;
        bool hasLeft = _localPlayer.handLeft != null;
        if (hasRight) { hrPos = _localPlayer.handRight.transform.position; hrRot = _localPlayer.handRight.transform.rotation; }
        if (hasLeft) { hlPos = _localPlayer.handLeft.transform.position; hlRot = _localPlayer.handLeft.transform.rotation; }

        ApplyPose(
            _localPlayer.headCamera.transform.position, _localPlayer.headCamera.transform.rotation,
            hrPos, hrRot, hlPos, hlRot,
            _localPlayer.transform.position.y, Time.deltaTime,
            hasRight, hasLeft);
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
