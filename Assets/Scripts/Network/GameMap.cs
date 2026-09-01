using Fusion;
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

    public class GameMap : MapDefault
    {
        private void OnEnable()
        {
            NetworkManager.instance.onSceneLoadDone += SpawnCharacterOnGameMap;
        }

        private void OnDisable()
        {
            NetworkManager.instance.onSceneLoadDone -= SpawnCharacterOnGameMap;
        }

        public void SpawnCharacterOnGameMap(NetworkRunner runner)
        {
            SpawnCharacter(runner, runner.LocalPlayer);
        }
    }
}
