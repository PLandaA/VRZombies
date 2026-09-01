using System.Collections.Generic;

namespace VRZ.Core
{
    /// Wave-system rules that need no network, no scene and no time: pure functions over the
    /// player contract. ZombieSpawner (the NetworkBehaviour) calls these; tests call them with fakes.
    public static class WaveRules
    {
        /// Players that are valid AND have signalled Ready (grabbed a weapon).
        public static int CountReady(IEnumerable<IPlayerState> players)
        {
            int ready = 0;
            foreach (var p in players)
                if (p != null && p.IsValid && p.Ready) ready++;
            return ready;
        }

        /// The game is over only when there IS someone to lose it and nobody is alive.
        /// An empty (or all-invalid) session is NOT a game over: nobody has joined yet.
        public static bool AllPlayersDead(IEnumerable<IPlayerState> players)
        {
            bool anyPlayer = false;
            foreach (var p in players)
            {
                if (p == null || !p.IsValid) continue;
                anyPlayer = true;
                if (p.IsAlive) return false;
            }
            return anyPlayer;
        }

        /// Zombies to spawn for `wave` (1-based): a base count plus a linear ramp.
        public static int ZombiesForWave(int wave, int baseZombies, int addedPerWave)
        {
            if (wave < 1) return 0;
            return baseZombies + (wave - 1) * addedPerWave;
        }
    }
}
