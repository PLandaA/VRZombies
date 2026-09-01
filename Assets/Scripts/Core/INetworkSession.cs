using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace VRZ.Core
{
    /// The per-player state gameplay and feedback are allowed to read or write. Implemented by
    /// the networked player object; Core never sees that class.
    public interface IPlayerState : IDamageable
    {
        bool IsValid { get; }
        bool Ready { get; set; }
        bool TutorialDone { get; set; }
        int MaxHealth { get; }
        int TotalScore { get; set; }
        int Kills { get; set; }
        int HeadshotKills { get; set; }

        /// Fired on the owning client when damage arrives, with the attacker's position.
        event System.Action<Vector3> OnDamagedFrom;
    }

    /// What GAMEPLAY and FEEDBACK code is allowed to know about the network session: who is
    /// playing and who I am. Nothing about runners, scene loading or spawning -- that is the
    /// network layer's own business.
    public interface INetworkSession
    {
        bool IsRunning { get; }
        PlayerRef LocalPlayer { get; }
        IReadOnlyCollection<IPlayerState> Players { get; }

        /// The local player's state (null before spawn).
        IPlayerState GetPlayer();
        IPlayerState GetPlayer(PlayerRef player);
    }

    /// Injection point for the session. Core ships no default: the network layer registers
    /// itself on startup (NetworkManager.Awake); tests or an offline mode register their own.
    public static class NetworkSession
    {
        private static INetworkSession _current;

        public static INetworkSession Current => _current;

        public static void Override(INetworkSession session) => _current = session;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _current = null;
    }

    /// Session with nobody in it. Everything that reads the session degrades gracefully.
    public sealed class NullNetworkSession : INetworkSession
    {
        private static readonly IPlayerState[] Nobody = System.Array.Empty<IPlayerState>();

        public bool IsRunning => false;
        public PlayerRef LocalPlayer => PlayerRef.None;
        public IReadOnlyCollection<IPlayerState> Players => Nobody;
        public IPlayerState GetPlayer() => null;
        public IPlayerState GetPlayer(PlayerRef player) => null;
    }
}
