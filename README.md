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
- **Shooter-authoritative damage**: only the shooter raycasts; damage is applied through an RPC to the zombie's state authority.

### Ammo Economy
- Magazines carry 100 rounds; a networked **AmmoSpawner** spawns fresh floating magazines between waves (state authority only) and despawns empty loose ones. Players physically eject the old mag and insert a new one.
- **FloatingWeapon** keeps loose guns/mags frozen midair until grabbed, and survives AutoHand's PlacePoint rigidbody destroy/recreate lifecycle (a subtle crash source I hardened against).

### Zombie AI + Wave System
- **NetworkZombie**: NavMesh-driven FSM (Idle / Chase / Attack / Dead) simulated on the session's master client. Animation speed always mirrors real agent velocity (normalized for the blend tree), so animation can never desync from movement.
- **Balanced aggro**: target picking deduplicates the local player (head camera vs. their own NetworkRig would otherwise double their probability) and periodically re-rolls targets so zombies distribute between both players.
- **ZombieSpawner**: wave/intermission loop driven by networked state, with guards against reading `[Networked]` properties before `Spawned()` (a recurring Fusion pitfall I guard against everywhere).

### Full-Body Networked Avatar
- Mixamo humanoid driven by **Unity Animation Rigging**: TwoBoneIK per arm (with elbow hints) + MultiParent head constraint.
- Only **3 IK target transforms are replicated** (via NetworkTransform); every client solves IK locally -- minimal bandwidth for a full-body presence.
- **Camera-anchored body**: the avatar's head bone is always exactly at the player's camera, with **automatic player-height scale calibration** (feet land on the floor when tracking is sane, view alignment is guaranteed always).
- The avatar's hand bones are collapsed so AutoHand's physical hands are THE visible hands (no double-hand overlap), and the local player's own head bone is hidden (VR first-person standard).

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
  Network/   NetworkManager (session, spawn teleport), LobbyManager (ready flow)
  Player/    NetworkRig (camera-anchored scaled avatar), NetworkPlayer (health/death),
             CharacterInputData (per-tick input struct), LocalAvatarHider (hide own head)
  Weapons/   NetworkAutoGun (tick-synced shots), NetworkAutoAmmo (mag sync),
             NetworkGunHitEffect (RPC impact FX), FloatingWeapon, AmmoSpawner (round economy)
  Enemies/   NetworkZombie (NavMesh FSM), ZombieSpawner (waves), ZombieHitFlash
  World/     AmbiencePlayer (synced random ambience)
Assets/Prefabs/  Rifle, magazines, zombie, network character/player
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
| [Free Horror Ambience 2](https://assetstore.unity.com/packages/audio/music/free-horror-ambience-2-215651) (Unity Asset Store, free) | Ambience audio |

Scenes will show missing references until these are imported.

## Setup

1. Clone the repo and open with **Unity 6 (URP)**.
2. Import the assets listed above into their original folders.
3. Create a Fusion App ID at photonengine.com and paste it into `PhotonAppSettings`.
4. Open `Assets/Scenes/LobbyScene`, press Play (or build two clients) -- sessions use **Shared Mode** with automatic matchmaking into the same room.

## Credits

- Networking/gameplay integration: the repository author.
- Base VR multiplayer scaffolding from the IronHeadVR Udemy course by [IronHead Games](https://www.udemy.com/user/ironhead-games/) and [Tevfik Ufuk Demirbas](https://www.udemy.com/user/tevfik-ufuk-demirbas/) (heavily modified -- original course content not included in this repo).
- AutoHand by Earnest Robot. Photon Fusion by Exit Games.
- VR body/IK approach inspired by [Valem's tutorial](https://www.youtube.com/watch?v=v47lmqfrQ9s) (Complete VR Body Setup).
