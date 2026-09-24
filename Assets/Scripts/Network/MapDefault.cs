using System.Collections.Generic;
using UnityEngine;
using Fusion;
using System.Linq;
using VRZ.Core;
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
                    // Spawn slot = my rank among the players in the room (SpawnRules, unit-tested).
                    var ids = new List<int>();
                    foreach (var p in runner.ActivePlayers) ids.Add(p.PlayerId);
                    int slot = SpawnRules.SlotFor(ids, playerRef.PlayerId, SpawnPoints.Count);
                    localCharacter.position = SpawnPoints[slot].spawnPoint.position;
                }
                else
                {
                    localCharacter.position = new Vector3 (0, 3, 0);
                }
                var spawned = runner.Spawn(networkCharacterPrefab, localCharacter.position, localCharacter.rotation,playerRef);
                if (spawned != null) Debug.Log("[Map] Avatar spawned: " + spawned.name);
                else Debug.LogError("[Map] Avatar spawn FAILED: the partner will not see this player.");
            }
        }

        [ContextMenu("FindSpawnPointsInScene")]
        public void FindSpawnPoints()
        {
            SpawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).ToList<SpawnPoint>();
        }
    }
}
