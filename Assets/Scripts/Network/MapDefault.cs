using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Fusion;
using System.Linq;
using VRZ.Core;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.FX;
using VRZ.World;

namespace VRZ.Network
{
    public class MapDefault : MonoBehaviour
    {
        [SerializeField]
        List<SpawnPoint> SpawnPoints = new List<SpawnPoint>();

        [SerializeField]
        Transform localCharacter;

        [SerializeField]
        NetworkObject networkCharacterPrefab;

        public void SpawnCharacter(NetworkRunner runner, PlayerRef playerRef)
        {
            if (playerRef == runner.LocalPlayer)
            {
                if (SpawnPoints.Count > 0)
                {
                    // Spawn slot = my rank among the players currently in the room, not my PlayerId.
                    // Ids are handed out by the cloud and are not consecutive after a leave/rejoin
                    // (1 and 3 both mapped to slot 1 and two players appeared side by side).
                    // ActivePlayers is the same set on every client, so sorting it gives every
                    // client the same slot for the same player.
                    int slot = SpawnSlotFor(runner, playerRef) % SpawnPoints.Count;
                    localCharacter.position = SpawnPoints[slot].spawnPoint.position;
                }
                else
                {
                    localCharacter.position = new Vector3 (0, 3, 0);
                }
                var spawned = runner.Spawn(networkCharacterPrefab, localCharacter.position, localCharacter.rotation,playerRef);
                Debug.Log("[Map] Avatar spawned: " + (spawned != null ? spawned.name : "SPAWN FAILED"));
            }
        }

        private static int SpawnSlotFor(NetworkRunner runner, PlayerRef me)
        {
            var ids = new List<int>();
            foreach (var p in runner.ActivePlayers) ids.Add(p.PlayerId);
            ids.Sort();
            int slot = ids.IndexOf(me.PlayerId);
            return slot >= 0 ? slot : 0;
        }

        [ContextMenu("FindSpawnPointsInScene")]
        public void FindSpawnPoints()
        {
            SpawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).ToList<SpawnPoint>();
        }
    }
}
