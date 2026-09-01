using UnityEngine;

namespace VRZ.Core
{
    /// Where presentation feedback is sent. Gameplay code (score, zombies, grenades) depends on
    /// this contract, not on a concrete queue -- a silent sink for tests or a different
    /// presentation layer plugs in without touching gameplay.
    public interface IFeedbackSink
    {
        void EnqueueKill(bool headshot, Vector3 worldPos);
        void EnqueueSound(AudioClip clip, Vector3 position, float volume = 1f);
    }

    /// Injection point for the feedback sink. Core ships no default: the presentation layer
    /// registers its queue on startup; until then (and in tests) everything is swallowed.
    public static class Feedback
    {
        private static readonly IFeedbackSink Silent = new SilentFeedbackSink();
        private static IFeedbackSink _current;

        public static IFeedbackSink Sink => _current ?? Silent;

        public static void Override(IFeedbackSink sink) => _current = sink;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _current = null;
    }

    /// Swallows everything. For tests, headless runs, or "no juice" profiling.
    public sealed class SilentFeedbackSink : IFeedbackSink
    {
        public void EnqueueKill(bool headshot, Vector3 worldPos) { }
        public void EnqueueSound(AudioClip clip, Vector3 position, float volume = 1f) { }
    }
}
