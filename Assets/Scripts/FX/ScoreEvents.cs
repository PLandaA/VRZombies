using UnityEngine;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.World;

namespace VRZ.FX
{

    /// Central kill accounting for the LOCAL player: credits networked score/kill stats
    /// immediately (authoritative, must not be deferred) and hands the PRESENTATION (combo
    /// popups) to the FeedbackQueue, which coalesces same-frame kills into one banner.
    public static class ScoreEvents
    {
        public static void RegisterKill(bool headshot, Vector3 worldPos)
        {
            var np = NetworkSession.Current?.GetPlayer();
            if (np != null && np.IsValid)
            {
                np.TotalScore += headshot ? 25 : 10;
                np.Kills++;
                if (headshot) np.HeadshotKills++;
            }

            Feedback.Sink.EnqueueKill(headshot, worldPos);
        }
    }
}
