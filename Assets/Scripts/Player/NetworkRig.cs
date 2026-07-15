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

        [Tooltip("Offset de rotación en euler (útil si el modelo tiene rotación base diferente)")]
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
    [SerializeField] Transform character;

    [Header("IK Targets — arrastrar desde VR_IK_Rig")]
    [Tooltip("headIK_target dentro de HeadIK")]
    [SerializeField] IKTarget headTarget;

    [Tooltip("rightarmik_target dentro de RightArmIK")]
    [SerializeField] IKTarget handRightTarget;

    [Tooltip("leftarmik_target dentro de LeftArmIK")]
    [SerializeField] IKTarget handLeftTarget;

    [Header("Body Visual (torso del avatar)")]
    [Tooltip("Avatar body transform. Anchored under the head, smoothly yawing toward where the player looks.")]
    [SerializeField] Transform body;

    [Tooltip("Body offset relative to the head. Y must be minus the model head height (e.g. 0, -1.55, 0).")]
    [SerializeField] Vector3 headBodyPositionOffset = new Vector3(0f, -0.5f, 0f);

    [Tooltip("Body rotation smoothing (0 = instant, 1 = never rotates). Recommended: 0.1")]
    [SerializeField][Range(0f, 1f)] float bodyRotateSmoothness = 0.1f;

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
            float modelHeadHeight = Mathf.Max(0.1f, -headBodyPositionOffset.y);

            float headH = headPos.y - floorY;
            float scale = (headH < 0.9f) ? 1f : Mathf.Clamp(headH / modelHeadHeight, 0.7f, 1.3f);
            body.localScale = Vector3.one * scale;

            body.position = new Vector3(
                headPos.x + headBodyPositionOffset.x,
                headPos.y - modelHeadHeight * scale,
                headPos.z + headBodyPositionOffset.z);
            body.rotation = Quaternion.Lerp(body.rotation, Quaternion.Euler(body.rotation.x, inputData.headRotation.eulerAngles.y, body.rotation.z), bodyRotateSmoothness);

            if (Runner.Tick % 64 == 0)
                Debug.Log("[NetworkRig] headY=" + headPos.y.ToString("F2") + " floorY=" + floorY.ToString("F2") + " escala=" + scale.ToString("F2") + " bodyY=" + body.position.y.ToString("F2"));
        }
        base.FixedUpdateNetwork();
    }
}