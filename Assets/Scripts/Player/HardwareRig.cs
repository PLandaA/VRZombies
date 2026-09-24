using Fusion;
using Fusion.Sockets;
using System;
using System.Collections.Generic;
using UnityEngine;
using VRZ.Network;

namespace VRZ.Player
{

    /// <summary>
    /// MODIFIED HardwareRig — Integración AutoHand 4 + Photon Fusion 2 (Shared Mode)
    ///
    /// REEMPLAZA al LocalCharacter del curso. Ya no lee directamente del XR,
    /// sino que lee los transforms del AutoHand Player Container, que ya
    /// maneja toda la lógica de tracking, física y locomoción VR.
    ///
    /// SETUP en el prefab AutoHand Player Container:
    /// ─────────────────────────────────────────────
    /// [AutoHand Player Container]
    ///   ├── Este script (HardwareRig) va aquí
    ///   ├── Tracked Offsets
    ///   │   ├── Camera (head)         → asignar a "head"
    ///   │   ├── RobotHand (R)         → asignar a "handRight"
    ///   │   └── RobotHand (L)         → asignar a "handLeft"
    ///   └── AutoHandPlayer            → asignar a "character"
    ///                                    (es el Transform del AutoHandPlayer,
    ///                                     que tiene el Rigidbody y CapsuleCollider)
    /// </summary>
    public class HardwareRig : MonoBehaviour, INetworkRunnerCallbacks
    {
        [Header("AutoHand Transforms — arrastrar desde la jerarquía")]
        [Tooltip("Transform del AutoHandPlayer (hijo directo del Container). " +
                 "Es el cuerpo físico del jugador con Rigidbody + CapsuleCollider.")]
        [SerializeField] Transform character;

        [Tooltip("Transform de 'Camera (head)' dentro de Tracked Offsets.")]
        [SerializeField] Transform head;

        [Tooltip("Transform de 'RobotHand (R)' dentro de Tracked Offsets.")]
        [SerializeField] Transform handRight;

        [Tooltip("Transform de 'RobotHand (L)' dentro de Tracked Offsets.")]
        [SerializeField] Transform handLeft;

        // ─── Interactor para colisión con zombies ─────────────────────────────────
        //
        //
        //
        // (Interactor del curso eliminado: el daño de zombies viaja por RPC de NetworkPlayer)

        // ─── Ciclo de vida ────────────────────────────────────────────────────────

        void Start()
        {
            // Registrarse al runner para recibir OnInput
            if (NetworkManager.instance != null && NetworkManager.instance.runner != null)
                NetworkManager.instance.runner.AddCallbacks(this);
            else
                Debug.LogError("[HardwareRig] NetworkManager o runner no disponible en Start.");
        }

        private void OnDisable()
        {
            if (NetworkManager.instance != null && NetworkManager.instance.runner != null)
                NetworkManager.instance.runner.RemoveCallbacks(this);
        }

        // ─── INetworkRunnerCallbacks → OnInput ────────────────────────────────────

        /// <summary>
        /// Fusión llama esto cada tick de simulación para recopilar el input local.
        /// Leemos los transforms de AutoHand y los empaquetamos en CharacterInputData.
        /// </summary>
        public void OnInput(NetworkRunner runner, NetworkInput input)
        {
            CharacterInputData inputData = new CharacterInputData();

            // Posición/rotación del cuerpo físico (AutoHandPlayer con CapsuleCollider)
            inputData.characterPosition = character.position;
            inputData.characterRotation = character.rotation;

            // Cabeza (Camera dentro de Tracked Offsets)
            inputData.headPosition = head.position;
            inputData.headRotation = head.rotation;

            // Manos (RobotHand R/L dentro de Tracked Offsets)
            inputData.handRightPosition = handRight.position;
            inputData.handRightRotation = handRight.rotation;

            inputData.handLeftPosition = handLeft.position;
            inputData.handLeftRotation = handLeft.rotation;

            input.Set(inputData);
        }

        // ─── Callbacks no utilizados ──────────────────────────────────────────────
        #region UnusedCallbacks

        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ArraySegment<byte> data) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
        public void OnUserSimulationMessage(NetworkRunner runner, SimulationMessagePtr message) { }

        #endregion
    }
}
