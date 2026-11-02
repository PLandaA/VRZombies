# Setup, testing and project structure

## Project structure (my code)

```
Assets/Scripts/
  Network/   NetworkManager (runner lifecycle, session lobby + rooms, Fusion player-object lookup + avatar registry, spawn handshake,
             disconnect handling), PooledObjectProvider (Fusion INetworkObjectProvider with opt-in pooling),
             LobbyManager (ready-up + scene load), MapDefault/GameMap/LobbyMap (avatar spawn per scene)
  Core/      VRZ.Core.asmdef (independent assembly -- contracts cannot depend on gameplay),
             IDamageable + DamageInfo, INetworkSession + IPlayerState (+ injection points,
             Null/Silent implementations), IFeedbackSink, PrefabPool, Rules/ (pure, unit-tested rules:
             WaveRules, GrenadeRules, ZombieDamageRules, SpawnRules, RoomCodeRules),
             DevFlags (compile-time test switches)
  Player/    NetworkRig (avatar orchestration) + NetworkRig.LiveTuner (editor partial),
             Avatar/ VRZ.Avatar.asmdef (engine-only assembly) { FloorSampler, AvatarScaleCalibrator,
                     PoseSmoother, ElbowPole, IKTarget, SpectatorFollow },
             HardwareRig (AutoHand -> Fusion input adapter), NetworkPlayer (health/score/stats),
             CharacterInputData (per-tick input struct), LocalAvatarHider (hide own head/neck/hands),
             PlayerBelt (per-round grenade budget, self-healing if AutoHand drops a grenade), DamageVignette
             (radial hurt feedback), PlayerHeightGuard, SnapTurnHeldFix (snap turn with objects outside the
             tracking container), DeathSpectator (follow-cam for a dead player)
  Weapons/   NetworkAutoGun (tick-synced shots, headshots, replicated chamber), NetworkAutoAmmo (mag sync),
             NetworkGrenade (personal grenades, fuse/explosion), NetworkGunHitEffect, AmmoSpawner, FloatingWeapon,
             WeaponHomeReturn (off by default since rifles float), WeaponFloatOnRelease, HeldIgnoresTerrain, KeepWorldParentWhileHeld (held objects replicate world coords)
  Enemies/   NetworkZombie (NavMesh FSM + melee tuning, nearest-player targeting, synchronous pathing,
             authoritative kill credit, pool-safe reset), ZombieSpawner (waves, dirty-flag counts,
             self-registration), ZombieHitFlash
  FX/        FeedbackQueue (coalescing/budgeted presentation, self-registers as the sink),
             WeaponFeel, ZombieJuice/ZombieFeel, PopupText (pooled), ScoreEvents, PlayerHUD (lazy-follow in-match HUD),
             RoundJuice (banners/music/slow-mo), StormAmbience, BulletTracer, EmptyMagFeedback,
             MuzzleFlashLight, HeartbeatFeedback (procedural audio), ExplosionLightFade, MobileParticleBudget,
             FpsReadout (in-headset frame counter, VRZ_NET_DIAGNOSTICS only)
  World/     TutorialManager (7-step tutorial), SessionMenu + HandUIPointerInput (create/join rooms with a hand
             laser), ScreenFader, SpawnPoint, PracticeGrenade, LobbyPrewarm, GameOverController (event-driven;
             game over / partner left / connection lost), AmbiencePlayer, FallCatcher, IgnoreCollisionsWithRoots,
             CollisionGroupIgnore (runtime group ignores for spawned prefabs), TerrainQualityProfile (per-platform
             grass budget), PlatformRenderTweaks, PhysicsStepMonitor (editor-only)
Assets/Tests/EditMode/ VRZ.Tests.EditMode.asmdef (NUnit, editor-only) -- Avatar/ (calibrator, smoother, elbow,
                 floor sampler), Rules/ (wave, grenade, zombie damage, spawn slot, room code), Fakes/FakePlayerState;
                 run via menu VRZ > Run EditMode Tests
Assets/Editor/   RunEditModeTests (TestRunnerApi one-shot for tooling/CI), DummyPartner (the editor as a stand-in
                 partner for one-headset tests, plus adb log/FPS pulls), ZombieMeshCombiner (merges the zombie's
                 skinned parts into one renderer)
Assets/Prefabs/  Rifle, magazines, grenade, zombies, network character/player, network runner, spawn points,
                 Auto Hand Local Character (the single local rig used by BOTH scenes), UI/SessionRow
Assets/Settings/ PC_RPAsset, Quest_RPAsset (URP), Quest_Renderer, LobbyLighting (lightmap settings)
Assets/Shaders/  AmmoText DepthTested (material for the 3D texts; its shader lives in Assets/TextMesh Pro/Shaders)
Assets/Scenes/   LobbyScene (entry, build index 0), GameScene (arena, build index 1)
```

