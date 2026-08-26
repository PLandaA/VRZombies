# VR Multiplayer Zombie Survival
### Photon Fusion 2 (Shared Mode) + AutoHand VR Integration

A cooperative VR zombie survival game for 2 players, built in Unity 6 (URP) with full physics-based hand interactions. This repository showcases the **networking and gameplay systems I built from scratch**: the integration between Photon Fusion 2 Shared Mode and the AutoHand VR interaction framework.

> Third-party assets (Photon SDK, AutoHand, art packs and the course base project) are **not included** -- see [External Assets](#external-assets-required).

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

### Full-Body Networked Avatar
- Mixamo humanoid driven by **Unity Animation Rigging**: TwoBoneIK per arm (with elbow hints) + MultiParent head constraint.
- Only **3 IK target transforms are replicated** (via NetworkTransform); every client solves IK locally -- minimal bandwidth for a full-body presence.
- **Camera-anchored body**: the avatar's head bone is always exactly at the player's camera, with **automatic player-height scale calibration** (feet land on the floor when tracking is sane, view alignment is guaranteed always).
- The local player's own avatar head and hands are hidden (AutoHand's physical hands are YOUR hands), while **remote players see the avatar's full body including its hands** -- wrist rotation offsets were **calibrated analytically** by measuring the bone geometry (finger direction + palm normal via cross products) and solving the quaternion that maps the Mixamo wrist frame onto the AutoHand hand frame.

### Lobby & Interactive Tutorial
- Six-step in-world weapon tutorial (grab, load, rack the slide, fire, throw a grenade, ready) with head-following signs and **order-of-appearance**: grenades and magazines only materialize at their step, so nobody can skip ahead.
- Event filtering that survives Shared Mode: tutorial steps only advance from events on objects **physically held by the local player's hands** (networked weapon events replicate to every client, and un-grabbed scene objects report the master as their authority -- both would otherwise advance the partner's tutorial).
- Networked readiness gate: the round starts only when every connected player has finished the tutorial, with a countdown and a hitch-free scene transition (shader/asset prewarm on lobby load).

### Game Feel & Scoring
- **Kill celebration** on every client: blood puff, floating score popups ("+10" / "+25 HEADSHOT!"), native per-zombie death groans and desynced 3D shamble loops.
- **Score system**: networked per-player score / kills / headshot counters, credited by the shooter with killing-blow prediction; **multi-kill combos** (DOUBLE / TRIPLE / MEGA KILL) on a rolling window; a wrist-mounted score+round HUD; end-of-run stats on the game-over screen.
- **Round drama**: "ROUND N" banner, two-layer music that crossfades with living-zombie pressure, a brief slow-motion beat (with tape-slow music pitch) when the last zombie of a wave falls, and an ambient lightning storm (skybox exposure + directional flash on a runtime material copy).

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
| Avatar sync | 3 NetworkTransforms (IK targets) + local IK solving on each client |

## Project Structure (my code)

```
Assets/Scripts/
  Network/   NetworkManager (session, spawn teleport, player registry), LobbyManager (tutorial gate)
  Player/    NetworkRig (camera-anchored scaled avatar), NetworkPlayer (health/score/stats),
             CharacterInputData (per-tick input struct), LocalAvatarHider (hide own head+hands),
             PlayerBelt (per-round grenade budget), DamageVignette (radial hurt feedback)
  Weapons/   NetworkAutoGun (tick-synced shots, headshots), NetworkAutoAmmo (mag sync),
             NetworkGrenade (fuse/explosion/respawn), NetworkGunHitEffect, AmmoSpawner, FloatingWeapon
  Enemies/   NetworkZombie (NavMesh FSM + melee tuning), ZombieSpawner (waves), ZombieHitFlash
  FX/        WeaponFeel, ZombieJuice/ZombieFeel, PopupText, ScoreEvents, ScoreWristHUD,
             RoundJuice (banners/music/slow-mo), StormAmbience, BulletTracer, EmptyMagFeedback
  World/     TutorialManager (6-step tutorial), PracticeGrenade, LobbyPrewarm, GameOverController,
             AmbiencePlayer, FallCatcher, IgnoreCollisionsWithRoots
Assets/Prefabs/  Rifle, magazines, grenade, zombies, network character/player
Assets/Scenes/   LobbyScene (entry, build index 0), GameScene (arena, build index 1)
```

## External Assets Required

This repo intentionally excludes licensed content. To run the project you need:

| Asset | Purpose |
|---|---|
| [Photon Fusion 2 SDK](https://www.photonengine.com/fusion) + your own App ID | Networking |
| [AutoHand 4](https://assetstore.unity.com/packages/tools/game-toolkits/auto-hand-vr-interaction-165323) | VR physics hands/interaction |
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
- AutoHand by Earnest Robot. Photon Fusion by Exit Games.
- VR body/IK approach inspired by [Valem's tutorial](https://www.youtube.com/watch?v=v47lmqfrQ9s) (Complete VR Body Setup).
