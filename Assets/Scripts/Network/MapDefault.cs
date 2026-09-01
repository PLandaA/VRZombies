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
        void Start()
        {

        }

        // Update is called once per frame
        void Update()
        {

        }
        public void SpawnCharacter(NetworkRunner runner, PlayerRef playerRef)
        {
            if (playerRef == runner.LocalPlayer)
            {
               
                if (SpawnPoints.Count > 0)
                {
                    localCharacter.localPosition = SpawnPoints[playerRef.PlayerId % SpawnPoints.Count].spawnPoint.position;
                }
                else
                {
                    localCharacter.position = new Vector3 (0, 3, 0);
                }
                var spawned = runner.Spawn(networkCharacterPrefab, localCharacter.position, localCharacter.rotation,playerRef);
                Debug.Log("[Map] Avatar spawned: " + (spawned != null ? spawned.name : "SPAWN FAILED"));
                
            }
        }

        public void ChangeNetworkScene(int sceneIndex)
        {
            NetworkManager.instance.runner.LoadScene(SceneRef.FromIndex(sceneIndex));
        }

        [ContextMenu("FindSpawnPointsInScene")]
        public void FindSpawnPoints()
        {
            SpawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None).ToList<SpawnPoint>();
        }
    }
}
