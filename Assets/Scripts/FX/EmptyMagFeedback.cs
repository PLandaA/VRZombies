using System.Collections;
using UnityEngine;
using Autohand;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.World;

namespace VRZ.FX
{

    [RequireComponent(typeof(AutoGun))]
    /// Dry-fire feedback: a distinct double-tick haptic when the trigger is pulled on an empty
    /// gun, so the player instantly KNOWS it's out of ammo (the click sound comes from AutoGun).
    public class EmptyMagFeedback : MonoBehaviour
    {
        [SerializeField] private Vector2 dryHaptic = new Vector2(0.04f, 0.5f);

        private AutoGun _gun;
        private Grabbable _grab;
        private float _lastPulse;

        private void Awake()
        {
            _gun = GetComponent<AutoGun>();
            _grab = GetComponent<Grabbable>();
        }

        private void OnEnable() { _gun.OnEmptyShoot.AddListener(OnDryFire); }
        private void OnDisable() { _gun.OnEmptyShoot.RemoveListener(OnDryFire); }

        private void OnDryFire(AutoGun gun)
        {
            if (Time.time - _lastPulse < 0.15f) return;   // throttle auto-fire spam
            _lastPulse = Time.time;
            if (_grab == null) return;
            foreach (var hand in _grab.GetHeldBy())
            {
                if (hand == null) continue;
                hand.PlayHapticVibration(0.18f, 0.7f);
                StartCoroutine(SecondTick(hand));
            }
        }

        private IEnumerator SecondTick(Hand hand)
        {
            yield return new WaitForSeconds(0.09f);
            if (hand != null) hand.PlayHapticVibration(0.14f, 0.55f);
        }
    }
}
