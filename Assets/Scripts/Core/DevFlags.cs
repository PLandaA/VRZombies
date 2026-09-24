using UnityEngine;

namespace VRZ.Core
{
    /// Development-only switches.
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

        // VRZ_NET_DIAGNOSTICS: periodic diagnostics ([Zombie N] path state, [Waves] Spawn took,
        // [NetGun] held, [SnapTurnFix] hand-vs-controller trace, and the editor-only [PhysStep]
        // physics-step monitor with its profiler-based hitch autopsy). Off by default, even in
        // development builds: they did their job during the hardening pass and would otherwise flood
        // the console (and the profiler recording slows the editor down).
        // The code sites use `#if VRZ_NET_DIAGNOSTICS` directly so the diagnostics compile out.
#if VRZ_NET_DIAGNOSTICS
        public const bool NetDiagnostics = true;
#else
        public const bool NetDiagnostics = false;
#endif

        /// Player-count gates (lobby start, waves ready check) collapse to 1 in solo test mode.
        public static int MinPlayers(int configured) => SoloTest ? 1 : configured;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void WarnIfActive()
        {
            // #if rather than `if (SoloTest)`: the flags are constants, so a plain if on a false
            // constant is unreachable code (compiler warning CS0162) in every normal build.
#if VRZ_SOLO_TEST
            Debug.LogWarning("[DevFlags] VRZ_SOLO_TEST is ON: lobby and waves start with 1 player.");
#endif
#if VRZ_INVULNERABLE
            Debug.LogWarning("[DevFlags] VRZ_INVULNERABLE is ON: the local player takes no damage.");
#endif
#if VRZ_NET_DIAGNOSTICS
            Debug.LogWarning("[DevFlags] VRZ_NET_DIAGNOSTICS is ON: periodic netcode diagnostics will be logged.");
#endif
        }
    }
}
