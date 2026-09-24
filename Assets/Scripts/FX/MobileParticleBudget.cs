using UnityEngine;

namespace VRZ.FX
{

    /// Platform-aware particle budget: on Android (Quest) reduces emission and count while
    /// slightly enlarging particles, keeping the same visual coverage at a fraction of the
    /// overdraw. On PC it changes nothing. Lives on the level-wide leaves emitter.
    public class MobileParticleBudget : MonoBehaviour
    {
    #pragma warning disable 0414   // fields are consumed inside the UNITY_ANDROID block below
        [Tooltip("Emission rate used on Android (PC keeps the authored rate)")]
        [SerializeField] private float mobileRate = 25f;

        [Tooltip("Max particles on Android")]
        [SerializeField] private int mobileMaxParticles = 350;

        [Tooltip("Size multiplier on Android (bigger particles = same coverage with fewer of them)")]
        [SerializeField] private float mobileSizeMultiplier = 1.25f;
    #pragma warning restore 0414

        private void Awake()
        {
    #if UNITY_ANDROID && !UNITY_EDITOR
            var ps = GetComponent<ParticleSystem>();
            if (ps == null) return;

            var main = ps.main;
            main.maxParticles = mobileMaxParticles;
            var size = main.startSize;
            size.constant *= mobileSizeMultiplier;
            main.startSize = size;

            var emission = ps.emission;
            emission.rateOverTime = mobileRate;
    #endif
        }
    }
}
