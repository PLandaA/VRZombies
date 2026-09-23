using System.Collections.Generic;

namespace VRZ.Core
{
    /// Spawn-slot rule (pure). MapDefault calls it with the room's active player ids.
    public static class SpawnRules
    {
        /// A player's spawn slot is their RANK among the active players, sorted by id, wrapped by the
        /// number of slots. Every client sees the same set of ids, so every client computes the same
        /// slot for the same player. Using the raw id (id % slots) collided after a leave/rejoin,
        /// because the cloud hands out ids that are not consecutive (1 and 3 both map to slot 1).
        /// Unknown players (not in the list) get slot 0; no slots means slot 0.
        public static int SlotFor(IEnumerable<int> activePlayerIds, int myPlayerId, int slotCount)
        {
            if (slotCount <= 0) return 0;
            var ids = new List<int>();
            if (activePlayerIds != null)
                foreach (var id in activePlayerIds)
                    if (!ids.Contains(id)) ids.Add(id);
            ids.Sort();
            int rank = ids.IndexOf(myPlayerId);
            return (rank >= 0 ? rank : 0) % slotCount;
        }
    }
}
