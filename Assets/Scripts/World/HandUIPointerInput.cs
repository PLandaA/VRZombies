using Autohand;
using Autohand.Demo;
using UnityEngine;
using UnityEngine.InputSystem;

namespace VRZ.World
{
    /// Drives a HandCanvasPointer from the controller trigger (netcode fix B2, session menu).
    ///
    /// AutoHand's HandCanvasPointer only exposes Press()/Release(); it does not read input by
    /// itself. This reads a 0..1 trigger axis every frame and turns it into press/release edges
    /// with hysteresis, so a half-pulled trigger does not flicker.
    ///
    /// The axis is an explicit InputActionProperty (assign "Auto Hand/Trigger Axis (R)" from the
    /// AutoHand .inputactions asset). It is NOT taken from OpenXRHandControllerLink.grabAxis: in
    /// this project that field is an inline action with no bindings (grabbing is wired to the
    /// GRIP button actions instead), so it always reads 0.
    [RequireComponent(typeof(HandCanvasPointer))]
    public class HandUIPointerInput : MonoBehaviour
    {
        [Tooltip("0..1 trigger axis action. Use the 'Trigger Axis (R)' reference from Auto Hand.inputactions.")]
        [SerializeField] private InputActionProperty triggerAxis;

        [Tooltip("Fallback only: the hand's OpenXR link, used if triggerAxis has no bindings.")]
        [SerializeField] private OpenXRHandControllerLink link;

        [SerializeField, Range(0.1f, 1f)] private float pressThreshold = 0.7f;
        [SerializeField, Range(0f, 0.9f)] private float releaseThreshold = 0.3f;

        private HandCanvasPointer _pointer;
        private InputAction _axis;
        private bool _pressed;

        private void Awake()
        {
            _pointer = GetComponent<HandCanvasPointer>();
            if (link == null) link = GetComponentInParent<OpenXRHandControllerLink>();
        }

        private void OnEnable()
        {
            // Prefer the explicit axis; fall back to the link's grab axis only if it is actually bound.
            _axis = triggerAxis.action;
            if (_axis == null || _axis.bindings.Count == 0)
                _axis = link != null ? link.grabAxis.action : null;

            if (_axis == null || _axis.bindings.Count == 0)
            {
                Debug.LogWarning("[HandUIPointerInput] No bound trigger axis: assign 'Trigger Axis (R)' on " + name + ". Menu clicks will not work.");
                _axis = null;
                return;
            }
            _axis.Enable();   // actions do nothing until enabled; the link never enables grabAxis
        }

        private void OnDisable()
        {
            // Never leave a UI button "held" when the pointer is switched off.
            if (_pressed) { _pressed = false; _pointer.Release(); }
            // Do not Disable() the action: it may be shared with other users of the asset.
        }

        private void Update()
        {
            if (_axis == null) return;

            float trigger = _axis.ReadValue<float>();

            if (!_pressed && trigger >= pressThreshold)
            {
                _pressed = true;
                _pointer.Press();
            }
            else if (_pressed && trigger <= releaseThreshold)
            {
                _pressed = false;
                _pointer.Release();
            }
        }
    }
}
