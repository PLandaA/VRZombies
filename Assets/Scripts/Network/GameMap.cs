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
            // Null when GameScene is opened directly in the editor (no lobby, no manager).
            var nm = NetworkManager.instance;
            if (nm != null) nm.onSceneLoadDone += SpawnCharacterOnGameMap;
        }

        private void OnDisable()
        {
            // On editor teardown the manager can already be gone (destroy order is not guaranteed).
            var nm = NetworkManager.instance;
            if (nm != null) nm.onSceneLoadDone -= SpawnCharacterOnGameMap;
        }

        public void SpawnCharacterOnGameMap(NetworkRunner runner)
        {
            SpawnCharacter(runner, runner.LocalPlayer);
            // Scene integration (the GC hitch) is behind us once the local character exists:
            // lift the fade the lobby started. Harmless if no fade was running.
            VRZ.World.ScreenFader.FadeIn(FadeInSeconds);
        }

        private const float FadeInSeconds = 1.2f;
    }
}
