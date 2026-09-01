# VR Multiplayer Zombie Survival
### Photon Fusion 2 (Shared Mode) + AutoHand VR + Final IK (VRIK)

A cooperative VR zombie survival game for 2 players, built in Unity 6 (URP) with full physics-based hand interactions and a full-body networked avatar. This repository showcases the **networking and gameplay systems I built from scratch**: the integration between Photon Fusion 2 Shared Mode, the AutoHand VR interaction framework and Final IK.

> Third-party assets (Photon SDK, AutoHand, Final IK and art packs) are **not included** -- see [External Assets](#external-assets-required). Everything under `Assets/Scripts/` is original or rewritten code.

---

## What I Built

### Networked Weapon System
- **Authority-on-grab**: whoever grabs the rifle takes StateAuthority over it (requested in AutoHand's OnBeforeGrab event), so the holder simulates their own weapon with zero input latency.
- **Shot replication without RPC spam**: the shooter raises a `[Networked] int LastShootTick`; remote clients detect the change via `ChangeDetector` in Render and play muzzle flash + 3D positional audio exactly once. State-based replication also covers late joiners.
- **Hit effects with payload**: bullet impacts are transient events with data (hit point + normal), so they use an RPC (`InvokeLocal = false` to avoid double effects on the shooter) -- following Fusion's state-vs-event guidelines.
- **Shooter-authoritative damage with geometric headshots**: only the shooter raycasts; damage goes through an RPC to the zombie's state authority. Headshots are detected by impact-point distance to the head bone (trigger hitboxes are invisible to AutoHand's ray) and deal 2x damage with a distinct hitmarker.
- **Weapon feel**: recoil + slide punch, hitmarker audio/haptics (pitched up on headshots), shell casing ejection, bullet tracers (exact hit point locally, muzzle ray for remote shots), dry-fire double-tick haptics, and high-friction physics material so dropped weapons settle instead of ice-skating.

### Ammo Economy
- Magazines carry 100 rounds; a networked **AmmoSpawner** spawns fresh floating magazines between waves (state authority only) and despawns empty loose ones. Players physically eject the old mag and insert a new one.
- **FloatingWeapon** keeps loose guns/mags frozen midair until grabbed, and survives AutoHand's PlacePoint rigidbody destroy/recreate lifecycle (a subtle crash source I hardened against).

### Zombie AI + Wave System
- **NetworkZombie**: NavMesh-driven FSM (Idle / Chase / Attack / Retreat / Dead) simulated on the session's master client. Animation speed always mirrors real agent velocity (normalized for the blend tree), so animation can never desync from movement.
- **Melee tuned for VR players**: attack-range hysteresis (enter at R, drop only beyond 1.4R) stops constantly-strafing players from flickering the state machine and cancelling swings; the bite lands mid-animation (0.45s into the swing) and can be dodged by stepping back in time.
- **Robust target lifecycle**: dead or unresolvable players are never valid targets; targetless zombies wander instead of freezing; on game over the horde force-exits any in-progress attack (dedicated Retreat state + animator CrossFade) and shambles away.
- **Balanced aggro**: target picking deduplicates the local player (head camera vs. their own NetworkRig would otherwise double their probability) and periodically re-rolls targets so zombies distribute between both players.
- **ZombieSpawner**: wave/intermission loop driven by networked state, with guards against reading `[Networked]` properties before `Spawned()` (a recurring Fusion pitfall I guard against everywhere).

### Full-Body Networked Avatar (Final IK / VRIK)
- Mixamo humanoid driven by **Final IK's VRIK** (the solver AutoHand officially supports), migrated from Unity Animation Rigging. The swap replaced only the solver underneath: the **same 3 IK target transforms** (head + both hands) are the only replicated data (NetworkTransform); every client solves the full body locally.
- **Dual-source, grab-aware hand targets** (pattern borrowed from AutoHand's own VRIK integration): with empty hands the arm IK follows the *controller frame* (zero physics lag), while holding an object it follows the *physical hand* (which sits at the object's grab pose). Sleeves stay welded to the hand whether you're pointing or reloading.
- **Camera-anchored body with anatomical head pivot**: the head bone sits 8cm behind the eyes in head space (where a skull actually rotates), which also keeps the collar out of view when looking down. Player-height scale calibration is grounded on a smoothed, layer-masked floor raycast, and arm reach is corrected through VRIK's native `armLengthMlp` (height alone cannot capture arm span).
- **Phase-correct solving**: AutoHand moves the tracking rig in `LateUpdate`, so the rig reads the camera *after* that (execution order 9999) and drives VRIK explicitly via `UpdateSolverExternal()` -- exactly one solve per frame with fresh targets. Reading the camera one phase early produced a walking judder that no smoothing could hide.
- Wrist offsets were **tuned by eye inside the headset** with an editor-only live keyboard tuner (rotation / position / elbow / arm length, printed on demand and baked into the prefab). Analytical calibration from edit-mode geometry was tried first and proved fragile: AutoHand re-assembles the hand model at runtime, so only runtime truth counts.
- The local player's own avatar head, neck and hands are hidden (AutoHand's physical hands are YOUR hands); the partner sees the full body, including a head-mounted lamp that follows their gaze.

