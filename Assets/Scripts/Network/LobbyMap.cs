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
            if (NetworkManager.instance != null)
                NetworkManager.instance.onPlayerSpawn += SpawnCharacter;
        }
        private void OnDisable()
        {
            // The manager destroys itself on shutdown (fix B1), so it can be gone by the time this
            // scene unloads or Play stops. GameMap already guarded this; LobbyMap did not.
            if (NetworkManager.instance != null)
                NetworkManager.instance.onPlayerSpawn -= SpawnCharacter;
        }

    }
}