## External Assets Required

This repo intentionally excludes licensed content. To run the project you need:

| Asset | Purpose |
|---|---|
| [Photon Fusion 2 SDK](https://www.photonengine.com/fusion) + your own App ID | Networking |
| [AutoHand 4](https://assetstore.unity.com/packages/tools/game-toolkits/auto-hand-vr-interaction-165323) | VR physics hands/interaction |
| [Final IK](https://assetstore.unity.com/packages/tools/animation/final-ik-14290) (RootMotion) | Full-body avatar solver (VRIK) |
| [Flooded Grounds](https://assetstore.unity.com/packages/3d/environments/flooded-grounds-48529) (Unity Asset Store, free) | Environment art |
| A zombie character pack (humanoid rig + clips) | Enemy model/animations |
| [Mixamo](https://www.mixamo.com) character (Ch32) | Player avatar (download free from Mixamo -- not redistributable) |
| [AllSky Free](https://assetstore.unity.com/packages/2d/textures-materials/sky/allsky-free-10-sky-skybox-set-146014) (Unity Asset Store, free) | Lobby skybox |
| [Free Horror Ambience 2](https://assetstore.unity.com/packages/audio/music/free-horror-ambience-2-215651) (Unity Asset Store, free) | Ambience audio |
| Abandoned Buildings by [Aleksey Kozhemyakin](https://assetstore.unity.com/publishers/12048) (Unity Asset Store, free) | Lobby environment (ruined house, stove, props) |
| [Zombie Massacre SFX Starter Pack](https://terrorbytegames.itch.io/zombie-massacre-sound-effects-starter-pack) by TerrorByteGames (itch.io, free) | Zombie death sounds |

Scenes will show missing references until these are imported.

## Setup

1. Clone the repo and open with **Unity 6 (URP)**.
2. Import the assets listed above into their original folders.
3. Create a Fusion App ID at photonengine.com and paste it into `PhotonAppSettings`.
4. **Fusion project config is not versioned** (`Assets/Photon*` is git-ignored with the SDK). In `NetworkProjectConfig` set: tick rate **32**, `Network > Connection Timeout` **25** s, and keep physics in Shared Mode configuration (`Auto` authority, Unity `FixedUpdate` timing, no `RunnerSimulatePhysics3D` on the runner).
5. Open `Assets/Scenes/LobbyScene` and press Play (or build two clients). One player chooses **Create Lobby** and reads out the two-digit room code; the other chooses **Join Lobby** and picks that room from the list. Both builds must share the same `Player Settings > Version`, because the room list is namespaced by it.
6. For Meta Quest, switch the build target to Android. The Android/OpenXR side is versioned and ready: OpenXR loader for Android with **Meta Quest Support** and the Touch / Touch Pro / Touch Plus controller profiles (Quest 2, 3 and 3S; Quest 1 is not targeted, and Quest Pro was dropped on purpose: with the foveated-rendering feature enabled, targeting the Pro makes the OpenXR package add an eye-tracking permission and a *required* eye-tracking feature to the manifest, which the game does not use), Single Pass Instanced, latency optimization on input polling; IL2CPP, ARM64, Vulkan only, ASTC, target SDK **34**, Internet permission *Require* (an online-only game must not rely on Unity's auto-detection), *Install Location* **Auto** and *Default Orientation* **Landscape Left** (the Meta Horizon Store's upload validator rejects Unity's defaults for both); the "Quest" quality level as the Android default, backed by `Quest_RPAsset` and its own `Quest_Renderer` (the PC renderer's SSAO is a full-screen pass per eye, so Quest gets a renderer without it); the Roslyn analyzer DLLs in `Assets/Plugins/Roslyn` are editor-only so they never enter the APK. Fusion and Feel add their own scripting defines to Android when the target is switched. Two things are yours to provide: a **custom keystore** (*Publishing Settings*; back it up outside the project, the debug keystore cannot update a published app) and the passwords. **Unity keeps the keystore passwords in memory only**: every script recompilation or domain reload (a code edit, a define change) clears them and the build fails with "Unable to sign the application; please provide passwords!". Do all code and configuration changes first, then enter the passwords, then build. Install with Meta Quest Developer Hub (drag the APK onto *Device Manager*) or `adb install -r`; the app appears under *Unknown Sources*. **OVR Metrics Tool** on the headset, in a six-zombie wave, is the reference for performance (target: 72 fps with GPU under ~85%); compositor log lines are not.

**Testing alone.** Add `VRZ_SOLO_TEST` (lobby and waves start with one player) and/or `VRZ_INVULNERABLE` to *Player Settings > Scripting Define Symbols* (per platform: adding it to Android does not touch Standalone); a warning is logged at startup while they are active. Remove them before a real build, and never commit `ProjectSettings.asset` with them in. A one-headset Quest test with `VRZ_SOLO_TEST` covers everything except what needs two screens (spectator, PARTNER score, "PARTNER LEFT"). Note that with it, two players in the same room start the match as soon as *one* finishes the tutorial.

**Netcode diagnostics.** Add `VRZ_NET_DIAGNOSTICS` to log spawn cost (`[Waves] Spawn took`), per-zombie path state (`[Zombie N]`), held-rifle authority and shot replication (`[NetGun]`), the snap-turn hand/controller trace (`[SnapTurnFix]`), an in-headset frame counter (`FpsReadout`: current fps, 5 s minimum and frame time at the bottom right of the view, plus a `[FPS]` log line every 5 s), and to enable the editor-only physics-step monitor (`[PhysStep]`: steps per frame, fps, and a profiler dump of any frame over 100 ms). They are compiled out otherwise, even in development builds, so the console stays readable and the editor does not pay for profiler recording; anomaly warnings (a dropped weapon authority, a snap turn that did not rotate) are always on in development builds.

**Testing with the editor as one client.** Fine for logic and networking, but let the *build* create the room (the master runs the zombie AI), and judge the feel of held objects only in builds: the editor at ~36 fps changes AutoHand's physics step.

**Testing with one headset: the editor as the partner.** `VRZ > Dev > Dummy Partner > Host` enters Play Mode, creates the room, marks the editor's player as tutorial-done and ready, and keeps its camera at standing height (without a headset it would lie on the floor); the headset joins that room as the non-master, which is the case that matters for most bugs. *Walk* moves the editor's avatar in a 2 m circle, *Turn* yaws its head, *Die* kills it (spectator, PARTNER score, grenades vanishing), *Leave* exits Play ("PARTNER LEFT"), and *God mode* (on by default) keeps it alive, because the dummy cannot defend itself and the zombies killing it ends the match. With the Quest on USB, *Pull Quest log* dumps logcat and prints the `[FPS]`, `[NetGun]`, `[DevFlags]`, `[NetworkManager]` lines, and *Quest FPS (VrApi)* summarizes the compositor's own per-second `FPS=72/72 ... Stale=N` lines, which tell you what the compositor delivered in that window but not what the GPU had left -- for the performance budget use OVR Metrics Tool on the headset (see Performance Lessons); raise the headset's log buffer first (`adb logcat -G 16M`, it resets to 256 KB on reconnect). Every adb call has a 15 s timeout, because adb blocks forever when the headset is asleep and that froze the editor once. Do not edit scripts while a hosted session runs.
