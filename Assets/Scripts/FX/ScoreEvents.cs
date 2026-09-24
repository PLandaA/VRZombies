using UnityEngine;
using VRZ.Core;

namespace VRZ.FX
{

    /// Central kill accounting for the LOCAL player: credits networked score/kill stats and hands
    /// the PRESENTATION (combo popups) to the FeedbackQueue, which coalesces same-frame kills.
    /// Called from NetworkZombie.Render when the zombie's replicated LastDamager
    /// is the local player, i.e. from the State Authority's verdict, never from a prediction.
    public static class ScoreEvents
    {
        /// Single source of truth for points; ZombieJuice reads these for its popup text.
        public const int KillPoints = 10;
        public const int HeadshotPoints = 25;

        public static void RegisterKill(bool headshot, Vector3 worldPos)
        {
            var np = NetworkSession.Current?.GetPlayer();
            if (np != null && np.IsValid)
            {
                np.TotalScore += headshot ? HeadshotPoints : KillPoints;
                np.Kills++;
                if (headshot) np.HeadshotKills++;
            }

            Feedback.Sink.EnqueueKill(headshot, worldPos);
        }
    }
}
