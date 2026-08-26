using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using Autohand;

[RequireComponent(typeof(Grabbable))]
/// Local (non-networked) practice grenade for the lobby tutorial: throw it, it detonates with
/// FX only (no damage), fires OnThrown for the tutorial, then respawns at its start pose.
public class PracticeGrenade : MonoBehaviour
{
    [Tooltip("Seconds between release and detonation")]
    [SerializeField] private float fuseSeconds = 1.5f;

    [Tooltip("Seconds before it reappears on the table")]
    [SerializeField] private float respawnDelay = 3f;

    [SerializeField] private GameObject explosionEffect;
    [SerializeField] private AudioClip explosionSound;

    public UnityEvent OnThrown;

    private Grabbable _grab;
    private Rigidbody _rb;
    private Vector3 _homePos;
    private Quaternion _homeRot;
    private bool _armed;
    private bool _wasHeld;   // a hand must actually grab it before it can arm (no phantom throws)

    private void Awake()
    {
        _grab = GetComponent<Grabbable>();
        _rb = GetComponent<Rigidbody>();
        _homePos = transform.position;
        _homeRot = transform.rotation;
        _grab.OnGrabEvent += OnGrabbed;
        _grab.OnReleaseEvent += OnReleased;
    }

    private void OnDestroy()
    {
        if (_grab != null)
        {
            _grab.OnGrabEvent -= OnGrabbed;
            _grab.OnReleaseEvent -= OnReleased;
        }
    }

    private void OnGrabbed(Hand hand, Grabbable g)
    {
        _wasHeld = true;
    }

    private void OnReleased(Hand hand, Grabbable g)
    {
        Invoke(nameof(ArmCheck), 0.25f);
    }

    private void ArmCheck()
    {
        if (_armed || !_wasHeld || _grab.IsHeld()) return;
        _armed = true;
        StartCoroutine(FuseRoutine());
    }

    private IEnumerator FuseRoutine()
    {
        yield return new WaitForSeconds(fuseSeconds);

        if (explosionEffect != null)
        {
            var fx = Instantiate(explosionEffect, transform.position, Quaternion.identity);
            if (fx.GetComponentInChildren<ExplosionLightFade>() == null)
                fx.AddComponent<ExplosionLightFade>();
            foreach (var ps in fx.GetComponentsInChildren<ParticleSystem>())
                if (!ps.isPlaying) ps.Play();
            Destroy(fx, 5f);
        }
        if (explosionSound != null)
            AudioSource.PlayClipAtPoint(explosionSound, transform.position, 1f);

        OnThrown?.Invoke();

        // Hide while "destroyed"
        foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = false;
        foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
        if (_rb != null) { _rb.isKinematic = true; _rb.linearVelocity = Vector3.zero; _rb.angularVelocity = Vector3.zero; }

        yield return new WaitForSeconds(respawnDelay);

        transform.SetPositionAndRotation(_homePos, _homeRot);
        foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = true;
        foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = true;
        if (_rb != null) _rb.isKinematic = false;
        _armed = false;
        _wasHeld = false;   // must be grabbed again before the next arm
    }
}
