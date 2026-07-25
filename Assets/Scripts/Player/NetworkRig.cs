using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Fusion;

/// Networked full-body avatar driver: camera-anchored body, player-height scale calibration and replicated IK targets. Heavily rewritten from the course base.
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
            targetTransform.SetPositionAndRotation(
                position + positionOffset,
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

    [Tooltip("Body rotation smoothing (0 = instant, 1 = never rotates). Recommended: 0.1")]
    [Range(0f, 1f)]
    [SerializeField] private float bodyRotateSmoothness = 0.1f;

    [Tooltip("Vertical offset from the eyes (camera) down to the model's head bone pivot.")]
    [SerializeField] private float eyeToHeadBoneOffset = 0.16f;

    [Tooltip("Compensates a miscalibrated tracking floor (headset reporting lower than reality). Standing, tune until scale reads ~1.00.")]
    [SerializeField] private float trackingHeightOffset = 0.5f;

    private Autohand.AutoHandPlayer _localPlayer;



        public override void FixedUpdateNetwork()
    {
        if (GetInput<CharacterInputData>(out var inputData))
        {
            character.SetPositionAndRotation(inputData.characterPosition, inputData.characterRotation);

            headTarget.SetPositionAndRotation(inputData.headPosition, inputData.headRotation);
            handRightTarget.SetPositionAndRotation(inputData.handRightPosition, inputData.handRightRotation);
            handLeftTarget.SetPositionAndRotation(inputData.handLeftPosition, inputData.handLeftRotation);

            float floorY = inputData.characterPosition.y;
            Vector3 headPos = headTarget.targetTransform.position;
            headPos.y += trackingHeightOffset;
            headPos.y -= eyeToHeadBoneOffset;
            headTarget.targetTransform.position = headPos;
            float modelHeadHeight = Mathf.Max(0.1f, -headBodyPositionOffset.y);
            float headH = headPos.y - floorY;

            // Dual mode: neutral standing pose when tracking is implausible (headset
            // resting on a desk), camera-anchored scaled body when tracking is real.
            float scale;
            float bodyY;
            if (headH < 0.9f)
            {
                scale = 1f;
                headPos.y = floorY + modelHeadHeight;
                headTarget.targetTransform.position = headPos;
                bodyY = floorY;
            }
            else
            {
                scale = Mathf.Clamp(headH / modelHeadHeight, 0.55f, 1.3f);
                bodyY = headPos.y - modelHeadHeight * scale;
            }

            body.localScale = Vector3.one * scale;
            body.position = new Vector3(headPos.x + headBodyPositionOffset.x, bodyY, headPos.z + headBodyPositionOffset.z);
            body.rotation = Quaternion.Lerp(body.rotation, Quaternion.Euler(body.rotation.x, inputData.headRotation.eulerAngles.y, body.rotation.z), bodyRotateSmoothness);

        }
        base.FixedUpdateNetwork();
    }

    private void LateUpdate()
    {
        return; // DISABLED: Animation Rigging evaluates before LateUpdate, so this live layer fought the IK and caused arm jitter. Revisit with proper script execution order.
        // Local-only smoothing layer: FixedUpdateNetwork runs at tick rate (32hz),
        // which makes the body visually lag behind the full-framerate AutoHand hands
        // during locomotion. Here the LOCAL client repositions the body and IK
        // targets every frame from live tracking (runs after NetworkTransform's
        // interpolated Render, so it wins visually). Remote clients keep normal
        // interpolation and the networked state written in FUN is unaffected.
        if (Object == null || !Object.IsValid || !Object.HasStateAuthority) return;
        if (_localPlayer == null)
        {
            _localPlayer = FindFirstObjectByType<Autohand.AutoHandPlayer>();
            if (_localPlayer == null || _localPlayer.headCamera == null) return;
        }

        float floorY = _localPlayer.transform.position.y;
        Vector3 headPos = _localPlayer.headCamera.transform.position;
        Quaternion headRot = _localPlayer.headCamera.transform.rotation;
        headPos.y += trackingHeightOffset;
        headPos.y -= eyeToHeadBoneOffset;
        float modelHeadHeight = Mathf.Max(0.1f, -headBodyPositionOffset.y);
        float headH = headPos.y - floorY;

        float scale;
        float bodyY;
        if (headH < 0.9f)
        {
            scale = 1f;
            headPos.y = floorY + modelHeadHeight;
            bodyY = floorY;
        }
        else
        {
            scale = Mathf.Clamp(headH / modelHeadHeight, 0.55f, 1.3f);
            bodyY = headPos.y - modelHeadHeight * scale;
        }

        headTarget.SetPositionAndRotation(headPos, headRot);
        if (_localPlayer.handRight != null)
            handRightTarget.SetPositionAndRotation(_localPlayer.handRight.transform.position, _localPlayer.handRight.transform.rotation);
        if (_localPlayer.handLeft != null)
            handLeftTarget.SetPositionAndRotation(_localPlayer.handLeft.transform.position, _localPlayer.handLeft.transform.rotation);

        body.localScale = Vector3.one * scale;
        body.position = new Vector3(headPos.x + headBodyPositionOffset.x, bodyY, headPos.z + headBodyPositionOffset.z);
        body.rotation = Quaternion.Lerp(body.rotation, Quaternion.Euler(body.rotation.eulerAngles.x, headRot.eulerAngles.y, body.rotation.eulerAngles.z), bodyRotateSmoothness);
    }
}
