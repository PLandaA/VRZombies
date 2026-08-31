# VR Multiplayer Zombie Survival
### Photon Fusion 2 (Shared Mode) + AutoHand VR + Final IK (VRIK)

A cooperative VR zombie survival game for 2 players, built in Unity 6 (URP) with full physics-based hand interactions and a full-body networked avatar. This repository showcases the **networking and gameplay systems I built from scratch**: the integration between Photon Fusion 2 Shared Mode, the AutoHand VR interaction framework and Final IK.

> Third-party assets (Photon SDK, AutoHand, Final IK, art packs and the course base project) are **not included** -- see [External Assets](#external-assets-required).

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
- **Score system**: networked per-player score / kills / headshot counters, credited by the shooter with killing-blow prediction; **multi-kill combos** (DOUBLE / TRIPLE / MEGA KILL) on a rolling window; a wrist-mounted score+round HUD; end-of-run stats on the game-over screen.
- **Round drama**: "ROUND N" banner, two-layer music that crossfades with living-zombie pressure, a brief slow-motion beat (with tape-slow music pitch) when the last zombie of a wave falls, and an ambient lightning storm (skybox exposure + directional flash on a runtime material copy).
- **Night-fight lighting**: every shot fires a real 3-frame muzzle light that illuminates the scene, and each avatar carries a head lamp (replicated for free through the head IK target). Both are shadowless to spare the URP shadow atlas.
- **Procedural heartbeat**: below 35% health a lub-dub fades in and accelerates toward death. The clip is synthesized at runtime (two decaying sine sweeps, 62→38Hz) -- no audio asset involved.

### Physics Configuration (the big one)
- The template shipped the Fusion Physics Addon configured for client/server prediction (`Fusion` authority + `FixedUpdateNetwork` timing) -- a mode the docs mark as unsupported in Shared Mode, and which ran ALL physics at network tick rate. Migrating to `Auto` authority + Unity `FixedUpdate` timing (the documented Shared Mode pattern) moved hands and held objects to the real 72hz physics step: the single biggest interaction-fluidity win in the project.

### Atmosphere
- **AmbiencePlayer**: one random horror ambience track per run, chosen by the master and synced to all players through a single networked index (covers late join).

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

## Engineering Lessons (the bugs worth remembering)

- **AutoHand layers are collision rules, not labels.** Scenery placed on `Hand`/`Grabbable`/`HandPlayer` inherits AutoHand's runtime ignore-matrix and becomes walk-through despite a perfect collider. 46 props were on those layers; a layer audit plus a NavMesh rebake from `Default`-only colliders fixed both players and zombies.
- **Read the tracking rig in the right phase.** AutoHand moves camera and hand targets in `LateUpdate`; anything sampling them in `Update` sees a frame-old pose, and with physics stepping 0/1/2 times per frame that lag fluctuates into visible judder. Solvers must run after that write (VRIK ships at execution order 9998 -- we drive it explicitly).
- **Trust runtime geometry only.** Hand-model axes measured in edit mode were 49° off from the assembled runtime hand; every offset derived from them inherited the error. Live measurement (and finally, eyes in the headset) settled it.
- **`RaycastNonAlloc` buffers overflow silently.** A downward floor ray crossing a held rifle's collider cloud filled its 8-hit buffer with hand/weapon hits, dropped the ground, and fell back to a poisoned reference -- the body popped in sync with every step. Masking layers, widening the buffer and remembering the last good floor closed it.

## Project Structure (my code)

```
Assets/Scripts/
  Network/   NetworkManager (session, spawn teleport, player registry), LobbyManager (tutorial gate)
  Player/    NetworkRig (VRIK-driven scaled avatar, dual-source targets, live tuner), NetworkPlayer (health/score/stats),
             CharacterInputData (per-tick input struct), LocalAvatarHider (hide own head/neck/hands),
             PlayerBelt (per-round grenade budget), DamageVignette (radial hurt feedback)
  Weapons/   NetworkAutoGun (tick-synced shots, headshots), NetworkAutoAmmo (mag sync),
             NetworkGrenade (fuse/explosion/respawn), NetworkGunHitEffect, AmmoSpawner, FloatingWeapon
  Enemies/   NetworkZombie (NavMesh FSM + melee tuning), ZombieSpawner (waves), ZombieHitFlash
  FX/        WeaponFeel, ZombieJuice/ZombieFeel, PopupText, ScoreEvents, ScoreWristHUD,
             RoundJuice (banners/music/slow-mo), StormAmbience, BulletTracer, EmptyMagFeedback,
             MuzzleFlashLight, HeartbeatFeedback (procedural audio)
  World/     TutorialManager (7-step tutorial), PracticeGrenade, LobbyPrewarm, GameOverController,
             AmbiencePlayer, FallCatcher, IgnoreCollisionsWithRoots, DynamicTimestepSetter
Assets/Prefabs/  Rifle, magazines, grenade, zombies, network character/player
Assets/Scenes/   LobbyScene (entry, build index 0), GameScene (arena, build index 1)
```

## External Assets Required

This repo intentionally excludes licensed content. To run the project you need:

| Asset | Purpose |
|---|---|
| [Photon Fusion 2 SDK](https://www.photonengine.com/fusion) + your own App ID | Networking |
| [AutoHand 4](https://assetstore.unity.com/packages/tools/game-toolkits/auto-hand-vr-interaction-165323) | VR physics hands/interaction |
| [Final IK](https://assetstore.unity.com/packages/tools/animation/final-ik-14290) (RootMotion) | Full-body avatar solver (VRIK) |
| IronHeadVR Udemy course base project | Rig/session scaffolding this project extends |
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

## Credits

- Networking/gameplay integration: the repository author.
- Grenade explosion VFX and fog texture (`Assets/VFX/`): hand-made by the repository author.
- Base VR multiplayer scaffolding from the IronHeadVR Udemy course by [IronHead Games](https://www.udemy.com/user/ironhead-games/) and [Tevfik Ufuk Demirbas](https://www.udemy.com/user/tevfik-ufuk-demirbas/) (heavily modified -- original course content not included in this repo).
- AutoHand by Earnest Robot. Final IK by RootMotion. Photon Fusion by Exit Games.
- VR body/IK approach inspired by [Valem's tutorial](https://www.youtube.com/watch?v=v47lmqfrQ9s) (Complete VR Body Setup); the grab-aware dual-source targeting follows AutoHand's own VRIK integration example.
