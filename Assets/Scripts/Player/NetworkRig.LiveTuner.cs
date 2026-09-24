#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.InputSystem;

namespace VRZ.Player
{

    /// Editor-only live tuner for the avatar rig (partial of NetworkRig so it can poke the
    /// private serialized fields without widening the class API). The canonical "eyeball it in
    /// the headset" calibration method, with power tools:
    ///   I/K J/L U/O      wrist ROTATION offset +-2 deg (pitch / yaw / roll)
    ///   Arrows, PgUp/Dn  wrist POSITION offset +-1cm (X/Y, Z)
    ///   T/G Y/H R/F      elbow offset +-2cm (out / down / back)
    ///   - / =            arm length +-2%
    ///   P                print every value so the winners can be baked into the prefab
    /// Hold LeftShift to tune the LEFT hand instead of the right.
    public partial class NetworkRig
    {
        private void LiveTuner()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            bool leftHand = kb.leftShiftKey.isPressed;

            void NudgeRot(Vector3 delta)
            {
                if (leftHand) handLeftTarget.rotationOffset += delta;
                else handRightTarget.rotationOffset += delta;
                Debug.Log("[RigTuner] " + (leftHand ? "L" : "R") + " rotOffset -> " +
                    (leftHand ? handLeftTarget.rotationOffset : handRightTarget.rotationOffset).ToString("F1"));
            }
            if (kb.iKey.wasPressedThisFrame) NudgeRot(new Vector3(+2f, 0f, 0f));
            if (kb.kKey.wasPressedThisFrame) NudgeRot(new Vector3(-2f, 0f, 0f));
            if (kb.jKey.wasPressedThisFrame) NudgeRot(new Vector3(0f, -2f, 0f));
            if (kb.lKey.wasPressedThisFrame) NudgeRot(new Vector3(0f, +2f, 0f));
            if (kb.uKey.wasPressedThisFrame) NudgeRot(new Vector3(0f, 0f, -2f));
            if (kb.oKey.wasPressedThisFrame) NudgeRot(new Vector3(0f, 0f, +2f));

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
    }
}
#endif
