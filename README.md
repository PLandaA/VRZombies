# VR Multiplayer Zombie Survival
### Photon Fusion 2 (Shared Mode) + AutoHand VR + Final IK (VRIK)

A cooperative VR zombie survival game for 2 players, built in Unity 6 (URP) with full physics-based hand interactions and a full-body networked avatar. This repository showcases the **networking and gameplay systems I built from scratch**: the integration between Photon Fusion 2 Shared Mode, the AutoHand VR interaction framework and Final IK.

> Third-party assets (Photon SDK, AutoHand, Final IK and art packs) are **not included** -- see [External Assets](#external-assets-required). Everything under `Assets/Scripts/` is original or rewritten code.

---

## What I Built

### Networked Weapon System
- **Authority-on-approach**: the rifle requests StateAuthority as soon as a hand *highlights* it (the round trip completes while the arm is still reaching, so the first grab never feels heavy), re-requests while held if the transfer was dropped, and hands the loaded magazine over with it. Grenades are deliberately **personal**: only the client that spawned them can grab them (`Grabbable.isGrabbable` bound to authority), so a partner never ends up holding an object whose pose is replicated from someone else's belt.
- **Round in the chamber is replicated**: AutoHand keeps "slide loaded" as a private per-client flag, so a rifle racked by one player would not fire for the other. The owner mirrors it into a `[Networked] NetworkBool`, and proxies copy it back into their `AutoGun` by reflection (calling `LoadSlide()` would consume a round).
- **Shot replication without RPC spam**: the shooter raises a `[Networked] int LastShootTick`; remote clients detect the change via `ChangeDetector` in Render and play muzzle flash + 3D positional audio exactly once. State-based replication also covers late joiners.
- **Hit effects with payload**: bullet impacts are transient events with data (hit point + normal), so they use an RPC (`InvokeLocal = false` to avoid double effects on the shooter) -- following Fusion's state-vs-event guidelines. The RPC source is `All`, not `StateAuthority`: right after grabbing someone else's rifle the shooter is still a proxy for one round trip, and a StateAuthority-only RPC would silently drop those first impacts.
- **Shooter-authoritative damage with geometric headshots**: only the shooter raycasts; damage goes through an RPC to the zombie's state authority. Headshots are detected by impact-point distance to the head bone (trigger hitboxes are invisible to AutoHand's ray) and deal 2x damage with a distinct hitmarker.
- **Weapon feel**: recoil + slide punch, hitmarker audio/haptics (pitched up on headshots), shell casing ejection, bullet tracers (exact hit point locally, muzzle ray for remote shots), dry-fire double-tick haptics, and high-friction physics material so dropped weapons settle instead of ice-skating.

### Ammo Economy
- Magazines carry 100 rounds; a networked **AmmoSpawner** spawns fresh floating magazines between waves (state authority only) and despawns empty loose ones. Players physically eject the old mag and insert a new one. Size comes from the prefab, never from the spawn marker: the spawner runs only on the master and `SyncScale` is off, so a marker-driven scale would differ between screens.
- **The loaded magazine follows the rifle on every screen**: the owner replicates which magazine is inserted as a `NetworkBehaviourId`; proxies parent their copy under their own magazine well and **unparent it again on removal**. Because `NetworkTransform` replicates local coordinates, a magazine left parented to the partner's rifle rendered at "rifle position + the owner's world coordinates" -- a floating, ungrabbable magazine somewhere else in the map.
- **Belt grenades stay on the belt**: the belt writes the grenade's Rigidbody pose as well as its transform every frame. AutoHand's PlacePoint releases a placed object whose *physics* body no longer overlaps it, and a transform-only write left the collider one physics step behind -- a snap turn or a quick step dropped the grenade where the player had been. A safety net re-seats any belt grenade that comes loose without being thrown (after a grace period longer than the arming delay, so real throws are never pulled back).
- **FloatingWeapon** keeps loose guns/mags frozen midair until grabbed, and survives AutoHand's PlacePoint rigidbody destroy/recreate lifecycle (a subtle crash source I hardened against).

