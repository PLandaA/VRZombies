using UnityEngine;

namespace VRZ.Core
{
    /// Development-only switches (netcode fix B3).
    ///
    /// The old approach was to leave test values in the scene (requiredPlayers = 1,
    /// maxHealth = 10000) and remember to put them back before a build. Nobody did, so the whole
    /// damage -> death -> game over flow shipped untested and one player could start a match alone.
    ///
    /// Now the scene keeps the PRODUCTION values and testing is opted into explicitly with
    /// scripting defines (Player Settings -> Scripting Define Symbols, or a .asmdef define):
    ///   VRZ_SOLO_TEST     lobby and waves start with a single player
    ///   VRZ_INVULNERABLE  the local player ignores damage
    /// Both are compile-time constants: in a normal build the branches vanish entirely, and a
    /// warning is logged at startup whenever one is active so it cannot go unnoticed.
    public static class DevFlags
    {
#if VRZ_SOLO_TEST
        public const bool SoloTest = true;
#else
        public const bool SoloTest = false;
#endif

#if VRZ_INVULNERABLE
        public const bool Invulnerable = true;
#else
        public const bool Invulnerable = false;
#endif

        /// Player-count gates (lobby start, waves ready check) collapse to 1 in solo test mode.
        public static int MinPlayers(int configured) => SoloTest ? 1 : configured;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void WarnIfActive()
        {
            if (SoloTest) Debug.LogWarning("[DevFlags] VRZ_SOLO_TEST is ON: lobby and waves start with 1 player.");
            if (Invulnerable) Debug.LogWarning("[DevFlags] VRZ_INVULNERABLE is ON: the local player takes no damage.");
        }
    }
}
