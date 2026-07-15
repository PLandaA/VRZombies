using UnityEngine;
using Autohand;

/// Keeps loose weapons and magazines floating frozen in place until grabbed (no gravity drop).
public class FloatingWeapon : MonoBehaviour
{
    [Tooltip("Damping applied when released so it does not drift when nudged")]
    [SerializeField] private float floatDamping = 4f;

    private Rigidbody _rb;
    private Grabbable _grabbable;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _grabbable = GetComponent<Grabbable>();

        if (_rb != null)
        {
            _rb.useGravity = false;
            _rb.linearDamping = floatDamping;
            _rb.angularDamping = floatDamping;
        }

        if (_grabbable != null)
        {
            _grabbable.OnReleaseEvent += OnReleased;
            _grabbable.OnGrabEvent += OnGrabbed;
        }
    }

    private void OnDestroy()
    {
        if (_grabbable != null)
        {
            _grabbable.OnReleaseEvent -= OnReleased;
            _grabbable.OnGrabEvent -= OnGrabbed;
        }
    }

    private void OnGrabbed(Hand hand, Grabbable grab)
    {
        if (_rb == null) _rb = GetComponent<Rigidbody>();
        if (_rb == null) return;
        _rb.useGravity = false;
    }

    private void OnReleased(Hand hand, Grabbable grab)
    {
        Invoke(nameof(FreezeInPlace), 0.02f);
    }

    private void FreezeInPlace()
    {
        if (_rb == null) _rb = GetComponent<Rigidbody>();
        if (_rb == null) return;
        _rb.useGravity = false;
        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        _rb.linearDamping = floatDamping;
        _rb.angularDamping = floatDamping;
    }
}