### Zombie AI + Wave System
- **NetworkZombie**: NavMesh-driven FSM (Idle / Chase / Attack / Retreat / Dead) simulated on the session's master client. Animation speed always mirrors real agent velocity (normalized for the blend tree), so animation can never desync from movement.
- **Melee tuned for VR players**: attack-range hysteresis (enter at R, drop only beyond 1.4R) stops constantly-strafing players from flickering the state machine and cancelling swings; the bite lands mid-animation (0.45s into the swing) and can be dodged by stepping back in time.
- **Robust target lifecycle**: dead or unresolvable players are never valid targets; targetless zombies wander instead of freezing; on game over the horde force-exits any in-progress attack (dedicated Retreat state + animator CrossFade) and shambles away.
- **Nearest-player targeting with stickiness**: each zombie chases the closest living player (XZ distance), re-evaluated every 2 s, and only switches when someone else is more than 1.5 m closer -- the pack surrounds whoever is near and splits naturally when players separate, without flip-flopping between two players at similar range. (It used to pick at random, which in real two-player sessions sent half the horde after a player standing still on the other side of the map.)
- **Pathfinding that survives a big, broken NavMesh**: paths are computed synchronously (`CalculatePath` + `SetPath`) instead of re-issuing `SetDestination` every network tick, which kept cancelling Unity's asynchronous search and starved two agents into taking turns standing still. Destinations are projected to the feet (the tracked head is 1.7 m off the mesh and produced partial paths), and when a player stands on unreachable rubble the zombie takes the first complete path to a ring around them. The arena NavMesh itself was rebaked (agent radius 0.75 -> 0.45 m, min region 2 -> 8 m²): 115 disconnected islands became 56, and the walkable main area grew from 38,678 to 55,287 m².
- **Pooled zombies**: Fusion 2 ships no pooling (`NetworkObjectProviderDefault` instantiates and destroys), so `PooledObjectProvider` overrides its two hooks, recycles opted-in prefabs, and prewarms twice the simultaneous-zombie cap behind the arena's load fade. Opt-in matters: Fusion only restores `[Networked]` state on a reused object, so every zombie component resets its own per-life state (colliders, tint, death flags, animator, agent) on `Spawned`.
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

