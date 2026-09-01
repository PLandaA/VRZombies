using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VRZ.Core;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.FX;
using VRZ.World;

namespace VRZ.Network
{

    public class LobbyMap : MapDefault
    {
        private void Start()
        {
            NetworkManager.instance.onPlayerSpawn += SpawnCharacter;
        }
        private void OnDisable()
        {
            NetworkManager.instance.onPlayerSpawn -= SpawnCharacter;
        }

    }
}