### Lobby & Interactive Tutorial
- Seven-step in-world weapon tutorial (trigger grip, support hand on the front grip, load, rack the slide, fire, throw a grenade, ready) with head-following signs and **order-of-appearance**: grenades and magazines only materialize at their step, so nobody can skip ahead. The two grab steps listen to the rifle's two distinct AutoHand grabbables (pistol-grip `Handle` vs. body `Core`), so the tutorial teaches dominant-hand-first for free.
- Event filtering that survives Shared Mode: tutorial steps only advance from events on objects **physically held by the local player's hands** (networked weapon events replicate to every client, and un-grabbed scene objects report the master as their authority -- both would otherwise advance the partner's tutorial).
- Networked readiness gate: the round starts only when every connected player has finished the tutorial, with a countdown and a hitch-free scene transition (shader/asset prewarm on lobby load).

### Game Feel & Scoring
- **Kill celebration** on every client: blood puff, floating score popups ("+10" / "+25 HEADSHOT!"), native per-zombie death groans and desynced 3D shamble loops.
- **Score system**: networked per-player score / kills / headshot counters, credited by the shooter with killing-blow prediction; **multi-kill combos** (DOUBLE / TRIPLE / MEGA / MULTIKILL) on a rolling window, coalesced per frame so a grenade wipe reads as one banner; a wrist-mounted score+round HUD; end-of-run stats on the game-over screen.
- **Round drama**: "ROUND N" banner, two-layer music that crossfades with living-zombie pressure, a brief slow-motion beat (with tape-slow music pitch) when the last zombie of a wave falls, and an ambient lightning storm (skybox exposure + directional flash on a runtime material copy).
- **Night-fight lighting**: every shot fires a real 3-frame muzzle light that illuminates the scene, and each avatar carries a head lamp (replicated for free through the head IK target). Both are shadowless to spare the URP shadow atlas.
- **Procedural heartbeat**: below 35% health a lub-dub fades in and accelerates toward death. The clip is synthesized at runtime (two decaying sine sweeps, 62→38Hz) -- no audio asset involved.

### Physics Configuration (the big one)
- The template shipped the Fusion Physics Addon configured for client/server prediction (`Fusion` authority + `FixedUpdateNetwork` timing) -- a mode the docs mark as unsupported in Shared Mode, and which ran ALL physics at network tick rate. Migrating to `Auto` authority + Unity `FixedUpdate` timing (the documented Shared Mode pattern) moved hands and held objects to the real 72hz physics step: the single biggest interaction-fluidity win in the project.

### Atmosphere
- **AmbiencePlayer**: one random horror ambience track per run, chosen by the master and synced to all players through a single networked index (covers late join).

### Quest-Ready Configuration
- **Dedicated render path**: a `Quest_RPAsset` URP asset behind a "Quest" quality level that is the Android default -- MSAA 4x (nearly free on tiled GPUs, essential for VR edges), no HDR, 25m shadows in 2 cascades, 3 per-pixel additional lights (two head lamps + a muzzle flash), and no depth/opaque texture passes. PC keeps its own asset untouched.
- **Android settings Meta expects**: Vulkan only, IL2CPP/ARM64, minSdk 29, ASTC texture subtarget, multithreaded rendering.
- **Memory diet**: the 22 long ambience/music clips stream (Vorbis 0.45) instead of sitting decompressed in RAM (~65MB of PCM each); 224 SFX capped at Vorbis 0.6; the 82 textures authored at 4096px carry an Android-only override to 2048 (about 4x less VRAM per texture on the headset).
- **Static work moved offline**: occlusion culling baked for both scenes (1.4MB of Umbra data for the arena), the lobby's four point lights baked into lightmaps instead of casting realtime soft shadows (which alone saturated the 2048 shadow atlas with 24 shadow maps), and 142 redundant colliders stripped from LOD1/LOD2 renderers across 118 LODGroups.
- **Allocation-free firefights**: pooled impact/muzzle particles and popups, pooled 3D audio voices, and an event-driven wave counter instead of per-tick scene scans (see Design & Architecture Decisions).

