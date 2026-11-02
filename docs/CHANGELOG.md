# Changelog

Builds that reached a headset, newest first. The *why* behind most entries is in [Engineering Lessons](ENGINEERING_LESSONS.md).

## 0.3.4 (2026-10-16) — Meta Horizon Store resubmission, approved 2026-10-20

- Quest Pro removed from the target devices: with the foveated-rendering feature enabled, targeting the Pro made the OpenXR package add `com.oculus.permission.EYE_TRACKING` and a *required* eye-tracking feature to the manifest, which the game does not use and which would have limited the store listing to headsets with eye tracking.
- 2026-10-22: OpenXR plugin 1.16.1 → 1.18.0; the upgrade reset the foveation API to *Legacy*, restored to *SRP Foveation*.

## 0.3.3 (2026-10-15) — performance pass after the first store review

The first submission was rejected for running below 60 fps; OVR Metrics on a Quest 3S confirmed 30-32 fps with the GPU at 95%. One change per measurement:

- Render scale 1.4 → 1.0 (50-56 fps).
- Post-processing disabled on the Quest renderer (59-72 fps); the look of the profile's Color Adjustments is baked into sky, fog and lights at load by the new `PostLookBaker`.
- Main-light shadows disabled on Quest (68-73 fps).
- Fixed foveated rendering at level 1.0 via the new `FoveatedRenderingBootstrap` (70-72 fps, GPU 80-85%).
- Zombie Animator set to *Always Animate*: retreating zombies glided with frozen legs because Unity culled bone updates during the game-over view.
- Three audio references restored (arena calm music layer, lobby music, two random ambience slots) that had gone silent when the ambience pack was reorganised outside Unity.
- Thunder follows the flash after 0.3-1.0 s instead of 1.5-4 s.
- Trailer re-edited: no close-up headshots, at most one per 30 s (store content policy).

## 0.3.2 (2026-10-11) — first Meta Horizon Store submission

- *Install Location* → Auto and *Default Orientation* → Landscape Left: the store's upload validator rejects Unity's defaults for both.
- Manifest permissions audited with `aapt` against Meta's prohibited and review-requiring lists.
- Privacy policy published (GitHub Pages); User Reporting Service enabled for the co-op mode.
- SideQuest listing submitted with the same build.

## 0.3.1 (2026-10-08) — feedback from the first two-headset session

- Remote bullet tracers end at the real impact point (`LastShootPoint` replicated with the shot tick), 180 ms and 1.5x wider, so the partner can actually see them.
- Rifles float where they are released (`WeaponFloatOnRelease`); re-grabbing re-seeds the network buffer (`NetworkRigidbody3D.Teleport`) so the rifle does not shake.
- Zombies fan out onto a ring of attack posts instead of queuing behind each other (6 of 6 in bite range, previously 1-2); avoidance priorities randomised per life.
- Game-over retreat without stutter: one destination every 4 s, projected onto the NavMesh, instead of one `SetDestination` per tick.
- Lobby floor and rifle table given solid box colliders: walking into the 3 cm plank the rifles rest on pushed the player's body half a metre into the mesh floor.
- Lightning flash brighter (sky +7.5, scene light 8); "contains flashing lights" added to the store descriptions.
- Tutorial skippable with A on the right controller; the final sign reads differently in Solo and co-op.
- One magazine per dispenser: each marker is judged on its own between waves (the old rule counted full magazines across the whole map, so the second dispenser rarely got one).
- Zombie NavMesh recovery: unconditional re-registration on spawn, warp-and-retry on a refused path, chase watchdog; targets projected to the feet.
- Avatar head pivot 8 cm → 12 cm behind the eyes (measured on the mesh).

## 0.3.0 (2026-10-07) — two headsets can finally meet

- Photon region fixed to `us`: with it empty, two headsets picked different "best" regions and could not see each other's rooms.
- Quest quality level fix: the "Quest" level had Android in its excluded platforms (inherited when it was cloned from the PC level), so every APK before this one silently shipped the *Mobile* URP asset (render scale 0.8, no MSAA).

## 0.2.0 (2026-10-07) — first build with the real Quest render path

- MCP tooling kept out of the APK; grenade throw power 1.8 → 1.5.

## Before 0.2.0 (August – September 2026)

- 2026-10-05: Solo Survival (Fusion `Single` mode, same code, offline).
- Late September: netcode audit of the 15 networked systems and the fixes it prioritised (play again without restarting, room codes instead of random matchmaking, strict co-op disconnect rules, test values removed from scenes); NUnit EditMode suite (83 tests); `VRZ.Core` and `VRZ.Avatar` assemblies.
- September: death spectator, kill credit from `RpcInfo.Source`, feedback queue, pooled transients, zombie mesh combiner, grass and lighting budgets, lobby lighting and tutorial polish.
- August: Fusion 2 Shared Mode + AutoHand integration, networked rifle and magazines, full-body VRIK avatar, wave system, lobby with room codes.