### Sessions, Rooms & Resilience
- **Room codes instead of random matchmaking**: the lobby opens with a world-space menu (laser pointer on the right hand, trigger to click). *Create Lobby* opens a room with a random two-digit code shown in large type; *Join Lobby* lists the open rooms from Photon's session lobby, so nobody has to type. The session directory is namespaced by build version (`VRZ-<Application.version>`), so two different builds never see each other's rooms. If two players create the same code in the same instant (Fusion silently *joins* an existing room instead of failing), the creator detects a second player in a room it just made, leaves, and retries with a new code automatically. The room closes (`IsOpen = false`) as soon as the arena loads, and the lobby props hide while the menu is open.
- **Play again without restarting the app**: `NetworkManager` is a `DontDestroyOnLoad` singleton whose connect call lived in `Start`, which runs once per object -- after a game over the lobby showed "Connecting..." forever. The manager now tears itself down on `OnShutdown` (and a fresh scene copy replaces any stale singleton), so every return to the lobby reconnects cleanly. `Runner.Shutdown()` is also awaited properly: a coroutine does not wait on a `Task`, it just skips one frame.
- **Strictly cooperative disconnect rules**: the master client owns the wave spawner and every zombie, and their wave state is not replicated, so if the host leaves the survivor gets a clear "PARTNER LEFT" screen and returns to the lobby menu instead of a frozen arena with invulnerable zombies (verified in two-build tests). A dropped connection shows "CONNECTION LOST" with the reason. The connection timeout is 25 s (Fusion's default is 10), so short Wi-Fi hiccups on a headset never end a match.
- **Deterministic spawn slots**: each player's spawn point is their rank among the room's active players (sorted by `PlayerId`, identical on every client), not `PlayerId % slots` -- ids are handed out by the cloud and are not consecutive after a leave/rejoin, which put two players on the same spot.
- **Death spectator**: a dead player loses locomotion and hands and their view follows the partner from behind and above with smoothing, keeping their own head rotation (putting the eyes inside someone else's head would nauseate in seconds).

### Game Feel & Scoring
- **Kill celebration**: every client sees the blood puff, native per-zombie death groans and desynced 3D shamble loops; the floating score popup ("+10" / "+25 HEADSHOT!") appears only for the player who actually got the kill.
- **Score system**: networked per-player score / kills / headshot counters with **authoritative kill credit**: the zombie's state authority records who landed the last hit from the damage RPC's own sender (`RpcInfo.Source`, so it cannot be spoofed), replicates it with the death, and only the matching client credits itself. The earlier design had every shooter predict the killing blow from replicated health, which double-credited or lost kills whenever both players fired at the same zombie. **Multi-kill combos** (DOUBLE / TRIPLE / MEGA / MULTIKILL) on a rolling window, coalesced per frame so a grenade wipe reads as one banner; the game-over screen shows YOU / PARTNER / TEAM totals.
- **Round drama**: "ROUND N" banner, two-layer music that crossfades with living-zombie pressure, a brief slow-motion beat (with tape-slow music pitch) when the last zombie of a wave falls, and an ambient lightning storm (skybox exposure + directional flash on a runtime material copy).
- **Night-fight lighting**: every shot fires a real 3-frame muzzle light that illuminates the scene, and each avatar carries a head lamp (replicated for free through the head IK target). Both are shadowless to spare the URP shadow atlas.
- **Procedural heartbeat**: below 35% health a lub-dub fades in and accelerates toward death. The clip is synthesized at runtime (two decaying sine sweeps, 62→38Hz) -- no audio asset involved.
- **End screens built for a headset**: game over, partner-left and connection-lost share one sequence, rendered 1.2 m in front of the eyes (inside the comfortable focus range) with the title at ~5° and the stat lines at ~2.5° of vertical angle; the fade still covers the whole field of view.

### Physics Configuration (the big one)
- The template shipped the Fusion Physics Addon configured for client/server prediction (`Fusion` authority + `FixedUpdateNetwork` timing) -- a mode the docs mark as unsupported in Shared Mode, and which ran ALL physics at network tick rate. Migrating to `Auto` authority + Unity `FixedUpdate` timing (the documented Shared Mode pattern) moved hands and held objects to the real 72hz physics step: the single biggest interaction-fluidity win in the project. Leftover `RunnerSimulatePhysics3D` components (on the `NetworkManager` objects, where Fusion never registered them) were removed so the scenes reflect the actual configuration.
- **Held-rifle angular cap**: every `Rigidbody` starts with `maxAngularVelocity = 7` rad/s (~400°/s), which is not exposed in the inspector; a fast wrist turn exceeds it and the joint-held rifle lagged, then caught up in a jump. The rifle raises it to 15 rad/s -- enough for any wrist flick without letting the grab joint ring when held by the long front grip.

### Atmosphere
- **AmbiencePlayer**: one random horror ambience track per run, chosen by the master and synced to all players through a single networked index (covers late join).

### Quest-Ready Configuration
- **Dedicated render path**: a `Quest_RPAsset` URP asset behind a "Quest" quality level that is the Android default -- MSAA 4x (nearly free on tiled GPUs, essential for VR edges), no HDR, 25m shadows in 2 cascades, 3 per-pixel additional lights (two head lamps + a muzzle flash), and no depth/opaque texture passes. PC keeps its own asset untouched.
- **Android settings Meta expects**: Vulkan only, IL2CPP/ARM64, minSdk 29, ASTC texture subtarget, multithreaded rendering.
- **Memory diet**: the 22 long ambience/music clips stream (Vorbis 0.45) instead of sitting decompressed in RAM (~65MB of PCM each); 224 SFX capped at Vorbis 0.6; the 82 textures authored at 4096px carry an Android-only override to 2048 (about 4x less VRAM per texture on the headset).
- **Static work moved offline**: occlusion culling baked for both scenes (1.4MB of Umbra data for the arena), the lobby's four point lights baked into lightmaps instead of casting realtime soft shadows (which alone saturated the 2048 shadow atlas with 24 shadow maps), and 142 redundant colliders stripped from LOD1/LOD2 renderers across 118 LODGroups.
- **Grass on a budget**: the arena's terrain grass was 65% of the GPU frame (13.7M triangles, 1,800 draws outside the SRP batcher). Detail meshes now render GPU-instanced (`VertexLit` + instancing on all nine prototypes and the material), cast no realtime shadows, the dominant filler prototype's painted map was rescaled to 41%, and `TerrainQualityProfile` applies a per-platform detail distance/density (PC 20m x0.4, Quest 10m x0.25). Result on PC: GPU 18ms -> 9ms, 14.9M -> 1.8M tris, 2,362 -> 1,064 draws, with grass still visible across the arena.
- **Allocation-free firefights**: pooled impact/muzzle particles and popups, pooled 3D audio voices, and an event-driven wave counter instead of per-tick scene scans (see Design & Architecture Decisions).

---

## Architecture Notes (Fusion 2 Shared Mode)

| Concern | Pattern used |
|---|---|
| Persistent state (health, wave, ammo, shot tick) | `[Networked]` properties + `ChangeDetector` |
| Transient events with payload (impacts, damage) | RPCs, `InvokeLocal = false` when the sender already played locally |
| Object ownership | Rifle/magazines: StateAuthority requested on approach/grab (`AllowStateAuthorityOverride`). Grenades: personal to their spawner. Master client simulates zombies/waves |
| Pre-spawn safety | Every `[Networked]` read is guarded with `Object != null && Object.IsValid` |
| Avatar sync | NetworkTransforms on the avatar root, body, IK targets and visual anchors (local coordinates, same hierarchy on every client) + VRIK solved locally on each client; interpolation enabled per authority (proxies interpolate, the owner writes live) |
| Held objects | `NetworkTransform` replicates **local** coordinates, so held magazines/grenades are kept at their original (scene-root) parent while in a hand -- see Engineering Lessons |
| Kill credit | `[Networked] PlayerRef LastDamager`, written by the victim's authority from `RpcInfo.Source`; credited on the killer's client when the death replicates |
| Spawning | `PooledObjectProvider` (an `INetworkObjectProvider`) recycles zombies; other prefabs keep Fusion's default instantiate/destroy |
| Sessions | Photon session lobby per build version, room code = session name, room closed once the arena loads |
| Player spawn handshake | `NetworkManager` fires `onPlayerSpawn` only once the scene's map has subscribed -- Fusion can deliver `OnPlayerJoined` in the very frame a scene finishes loading |

## Netcode Audit (September 2026)

Every networked system (15 of them: session, lobby, maps, player state, avatar, rifle, magazines, impacts, weapon return, grenades, waves, zombies, score, game over, presentation readers) was inventoried and analysed one at a time against the same template: what it does on the wire and who holds authority, which decisions were deliberate vs. untouched Fusion defaults, the alternatives Fusion 2 offers, where it breaks (latency, packet loss, authority changes in flight, the authority disconnecting), what is actually measured vs. assumed, and what would break first at 5x the players. Claims about Fusion defaults were checked against the SDK source or the compiled IL rather than remembered.

Fixes were then prioritised with one rule -- *does it break the two-player experience on Quest?* -- and every one was verified in two-build sessions before moving on. The blockers it found: the game could not be replayed without restarting the app, random matchmaking put strangers in the same room, test values left in the scenes (`maxHealth = 10000`, one-player gates) meant the damage/death/game-over flow had never actually run, and a host leaving froze the arena with invulnerable zombies. Development builds with `VRZ_NET_DIAGNOSTICS` log the numbers that guided the decisions (spawn cost, per-zombie path state, held-weapon authority, snap-turn hand error).

## Design & Architecture Decisions

- **Dirty flag where the scans were.** The wave spawner's tick loop used to call `FindObjectsByType` three to four times per tick (players for the game-over check, zombies twice for the alive count) -- 100-240 full scene scans per second on the host. Players now come from the registry the manager already maintains on join/leave, and the alive count is cached behind a flag that only flips on spawn and on `NetworkZombie.OnAnyDied`. The other candidates were audited and left alone: `RoundJuice` and the heartbeat already throttle, and the floor raycast's input changes every frame, so a flag there would just be an extra branch.
- **Damage is a contract, not a type check.** Every damage source (rifle, grenade blast, zombie melee) talks only to `IDamageable { IsAlive, Health, Position, IsCriticalHit(point), ApplyDamage(in DamageInfo) }`. The *target* decides what counts as a critical hit (a zombie knows where its head is), and the *target's authority* decides who killed it (`LastDamager`), so sources never guess a killing blow from replicated health that may be a snapshot old. A barrel or a destructible prop joins the game by implementing the interface -- no weapon code changes (Open/Closed).
- **The avatar driver is an orchestrator, not a god class.** `NetworkRig` owns the network lifecycle, the tracking-source selection and the single pose pipeline; the sub-problems are small focused collaborators with one reason to change each: `FloorSampler` (masked, buffered, smoothed ground), `AvatarScaleCalibrator` (height scale + body root, pure logic returning a `Result` struct), `PoseSmoother` (framerate-independent filter), `ElbowPole` (bend-plane geometry) and an editor-only `partial` for the live tuner. The pure classes take `dt` as a parameter and touch no Unity objects, so they are unit-testable without a scene or a network session.
- **Composition over inheritance for game feel.** Weapon feedback (`WeaponFeel`, `BulletTracer`, `EmptyMagFeedback`, `MuzzleFlashLight`) and player feedback (`DamageVignette`, `HeartbeatFeedback`) are independent components subscribing to events (`AutoGun.OnShoot`, `NetworkPlayer.OnDamagedFrom`); the systems they decorate never learn they exist.
- **Adapter at the input boundary.** `HardwareRig` implements Fusion's `INetworkRunnerCallbacks.OnInput` and packs AutoHand's tracked transforms into the `CharacterInputData` struct -- the only place where the two frameworks meet.
- **Template method for scene flow.** `MapDefault` defines *how* the avatar is spawned; `LobbyMap` and `GameMap` decide *when* (player-joined vs. scene-loaded), each hooking a different `NetworkManager` event.
- **Defensive construction on woven types.** Fusion's IL weaver was observed producing a `NetworkBehaviour` instance whose field initializers never ran; helper objects are therefore created lazily on first use (`??=`) instead of at field declaration.
- **Pooled transients.** Gunfire is hitscan (no projectiles), but every shot used to `Instantiate`/`Destroy` impact particles on all clients, remote muzzle effects and TextMeshPro score popups -- ~20 allocations per second in a two-player firefight, which reads as micro-stutter on a headset. `PrefabPool` (generic, self-returning after a lifetime, replays particle systems on reuse) and a dedicated popup pool remove that churn; both reset their static state on subsystem registration so disabled domain reload in the editor can't leak instances between plays.
- **Feedback goes through an event queue, gameplay does not.** Fusion itself is state-replication first (RPCs are its only queue-like part), so networked gameplay stays synchronous and authoritative. Presentation is different: a grenade wiping six zombies in one tick used to fire six overlapping popups and six identical death sounds. `FeedbackQueue` buffers kill and sound events and flushes once per frame in `LateUpdate` with coalescing (same-frame kills become one "MULTIKILL x6" banner and a single combo step) and per-clip budgeting (max two plays of the same clip per frame, pitch-varied, through a pool of twelve 3D voices instead of `PlayClipAtPoint`'s throwaway GameObjects).
- **Gameplay depends on contracts, not on the network layer.** Wave logic, end screens, weapons and feedback read the session through `INetworkSession` / `IPlayerState` and send presentation through `IFeedbackSink`; the concrete `NetworkManager` and `FeedbackQueue` REGISTER themselves at startup behind two injection points (`NetworkSession.Current`, `Feedback.Sink`) that ship with no default. `NullNetworkSession` and `SilentFeedbackSink` let wave or scoring logic run in a test or an offline scene with no Fusion and no audio at all. A full Service Locator was deliberately *not* introduced: with two services and one implementation each, it would only hide the dependencies that `grep` currently makes obvious.
- **The dependency direction is compiler-enforced.** All code lives under `VRZ.*` namespaces by folder (`VRZ.Core`, `VRZ.Network`, `VRZ.Player`, `VRZ.Weapons`, `VRZ.Enemies`, `VRZ.FX`, `VRZ.World`), and `VRZ.Core` -- the contracts, the damage model, the pool -- is a separate assembly (`VRZ.Core.asmdef`) that references only Fusion. Core physically *cannot* see gameplay code: any accidental Core -> game dependency is a compile error, not a code-review catch. The rest of the game intentionally stays in `Assembly-CSharp` because Final IK ships without an asmdef (it compiles into the firstpass assembly, which custom assemblies cannot reference).
- **Pure logic is a separate assembly so it can be tested.** A test assembly cannot reference `Assembly-CSharp`, so the avatar helpers live in their own `VRZ.Avatar.asmdef` with an *empty* reference list (engine only). Getting there forced two small inversions: `FloorSampler` no longer knows what AutoHand or the rig are -- the rig injects an `ignoreCollider` predicate -- and `ElbowPole` exposes a pure `TryCompute` over positions and basis vectors, with `Aim` reduced to a `Transform` adapter. The wave rules (`CountReady`, `AllPlayersDead`, `ZombiesForWave`) were lifted out of the `NetworkBehaviour` into `VRZ.Core.WaveRules` for the same reason. `Assets/Tests/EditMode` holds the NUnit EditMode suite (75 tests, see [Tests](#tests)); `VRZ > Run EditMode Tests` runs it in under two seconds.
- **Self-registration and events instead of scene queries.** Objects that others need to find register themselves in `Spawned`/`Despawned` (`ZombieSpawner.Current`, `NetworkManager.Rigs` for avatars), which removed every `FindObjectsByType` from hot paths (zombie re-targeting, grenade blasts, a per-frame scan during the whole lobby). Game over is an `OnChangedRender` event (`ZombieSpawner.GameOverRaised`), and health readers (damage vignette, heartbeat) subscribe to `IPlayerState.OnHealthChanged` instead of polling a replicated value that Fusion already knows the exact frame it changes.
- **Test switches are compile-time, production values live in the scene.** Solo testing used to mean editing the scene (`requiredPlayers = 1`, `maxHealth = 10000`) and remembering to revert -- nobody did, so the whole damage -> death -> game-over flow shipped untested. Now the scene always holds production values and testing opts in through scripting define symbols (`VRZ_SOLO_TEST`, `VRZ_INVULNERABLE`, see `VRZ.Core.DevFlags`) that compile out of normal builds and log a warning at startup when active.

## Engineering Lessons (the bugs worth remembering)

- **AutoHand layers are collision rules, not labels.** Scenery placed on `Hand`/`Grabbable`/`HandPlayer` inherits AutoHand's runtime ignore-matrix and becomes walk-through despite a perfect collider. 46 props were on those layers; a layer audit plus a NavMesh rebake from `Default`-only colliders fixed both players and zombies.
- **Read the tracking rig in the right phase.** AutoHand moves camera and hand targets in `LateUpdate`; anything sampling them in `Update` sees a frame-old pose, and with physics stepping 0/1/2 times per frame that lag fluctuates into visible judder. Solvers must run after that write (VRIK ships at execution order 9998 -- we drive it explicitly).
- **Trust runtime geometry only.** Hand-model axes measured in edit mode were 49° off from the assembled runtime hand; every offset derived from them inherited the error. Live measurement (and finally, eyes in the headset) settled it.
- **`RaycastNonAlloc` buffers overflow silently.** A downward floor ray crossing a held rifle's collider cloud filled its 8-hit buffer with hand/weapon hits, dropped the ground, and fell back to a poisoned reference -- the body popped in sync with every step. Masking layers, widening the buffer and remembering the last good floor closed it.
- **A held weapon "vibrating" is a framerate bug, not a weapon bug.** AutoHand disables `Rigidbody` interpolation on hands, held objects and the player body and instead keeps `fixedDeltaTime` locked to the frame time (one physics step per frame). Below its 50 Hz floor -- the arena ran at 30-36 fps in the editor with a headset -- frames get 1 or 2 steps alternately and the joint-chained rifle hitches against the reprojected camera while walking; the lobby at 72 fps never showed it. A per-frame step histogram (`PhysicsStepMonitor`, editor-only) made the correlation undeniable, and the fix was the GPU budget above, not anything on the rifle. Measure with the Scene View closed: it renders the arena a second time (3-6ms) and blinds the GPU counter.
- **Terrain detail knobs that look alive but aren't.** `DetailPrototype.density` is ignored in `InstanceCountMode` (only `CoverageMode` reads it), and `useInstancing` set through the API on a `Grass`-mode prototype produces draw calls that draw nothing -- the inspector silently switches to `VertexLit` when you tick the box, the API does not. Both cost an afternoon of measuring the wrong thing.
- **`NetworkTransform` replicates local coordinates -- always.** Verified in the Fusion 2.0.10 IL: `CopyToBuffer` reads `localPosition`/`localRotation` and `CopyToEngine` writes them; `SyncParent` only decides whether the parent itself travels. AutoHand's `parentOnGrab` parents a grabbed object to `hand.transform.parent` (the tracking container), so while a partner held a magazine or grenade they sent coordinates relative to their play space, and everyone else rendered the object near the arena's world origin, following the partner's arm. (The avatar was never affected: its NetworkTransforms live in the same hierarchy on every client.)
- **AutoHand's `PlacePoint` has an opinion about that fix.** Simply turning `parentOnGrab` off broke magazine insertion: `PlacePoint.CanPlace` refuses objects without `parentOnGrab` when the place point belongs to another grabbable (the rifle's magazine well). The final fix keeps the flag -- AutoHand decides with it -- and `KeepWorldParentWhileHeld` undoes the actual parenting in `OnGrabEvent`, which AutoHand raises at the end of the same call that parents the object, so no network tick ever sees the parented state.
- **Snap turn already moves most of what you'd "fix".** The hands are children of the tracking container, and so is anything grabbed with `parentOnGrab`; `RotateAround` on the container turns them all, and AutoHand re-seats the right hand on its grab point. A helper that rotated hands and held bodies again left hand + weapon 30° past the controllers, and the hand follower dragged them back over the next frames: a whip after every turn. The helper now only moves held objects that live outside the container, by the container's measured delta.
- **Two motors on one object shake.** AutoHand's `ignoreWeight` adds a `WeightlessFollower` that drives the held object toward the controller, while the grab joint pulls it toward the physical hand. On a long rifle that was a violent oscillation on the host; turning `ignoreWeight` back off fixed it.
- **Never judge hand feel in a slow editor.** An editor-only helper relaxed AutoHand's physics-step floor to 1/30 s so walking would not hitch at 36 fps; at 28-33 ms per step the grab joint is under-damped and held objects oscillate. Logic and networking are fine to test with the editor as one client (make the *build* the master), but the feel of held objects has to be judged in builds at the headset's frame rate.
- **A tuned pipeline is part of its tuning.** The avatar's tick layer (`HardwareRig.OnInput` -> `CharacterInputData` -> `GetInput` -> pose in `FixedUpdateNetwork`, alongside the per-frame pose) looks like a Host Mode leftover in Shared Mode, since that input never leaves the client. Removing it as "cleanup" made the arms shake, because the smoothing had been tuned with it in place; it was restored byte-for-byte from git. Refactors of hand-tuned systems need a recorded baseline and a headset A/B, not just a green compile.

## Tests

**75 NUnit EditMode tests, all passing** (last run 2026-09-23: 75/75 in under two seconds including editor setup). They cover the pure logic that was deliberately lifted out of `NetworkBehaviour`s into engine-only code (see Design & Architecture Decisions) -- so they need no scene, no headset and no Fusion session -- plus the floor sampler against real colliders.

| Suite | Tests | What it pins down |
|---|---|---|
| `AvatarScaleCalibratorTests` | 12 | First valid reading snaps the scale, later readings ease in and converge; min/max clamps; crouching freezes the scale while the body still ducks; headset-on-desk keeps the last scale and shows a neutral pose; the floor is relative, and the body root never sinks below it; standing, the root sits one model head-height below the head |
| `ElbowPoleTests` | 8 | Bend-plane geometry: pole perpendicular to the arm for any arm direction, down/out/back bias, left/right mirroring, scale; degenerate inputs (arm too short, bias parallel to the arm); `Aim` blending and null safety |
| `PoseSmootherTests` | 7 | Exponential filter: the first sample primes with no lag, each step moves by the exponential factor, framerate independence, rotation slerp, a zero time constant passes through, reset and re-enable re-prime instead of resuming stale state |
| `WaveRulesTests` | 15 | Wave size = base + linear ramp (five parameterised cases) and an empty wave at 0 or below; the ready count ignores invalid and null players and a null session; "all players dead" requires every *valid* player at zero; a fake player dies through `IDamageable` |
| `GrenadeRulesTests` | 7 | Zombie damage is full at the centre, 25% at the edge and beyond (the blast is a sphere *overlap*), exactly linear in between and never increasing with distance; a zero radius does not divide by zero; players are hit inside the radius and on its edge, not beyond |
| `ZombieDamageRulesTests` | 6 | Non-lethal hits, exact and overkill lethal hits (health clamps at zero), a hit on a corpse is not applied, negative damage never heals, and the full "last hit gets the kill, a shot on the corpse cannot steal it" scenario with two shooters |
| `SpawnRulesTests` | 5 | Two players get distinct slots, including the non-consecutive ids of a leave/rejoin that broke the old `id % slots` rule; the order of the active-player list does not matter; wrap-around; unknown player / no slots / no list fall back to slot 0 |
| `RoomCodeRulesTests` | 7 | Codes are the prefix + a two-digit number from the 10..99 range, taken codes are skipped, the last free code is found even when the random picks keep missing, all 90 taken returns null, and display codes strip the prefix |
| `FloorSamplerTests` | 8 | With real colliders: no ground ever, the first sample snaps to the ground top, the highest surface wins, the injected "own collider" filter, AutoHand's `Hand` layer and triggers are not ground, a lost sample keeps the last value, and rising ground eases in over the settle time instead of snapping |

**Test doubles.** `FakePlayerState` implements the full `IPlayerState` contract -- including the `OnHealthChanged` and `OnDied` events added during the netcode hardening pass, so the contract and the double cannot drift -- and the wave rules are also exercised against the real `NullNetworkSession`.

**The tests pin what ships.** Every rule extracted for testing (`GrenadeRules`, `ZombieDamageRules`, `SpawnRules`, `RoomCodeRules`) is the code the game itself calls -- `NetworkGrenade`, `NetworkZombie.RPC_TakeDamage`, `MapDefault`, `NetworkManager` and `SessionMenu` -- not a copy kept next to it. Writing the tests also closed two edge cases: a negative damage value (the damage RPC accepts input from any client) can no longer heal a zombie, and room-code selection falls back to an ordered scan so a free code is always found when one exists, instead of random picks possibly missing the last free one. `FloorSampler`, part of the tuned avatar pipeline, was tested from the outside without changing a line of it.

**Running them.**
- In the editor: *Window > General > Test Runner > EditMode > Run All*, or the menu **VRZ > Run EditMode Tests** (`Assets/Editor/RunEditModeTests.cs`: a one-shot `TestRunnerApi` run that logs a `[Tests] DONE passed=... failed=...` summary and every failure with its stack trace -- handy for tooling that cannot drive the Test Runner window).
- Headless (project closed in the editor): `Unity.exe -batchmode -projectPath . -runTests -testPlatform EditMode -assemblyNames VRZ.Tests.EditMode -testResults TestResults.xml`.

**What is deliberately not unit-tested.** Networking (authority transfer, RPCs, replication, reconnection) and the feel of hands and held objects depend on a live Fusion session, AutoHand physics and the headset's frame rate. Those are verified in two-build and headset sessions against a checklist, reading the diagnostics described under Setup.

## Project Structure (my code)

```
Assets/Scripts/
  Network/   NetworkManager (runner lifecycle, session lobby + rooms, player/avatar registries, spawn handshake,
             disconnect handling), PooledObjectProvider (Fusion INetworkObjectProvider with opt-in pooling),
             LobbyManager (ready-up + scene load), MapDefault/GameMap/LobbyMap (avatar spawn per scene)
  Core/      VRZ.Core.asmdef (independent assembly -- contracts cannot depend on gameplay),
             IDamageable + DamageInfo, INetworkSession + IPlayerState (+ injection points,
             Null/Silent implementations), IFeedbackSink, PrefabPool, Rules/ (pure, unit-tested rules:
             WaveRules, GrenadeRules, ZombieDamageRules, SpawnRules, RoomCodeRules),
             DevFlags (compile-time test switches)
  Player/    NetworkRig (avatar orchestration) + NetworkRig.LiveTuner (editor partial),
             Avatar/ VRZ.Avatar.asmdef (engine-only assembly) { FloorSampler, AvatarScaleCalibrator,
                     PoseSmoother, ElbowPole, IKTarget },
             HardwareRig (AutoHand -> Fusion input adapter), NetworkPlayer (health/score/stats),
             CharacterInputData (per-tick input struct), LocalAvatarHider (hide own head/neck/hands),
             PlayerBelt (per-round grenade budget, self-healing if AutoHand drops a grenade), DamageVignette
             (radial hurt feedback), PlayerHeightGuard, SnapTurnHeldFix (snap turn with objects outside the
             tracking container), DeathSpectator (follow-cam for a dead player)
  Weapons/   NetworkAutoGun (tick-synced shots, headshots, replicated chamber), NetworkAutoAmmo (mag sync),
             NetworkGrenade (personal grenades, fuse/explosion), NetworkGunHitEffect, AmmoSpawner, FloatingWeapon,
             WeaponHomeReturn, HeldIgnoresTerrain, KeepWorldParentWhileHeld (held objects replicate world coords)
  Enemies/   NetworkZombie (NavMesh FSM + melee tuning, nearest-player targeting, synchronous pathing,
             authoritative kill credit, pool-safe reset), ZombieSpawner (waves, dirty-flag counts,
             self-registration), ZombieHitFlash
  FX/        FeedbackQueue (coalescing/budgeted presentation, self-registers as the sink),
             WeaponFeel, ZombieJuice/ZombieFeel, PopupText (pooled), ScoreEvents,
             RoundJuice (banners/music/slow-mo), StormAmbience, BulletTracer, EmptyMagFeedback,
             MuzzleFlashLight, HeartbeatFeedback (procedural audio), ExplosionLightFade, MobileParticleBudget
  World/     TutorialManager (7-step tutorial), SessionMenu + HandUIPointerInput (create/join rooms with a hand
             laser), ScreenFader, SpawnPoint, PracticeGrenade, LobbyPrewarm, GameOverController (event-driven;
             game over / partner left / connection lost), AmbiencePlayer, FallCatcher, IgnoreCollisionsWithRoots,
             CollisionGroupIgnore (runtime group ignores for spawned prefabs), TerrainQualityProfile (per-platform
             grass budget), PlatformRenderTweaks, PhysicsStepMonitor (editor-only)
Assets/Tests/EditMode/ VRZ.Tests.EditMode.asmdef (NUnit, editor-only) -- Avatar/ (calibrator, smoother, elbow,
                 floor sampler), Rules/ (wave, grenade, zombie damage, spawn slot, room code), Fakes/FakePlayerState;
                 run via menu VRZ > Run EditMode Tests
Assets/Editor/   RunEditModeTests (TestRunnerApi one-shot for tooling/CI)
Assets/Prefabs/  Rifle, magazines, grenade, zombies, network character/player, network runner, spawn points,
                 Auto Hand Local Character (the single local rig used by BOTH scenes), UI/SessionRow
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
4. **Fusion project config is not versioned** (`Assets/Photon*` is git-ignored with the SDK). In `NetworkProjectConfig` set: tick rate **32**, `Network > Connection Timeout` **25** s, and keep physics in Shared Mode configuration (`Auto` authority, Unity `FixedUpdate` timing, no `RunnerSimulatePhysics3D` on the runner).
5. Open `Assets/Scenes/LobbyScene` and press Play (or build two clients). One player chooses **Create Lobby** and reads out the two-digit room code; the other chooses **Join Lobby** and picks that room from the list. Both builds must share the same `Player Settings > Version`, because the room list is namespaced by it.
6. For Meta Quest: switch the build target to Android (the "Quest" quality level, ASTC subtarget and Vulkan-only settings are already configured) and Build & Run.

**Testing alone.** Add `VRZ_SOLO_TEST` (lobby and waves start with one player) and/or `VRZ_INVULNERABLE` to *Player Settings > Scripting Define Symbols*; a warning is logged at startup while they are active. Remove them before a real build.

**Netcode diagnostics.** Add `VRZ_NET_DIAGNOSTICS` to log spawn cost (`[Waves] Spawn took`), per-zombie path state (`[Zombie N]`), held-rifle authority (`[NetGun] held`) and the snap-turn hand/controller trace (`[SnapTurnFix]`), and to enable the editor-only physics-step monitor (`[PhysStep]`: steps per frame, fps, and a profiler dump of any frame over 100 ms). They are compiled out otherwise, even in development builds, so the console stays readable and the editor does not pay for profiler recording; anomaly warnings (a dropped weapon authority, a snap turn that did not rotate) are always on in development builds.

**Testing with the editor as one client.** Fine for logic and networking, but let the *build* create the room (the master runs the zombie AI), and judge the feel of held objects only in builds: the editor at ~36 fps changes AutoHand's physics step.

## Known Issues

- **Slight shoulder jitter on the avatar.** Pre-existing in the VRIK tuning, not a networking artifact; left alone deliberately until it can be retuned in the headset against a recorded baseline.
- **The room list is public within a build version.** Anyone running the same build can see and join an open room; a private code-entry join would need a keypad.
- **If the host leaves, the match ends** (by design for a strictly co-op game). Continuing would require `IsMasterClientObject` on the scene spawners, replicating the alive-zombie bookkeeping and `IStateAuthorityChanged` handling on every zombie.

## Credits

- Networking/gameplay integration: the repository author.
- Grenade explosion VFX and fog texture (`Assets/VFX/`): hand-made by the repository author.
- The session/spawn scaffolding (`HardwareRig`, the Map classes, `SpawnPoint`) started from the IronHeadVR Udemy course by [IronHead Games](https://www.udemy.com/user/ironhead-games/) and [Tevfik Ufuk Demirbas](https://www.udemy.com/user/tevfik-ufuk-demirbas/); it has been rewritten for AutoHand and Fusion 2 and the course content itself is not included.
- AutoHand by Earnest Robot. Final IK by RootMotion. Photon Fusion by Exit Games.
- VR body/IK approach inspired by [Valem's tutorial](https://www.youtube.com/watch?v=v47lmqfrQ9s) (Complete VR Body Setup); the grab-aware dual-source targeting follows AutoHand's own VRIK integration example.
- Development tooling: `Packages/manifest.json` references `com.coplaydev.unity-mcp`, an optional editor-time MCP bridge used during development. It is safe to remove from the manifest. `Assets/Plugins/Roslyn/` holds Microsoft's Roslyn compiler assemblies (MIT license) used by that tooling; they are not part of the game.