---

## Architecture Notes (Fusion 2 Shared Mode)

| Concern | Pattern used |
|---|---|
| Persistent state (health, wave, ammo, shot tick) | `[Networked]` properties + `ChangeDetector` |
| Transient events with payload (impacts, damage) | RPCs, `InvokeLocal = false` when the sender already played locally |
| Object ownership | StateAuthority transferred on grab; master client simulates zombies/waves |
| Pre-spawn safety | Every `[Networked]` read is guarded with `Object != null && Object.IsValid` |
| Avatar sync | 3 NetworkTransforms (IK targets) + VRIK solved locally on each client; interpolation enabled per authority (proxies interpolate, the owner writes live) |
| Player spawn handshake | `NetworkManager` fires `onPlayerSpawn` only once the scene's map has subscribed -- Fusion can deliver `OnPlayerJoined` in the very frame a scene finishes loading |

## Design & Architecture Decisions

- **Dirty flag where the scans were.** The wave spawner's tick loop used to call `FindObjectsByType` three to four times per tick (players for the game-over check, zombies twice for the alive count) -- 100-240 full scene scans per second on the host. Players now come from the registry the manager already maintains on join/leave, and the alive count is cached behind a flag that only flips on spawn and on `NetworkZombie.OnAnyDied`. The other candidates were audited and left alone: `RoundJuice` and the heartbeat already throttle, and the floor raycast's input changes every frame, so a flag there would just be an extra branch.
- **Damage is a contract, not a type check.** Every damage source (rifle, grenade blast, zombie melee) talks only to `IDamageable { IsAlive, Health, Position, IsCriticalHit(point), ApplyDamage(in DamageInfo) }`. The *target* decides what counts as a critical hit (a zombie knows where its head is), and exposes read-only health so sources can predict a killing blow for instant local feedback before the authoritative RPC lands. A barrel or a destructible prop joins the game by implementing the interface -- no weapon code changes (Open/Closed).
- **The avatar driver is an orchestrator, not a god class.** `NetworkRig` owns the network lifecycle, the tracking-source selection and the single pose pipeline; the sub-problems are small focused collaborators with one reason to change each: `FloorSampler` (masked, buffered, smoothed ground), `AvatarScaleCalibrator` (height scale + body root, pure logic returning a `Result` struct), `PoseSmoother` (framerate-independent filter), `ElbowPole` (bend-plane geometry) and an editor-only `partial` for the live tuner. The pure classes take `dt` as a parameter and touch no Unity objects, so they are unit-testable without a scene or a network session.
- **Composition over inheritance for game feel.** Weapon feedback (`WeaponFeel`, `BulletTracer`, `EmptyMagFeedback`, `MuzzleFlashLight`) and player feedback (`DamageVignette`, `HeartbeatFeedback`) are independent components subscribing to events (`AutoGun.OnShoot`, `NetworkPlayer.OnDamagedFrom`); the systems they decorate never learn they exist.
- **Adapter at the input boundary.** `HardwareRig` implements Fusion's `INetworkRunnerCallbacks.OnInput` and packs AutoHand's tracked transforms into the `CharacterInputData` struct -- the only place where the two frameworks meet.
- **Template method for scene flow.** `MapDefault` defines *how* the avatar is spawned; `LobbyMap` and `GameMap` decide *when* (player-joined vs. scene-loaded), each hooking a different `NetworkManager` event.
- **Defensive construction on woven types.** Fusion's IL weaver was observed producing a `NetworkBehaviour` instance whose field initializers never ran; helper objects are therefore created lazily on first use (`??=`) instead of at field declaration.
- **Pooled transients.** Gunfire is hitscan (no projectiles), but every shot used to `Instantiate`/`Destroy` impact particles on all clients, remote muzzle effects and TextMeshPro score popups -- ~20 allocations per second in a two-player firefight, which reads as micro-stutter on a headset. `PrefabPool` (generic, self-returning after a lifetime, replays particle systems on reuse) and a dedicated popup pool remove that churn; both reset their static state on subsystem registration so disabled domain reload in the editor can't leak instances between plays.
- **Feedback goes through an event queue, gameplay does not.** Fusion itself is state-replication first (RPCs are its only queue-like part), so networked gameplay stays synchronous and authoritative. Presentation is different: a grenade wiping six zombies in one tick used to fire six overlapping popups and six identical death sounds. `FeedbackQueue` buffers kill and sound events and flushes once per frame in `LateUpdate` with coalescing (same-frame kills become one "MULTIKILL x6" banner and a single combo step) and per-clip budgeting (max two plays of the same clip per frame, pitch-varied, through a pool of twelve 3D voices instead of `PlayClipAtPoint`'s throwaway GameObjects).
- **Gameplay depends on contracts, not on the network layer.** Wave logic, HUDs, weapons and feedback read the session through `INetworkSession` / `IPlayerState` and send presentation through `IFeedbackSink`; the concrete `NetworkManager` and `FeedbackQueue` REGISTER themselves at startup behind two injection points (`NetworkSession.Current`, `Feedback.Sink`) that ship with no default. `NullNetworkSession` and `SilentFeedbackSink` let wave or scoring logic run in a test or an offline scene with no Fusion and no audio at all. A full Service Locator was deliberately *not* introduced: with two services and one implementation each, it would only hide the dependencies that `grep` currently makes obvious.
- **The dependency direction is compiler-enforced.** All code lives under `VRZ.*` namespaces by folder (`VRZ.Core`, `VRZ.Network`, `VRZ.Player`, `VRZ.Weapons`, `VRZ.Enemies`, `VRZ.FX`, `VRZ.World`), and `VRZ.Core` -- the contracts, the damage model, the pool -- is a separate assembly (`VRZ.Core.asmdef`) that references only Fusion. Core physically *cannot* see gameplay code: any accidental Core -> game dependency is a compile error, not a code-review catch. The rest of the game intentionally stays in `Assembly-CSharp` because Final IK ships without an asmdef (it compiles into the firstpass assembly, which custom assemblies cannot reference).
- **Pure logic is a separate assembly so it can be tested.** A test assembly cannot reference `Assembly-CSharp`, so the avatar helpers live in their own `VRZ.Avatar.asmdef` with an *empty* reference list (engine only). Getting there forced two small inversions: `FloorSampler` no longer knows what AutoHand or the rig are -- the rig injects an `ignoreCollider` predicate -- and `ElbowPole` exposes a pure `TryCompute` over positions and basis vectors, with `Aim` reduced to a `Transform` adapter. The wave rules (`CountReady`, `AllPlayersDead`, `ZombiesForWave`) were lifted out of the `NetworkBehaviour` into `VRZ.Core.WaveRules` for the same reason. `Assets/Tests/EditMode` holds 42 NUnit EditMode tests (calibration snap/clamp/freeze/desk, filter framerate independence, elbow bend-plane geometry, wave rules against a `FakePlayerState` and the real `NullNetworkSession`); `VRZ > Run EditMode Tests` runs them in under a second.

## Engineering Lessons (the bugs worth remembering)

- **AutoHand layers are collision rules, not labels.** Scenery placed on `Hand`/`Grabbable`/`HandPlayer` inherits AutoHand's runtime ignore-matrix and becomes walk-through despite a perfect collider. 46 props were on those layers; a layer audit plus a NavMesh rebake from `Default`-only colliders fixed both players and zombies.
- **Read the tracking rig in the right phase.** AutoHand moves camera and hand targets in `LateUpdate`; anything sampling them in `Update` sees a frame-old pose, and with physics stepping 0/1/2 times per frame that lag fluctuates into visible judder. Solvers must run after that write (VRIK ships at execution order 9998 -- we drive it explicitly).
- **Trust runtime geometry only.** Hand-model axes measured in edit mode were 49° off from the assembled runtime hand; every offset derived from them inherited the error. Live measurement (and finally, eyes in the headset) settled it.
- **`RaycastNonAlloc` buffers overflow silently.** A downward floor ray crossing a held rifle's collider cloud filled its 8-hit buffer with hand/weapon hits, dropped the ground, and fell back to a poisoned reference -- the body popped in sync with every step. Masking layers, widening the buffer and remembering the last good floor closed it.

## Project Structure (my code)

```
Assets/Scripts/
  Network/   NetworkManager (runner lifecycle, player registry, spawn handshake), LobbyManager (ready-up + scene load),
             MapDefault/GameMap/LobbyMap (avatar spawn per scene)
  Core/      VRZ.Core.asmdef (independent assembly -- contracts cannot depend on gameplay),
             IDamageable + DamageInfo, INetworkSession + IPlayerState (+ injection points,
             Null/Silent implementations), IFeedbackSink, PrefabPool, Rules/WaveRules (pure wave logic)
  Player/    NetworkRig (avatar orchestration) + NetworkRig.LiveTuner (editor partial),
             Avatar/ VRZ.Avatar.asmdef (engine-only assembly) { FloorSampler, AvatarScaleCalibrator,
                     PoseSmoother, ElbowPole, IKTarget },
             HardwareRig (AutoHand -> Fusion input adapter), NetworkPlayer (health/score/stats),
             CharacterInputData (per-tick input struct), LocalAvatarHider (hide own head/neck/hands),
             PlayerBelt (per-round grenade budget), DamageVignette (radial hurt feedback)
  Weapons/   NetworkAutoGun (tick-synced shots, headshots), NetworkAutoAmmo (mag sync),
             NetworkGrenade (fuse/explosion/respawn), NetworkGunHitEffect, AmmoSpawner, FloatingWeapon
  Enemies/   NetworkZombie (NavMesh FSM + melee tuning, IDamageable), ZombieSpawner (waves, dirty-flag counts),
             ZombieHitFlash, HeadshotHitbox
  FX/        FeedbackQueue (coalescing/budgeted presentation, self-registers as the sink),
             WeaponFeel, ZombieJuice/ZombieFeel, PopupText (pooled), ScoreEvents, ScoreWristHUD,
             RoundJuice (banners/music/slow-mo), StormAmbience, BulletTracer, EmptyMagFeedback,
             MuzzleFlashLight, HeartbeatFeedback (procedural audio), ExplosionLightFade, MobileParticleBudget
  World/     TutorialManager (7-step tutorial), SpawnPoint, PracticeGrenade, LobbyPrewarm, GameOverController,
             AmbiencePlayer, FallCatcher, IgnoreCollisionsWithRoots, DynamicTimestepSetter
Assets/Tests/EditMode/ VRZ.Tests.EditMode.asmdef (NUnit, editor-only) -- Avatar/ (calibrator, smoother, elbow),
                 Rules/ (wave rules), Fakes/FakePlayerState; run via menu VRZ > Run EditMode Tests
Assets/Editor/   RunEditModeTests (TestRunnerApi one-shot for tooling/CI)
Assets/Prefabs/  Rifle, magazines, grenade, zombies, network character/player, network runner, spawn points
Assets/Settings/ PC_RPAsset, Quest_RPAsset (URP), LobbyLighting (lightmap settings)
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
4. Open `Assets/Scenes/LobbyScene`, press Play (or build two clients) -- sessions use **Shared Mode** with automatic matchmaking into the same room.
5. For Meta Quest: switch the build target to Android (the "Quest" quality level, ASTC subtarget and Vulkan-only settings are already configured) and Build & Run.

## Credits

- Networking/gameplay integration: the repository author.
- Grenade explosion VFX and fog texture (`Assets/VFX/`): hand-made by the repository author.
- The session/spawn scaffolding (`HardwareRig`, the Map classes, `SpawnPoint`) started from the IronHeadVR Udemy course by [IronHead Games](https://www.udemy.com/user/ironhead-games/) and [Tevfik Ufuk Demirbas](https://www.udemy.com/user/tevfik-ufuk-demirbas/); it has been rewritten for AutoHand and Fusion 2 and the course content itself is not included.
- AutoHand by Earnest Robot. Final IK by RootMotion. Photon Fusion by Exit Games.
- VR body/IK approach inspired by [Valem's tutorial](https://www.youtube.com/watch?v=v47lmqfrQ9s) (Complete VR Body Setup); the grab-aware dual-source targeting follows AutoHand's own VRIK integration example.
- Development tooling: `Packages/manifest.json` references `com.coplaydev.unity-mcp`, an optional editor-time MCP bridge used during development. It is safe to remove from the manifest.
