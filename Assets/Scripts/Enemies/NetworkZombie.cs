using UnityEngine;
using VRZ.Core;
using UnityEngine.AI;
using Fusion;
using Autohand;
using VRZ.Network;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.FX;
using VRZ.World;

namespace VRZ.Enemies
{

    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(NetworkObject))]
    /// Authority-driven zombie AI: NavMesh chase/attack FSM with networked state, health and animation sync.
    public class NetworkZombie : NetworkBehaviour, IDamageable
    {
        /// Raised on the state authority the moment a zombie dies. Lets counters (wave spawner,
        /// music pressure) invalidate their caches instead of scanning the scene every tick.
        public static event System.Action<NetworkZombie> OnAnyDied;

        // ── IDamageable ──
        // Geometric headshot: distance from the impact point to the head bone. (A trigger sphere
        // was invisible to AutoGun's ray: QueryTriggerInteraction.Ignore.)
        public bool IsAlive => State != ZombieState.Dead;
        public Vector3 Position => transform.position;
        public bool IsCriticalHit(Vector3 hitPoint) =>
            HeadBone != null && Vector3.Distance(hitPoint, HeadBone.position) < 0.3f;
        public void ApplyDamage(in DamageInfo damage) => RPC_TakeDamage(damage.Amount, damage.Critical);

        public enum ZombieState : byte { Idle = 0, Chasing = 1, Attacking = 2, Dead = 3, Retreating = 4 }

        [Header("Stats")]
        [SerializeField] private int maxHealth = 100;
        [SerializeField] private float runSpeed = 3.5f;
        [SerializeField] private float attackRange = 1.6f;
        [SerializeField] private float attackCooldown = 1.5f;
        [SerializeField] private int attackDamage = 10;

        [Header("Death")]
        [Tooltip("Seconds the corpse remains before despawning")]
        [SerializeField] private float corpseDuration = 6f;

        [Header("Target Discovery")]
        [SerializeField] private float retargetInterval = 2f;

        [Header("Animator")]
        [SerializeField] private Animator animator;
        [SerializeField] private string speedParam = "Speed";
        [SerializeField] private string attackParam = "Attack";
        [SerializeField] private string dieParam = "Die";

        [Networked] public int Health { get; private set; }
        [Networked] public ZombieState State { get; private set; }
        [Networked] public NetworkBool DiedByHeadshot { get; private set; }
        [Networked] public float AnimSpeed { get; private set; }

        /// Netcode fix A5. Who landed the last damage, written by the State Authority inside
        /// RPC_TakeDamage from the RPC's own sender (RpcInfo.Source), so it cannot be spoofed by
        /// the attacker and needs no extra parameter. At death it names the killer; every client
        /// reads it in Render and only the matching local player credits the kill.
        [Networked] public PlayerRef LastDamager { get; private set; }

        /// True on the client whose player killed this zombie (valid once State == Dead).
        public bool KilledByLocalPlayer => Runner != null && LastDamager == Runner.LocalPlayer;

        [Networked] private int AttackTick { get; set; }
        [Networked] private TickTimer AttackCooldownTimer { get; set; }
        [Networked] private TickTimer PendingHitTimer { get; set; }
        [Networked] private TickTimer StaggerTimer { get; set; }
        [Networked] private TickTimer RetargetTimer { get; set; }
        [Networked] private TickTimer WanderTimer { get; set; }
        [Networked] private TickTimer DespawnTimer { get; set; }

        private NavMeshAgent _agent;
        private Transform _targetTransform;
        private ZombieSpawner _gameOverSpawner;
        private ChangeDetector _changes;

        /// Fired in Render on every client when this zombie dies (arg: killed by headshot).
        public event System.Action<bool> OnDiedRender;

        [Header("Audio")]
        [Tooltip("Random death sound played at the corpse position on every client (index derived from network id, so all clients hear the same clip)")]
        [SerializeField] private AudioClip[] deathSounds;

        [Tooltip("Looping 3D footsteps played while the zombie is moving, so players can hear it approaching")]
        [SerializeField] private AudioClip walkSound;
        private bool _deathSoundPlayed;
        private AudioSource _walkAudio;

        private void Update()
        {
            if (Object == null || !Object.IsValid) return;

            if (State == ZombieState.Dead)
            {
                if (_walkAudio != null && _walkAudio.isPlaying) _walkAudio.Stop();
                if (_deathSoundPlayed) return;
                _deathSoundPlayed = true;
                if (deathSounds != null && deathSounds.Length > 0)
            {
                int i = (int)(Object.Id.Raw % (uint)deathSounds.Length);
                Feedback.Sink.EnqueueSound(deathSounds[i], transform.position, 1f);
            }
                return;
            }

            if (walkSound == null) return;
            if (_walkAudio == null)
            {
                _walkAudio = gameObject.AddComponent<AudioSource>();
                _walkAudio.clip = walkSound;
                _walkAudio.loop = true;
                _walkAudio.playOnAwake = false;
                _walkAudio.spatialBlend = 1f;
                _walkAudio.rolloffMode = AudioRolloffMode.Linear;
                _walkAudio.minDistance = 1f;
                _walkAudio.maxDistance = 9f;      // was 14: only nearby zombies are audible
                _walkAudio.volume = 0.22f;        // was 0.45: the horde no longer drowns the mix
                _walkAudio.pitch = 0.85f + (Object.Id.Raw % 40) * 0.01f;   // 0.85-1.24: wide spread
                _walkAudio.time = (Object.Id.Raw % 100) * 0.01f * walkSound.length;   // desync loops
            }
            // Netcode fix A1. The old gate read the NavMeshAgent's velocity, but the agent is only
            // enabled on the State Authority (Spawned), so proxies never played footsteps: the
            // non-master player could not hear zombies approach. AnimSpeed is the master's
            // |velocity| / runSpeed (0..1), replicated every tick and read by every client for the
            // locomotion blend, so it is the same signal on all peers. 0.14 ~= 0.5 m/s / 3.5 m/s.
            bool moving = AnimSpeed > 0.14f;
            if (moving && !_walkAudio.isPlaying) _walkAudio.Play();
            else if (!moving && _walkAudio.isPlaying) _walkAudio.Stop();
        }

        public bool IsDead => State == ZombieState.Dead;

        /// Head bone, cached for geometric headshot detection (trigger colliders are invisible
        /// to AutoGun's raycast, which uses QueryTriggerInteraction.Ignore).
        public Transform HeadBone { get; private set; }

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
            if (animator == null) animator = GetComponentInChildren<Animator>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.name.Contains("HumanHead")) { HeadBone = t; break; }
        }

        /// Netcode debt #1 (pool readiness). Raised from Spawned on every client, AFTER this
        /// component has cleared its own per-life state, so sibling presentation scripts
        /// (ZombieHitFlash, ZombieFeel, ...) can clear theirs too. Without this, an
        /// INetworkObjectProvider pool would hand out corpses: colliders off, red tint, death
        /// already "played", stale last-health.
        public event System.Action OnLocalReset;

        public override void Spawned()
        {
            _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);
            ResetLocalState();
            if (_agent != null)
            {
                _agent.speed = runSpeed;
                _agent.acceleration = 16f;
                _agent.updateRotation = false;     // facing is ours (see FaceTowards): target first, path when detouring
                _agent.enabled = Object.HasStateAuthority;
                // A pooled instance may be re-spawned somewhere else: move the agent with it
                // instead of letting it path from its previous death spot. Only when they really
                // disagree; a fresh Instantiate already has them together.
                if (_agent.enabled && _agent.isOnNavMesh && Vector3.Distance(_agent.nextPosition, transform.position) > 0.5f)
                    _agent.Warp(transform.position);
            }
            if (Object.HasStateAuthority)
            {
                Health = maxHealth;
                State = ZombieState.Idle;
                RetargetTimer = TickTimer.None;
                // Face somebody from the very first frame instead of holding the spawn point's rotation
                PickNearestTarget();
                if (_targetTransform != null)
                {
                    Vector3 look = _targetTransform.position - transform.position; look.y = 0f;
                    if (look.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(look);
                }
            }
            base.Spawned();
            OnLocalReset?.Invoke();
        }

        /// Everything a previous life left behind on this GameObject. Runs on every client.
        private void ResetLocalState()
        {
            _deathSoundPlayed = false;
            _targetTransform = null;
            _gameOverSpawner = null;
            _lastRequestedDest = new Vector3(float.NaN, 0f, 0f);
            _nextRepathAt = 0;

            // Render disables every collider on death so bullets pass through corpses (12b).
            foreach (var c in GetComponentsInChildren<Collider>(true)) c.enabled = true;

            if (animator != null)
            {
                // Rebind returns the controller to its entry state and clears pending triggers
                // (a queued "Die" would otherwise fire on the first frame of the new life).
                animator.Rebind();
                animator.Update(0f);
                animator.SetFloat(speedParam, 0f);
            }

            if (_walkAudio != null && _walkAudio.isPlaying) _walkAudio.Stop();
            transform.localScale = Vector3.one;   // ZombieFeel's death pop scales the root
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            // Silence immediately: a pooled instance sits disabled for a while, and a looping
            // AudioSource would keep the "last footstep" state alive.
            if (_walkAudio != null && _walkAudio.isPlaying) _walkAudio.Stop();
            base.Despawned(runner, hasState);
        }

        // Measurement for netcode debt #2 (DisableSharedModeInterpolation on the zombie prefab).
        // On the State Authority the NavMeshAgent moves the transform in Update; if Fusion's
        // shared-mode interpolation is active on the authority, NetworkTransform.Render then
        // rewrites the transform ~1 tick behind the agent. The agent keeps its own idea of where
        // it is (nextPosition), so |nextPosition - transform.position| at Render time IS the
        // pull-back. Expected: ~0 with the flag on, up to ~0.11 m at 3.5 m/s with it off.
#if VRZ_NET_DIAGNOSTICS
        private float _gapMax, _gapLogAt;
        private void LogInterpolationGap()
        {
            if (!Object.HasStateAuthority || _agent == null || !_agent.enabled) return;
            if (AnimSpeed >= 0.14f)
            {
                float gap = Vector3.Distance(_agent.nextPosition, transform.position);
                if (gap > _gapMax) _gapMax = gap;
            }
            if (Time.time >= _gapLogAt)
            {
                // Diagnostic (2026-09-19, "zombies stand still" report): who am I chasing and is the path alive.
                string target = _targetTransform != null ? _targetTransform.root.name + "/" + _targetTransform.name : "NONE";
                float dist = _targetTransform != null ? Vector3.Distance(_targetTransform.position, transform.position) : -1f;
                Debug.Log("[Zombie " + Object.Id.Raw + "] state=" + State + " target=" + target + " dist=" + dist.ToString("F1")
                          + " onNavMesh=" + _agent.isOnNavMesh + " hasPath=" + _agent.hasPath + " pathStatus=" + _agent.pathStatus
                          + " remaining=" + _agent.remainingDistance.ToString("F1") + " vel=" + _agent.velocity.magnitude.ToString("F2")
                          + " animSpeed=" + AnimSpeed.ToString("F2") + " gapMax=" + (_gapMax * 100f).ToString("F1") + "cm");
                _gapMax = 0f; _gapLogAt = Time.time + 2f;
            }
        }
#else
        private void LogInterpolationGap() { }
#endif

        public override void FixedUpdateNetwork()
        {
            if (!Object.HasStateAuthority) return;

            if (State == ZombieState.Dead)
            {
                if (DespawnTimer.Expired(Runner))
                    Runner.Despawn(Object);
                return;
            }

                if (_gameOverSpawner == null) _gameOverSpawner = FindObjectsByType<ZombieSpawner>(FindObjectsSortMode.None) is var sps && sps.Length > 0 ? sps[0] : null;
                if (_gameOverSpawner != null && _gameOverSpawner.Object != null && _gameOverSpawner.Object.IsValid && _gameOverSpawner.GameOver)
                {
                    State = ZombieState.Retreating;   // dedicated walk-away state: Render force-exits any attack anim
                    if (_agent != null && _agent.isActiveAndEnabled && _agent.isOnNavMesh)
                    {
                        _agent.speed = 1.1f;
                        Vector3 dir = _targetTransform != null ? (transform.position - _targetTransform.position).normalized : transform.forward;
                        dir.y = 0f;
                        _agent.SetDestination(transform.position + dir * 40f);
                    }
                    _targetTransform = null;
                    FaceAlongPath();
                    UpdateAnimSpeed();
                    return;
                }
            if (!HasValidTarget())
            {
                _targetTransform = null;

                // Retarget quickly while targetless (a living player may remain)
                if (RetargetTimer.ExpiredOrNotRunning(Runner))
                {
                    PickNearestTarget();
                    RetargetTimer = TickTimer.CreateFromSeconds(Runner, 0.5f);
                }

                // Nobody alive to chase: shamble somewhere else instead of freezing mid-animation
                if (_targetTransform == null)
                {
                    State = ZombieState.Retreating;
                    if (_agent != null && _agent.isActiveAndEnabled && _agent.isOnNavMesh &&
                        (WanderTimer.ExpiredOrNotRunning(Runner) || !_agent.hasPath))
                    {
                        WanderTimer = TickTimer.CreateFromSeconds(Runner, 4f);
                        Vector2 rnd = Random.insideUnitCircle.normalized * 12f;
                        _agent.speed = runSpeed * 0.5f;
                        _agent.SetDestination(transform.position + new Vector3(rnd.x, 0f, rnd.y));
                    }
                    FaceAlongPath();
                    UpdateAnimSpeed();
                    return;
                }

                // Reacquired someone: restore full chase speed (wander/game-over slow it down)
                if (_agent != null) _agent.speed = runSpeed;
            }

            if (RetargetTimer.ExpiredOrNotRunning(Runner))
            {
                PickNearestTarget();
                // Re-evaluate every retargetInterval (2 s) instead of x3: nearest-player targeting
                // needs to notice when the players swap distances.
                RetargetTimer = TickTimer.CreateFromSeconds(Runner, retargetInterval);
                if (!HasValidTarget()) { UpdateAnimSpeed(); return; }
            }

            Vector3 targetPos = _targetTransform.position;

            Vector2 zombieXZ = new Vector2(transform.position.x, transform.position.z);
            Vector2 targetXZ = new Vector2(targetPos.x, targetPos.z);
            float distSqXZ = (targetXZ - zombieXZ).sqrMagnitude;

            // Hysteresis: ENTER melee at attackRange, but only DROP back to chasing beyond 1.4x.
            // Without this, a VR player strafing on the boundary flips the state several times a
            // second and every attack swing gets cancelled -- the "stuck zombie" look.
            float enterSq = attackRange * attackRange;
            float exitSq = (attackRange * 1.4f) * (attackRange * 1.4f);
            bool stayInMelee = State == ZombieState.Attacking && distSqXZ <= exitSq;

            if (distSqXZ <= enterSq || stayInMelee)
            {
                if (_agent != null && _agent.enabled)
                {
                    _agent.ResetPath();
                    _agent.velocity = Vector3.zero;
                }
                State = ZombieState.Attacking;
                FaceTowards(targetPos - transform.position, 8f);

                // The bite lands mid-swing, not at windup: damage fires 0.45s after the anim starts
                if (PendingHitTimer.IsRunning && PendingHitTimer.Expired(Runner))
                {
                    PendingHitTimer = TickTimer.None;
                    if (distSqXZ <= exitSq)
                        DealDamageToTarget();
                }

                if (AttackCooldownTimer.ExpiredOrNotRunning(Runner))
                {
                    AttackCooldownTimer = TickTimer.CreateFromSeconds(Runner, attackCooldown);
                    AttackTick = Runner.Tick;
                    PendingHitTimer = TickTimer.CreateFromSeconds(Runner, 0.45f);
                }
            }
            else
            {
                State = ZombieState.Chasing;
                PendingHitTimer = TickTimer.None;   // stepping out dodges the pending bite
                if (_agent != null && _agent.enabled)
                {
                    // Stagger: a fresh bullet briefly cuts the charge to a stumble, so shots
                    // read as physical impacts instead of just a colour flash
                    _agent.speed = StaggerTimer.ExpiredOrNotRunning(Runner) ? runSpeed : runSpeed * 0.3f;
                    RequestPathTo(targetPos);

                    // Look at the target while charging; when the path bends around an obstacle
                    // (path direction far from target direction), look where the feet go instead.
                    Vector3 toTarget = targetPos - transform.position; toTarget.y = 0f;
                    Vector3 pathDir = _agent.desiredVelocity; pathDir.y = 0f;
                    bool detouring = pathDir.sqrMagnitude > 0.05f && Vector3.Angle(pathDir, toTarget) > 45f;
                    FaceTowards(detouring ? pathDir : toTarget, 10f);
                }
            }

            UpdateAnimSpeed();
        }

        public override void Render()
        {
            if (animator == null) return;
            animator.SetFloat(speedParam, AnimSpeed);
            LogInterpolationGap();
            foreach (var change in _changes.DetectChanges(this, out _, out _))
            {
                if (change == nameof(AttackTick))
                    animator.SetTrigger(attackParam);
                else if (change == nameof(State))
                {
                    if (State == ZombieState.Dead)
                    {
                        animator.ResetTrigger(attackParam);   // never queue an attack into the death anim
                        animator.SetTrigger(dieParam);
                        OnDiedRender?.Invoke(DiedByHeadshot);

                        // Netcode fix A5: the kill is credited HERE, from the master's verdict
                        // (LastDamager arrives in the same snapshot as State == Dead), and only on
                        // the killer's client. Weapons no longer predict kills from stale health,
                        // so two shooters can no longer both bank the same zombie, and a shooter
                        // whose replicated health was one snapshot behind no longer loses a kill.
                        if (KilledByLocalPlayer)
                            VRZ.FX.ScoreEvents.RegisterKill(DiedByHeadshot, transform.position);

                        // Corpses must not block bullets: disable every collider on EVERY client
                        // (each shooter raycasts locally). The body has no dynamic rigidbody, so
                        // the posed corpse stays on the ground just fine without them.
                        foreach (var col in GetComponentsInChildren<Collider>(true))
                            col.enabled = false;
                    }
                    else
                    {
                        // Leaving Attacking with a stranded trigger breaks transitions (T-pose): clear it
                        animator.ResetTrigger(attackParam);

                        // Only a genuine retreat (game over / no targets left) force-exits the attack
                        // anim. Combat-range flips let the swing finish naturally -- interrupting them
                        // was what made zombies look stuck mid-attack.
                        if (State == ZombieState.Retreating)
                            animator.CrossFade("Locomotion", 0.2f);
                    }
                }
            }
        }

        /// Retreat / wander: no target to look at, so face the way the agent is walking.
        private void FaceAlongPath()
        {
            if (_agent == null || !_agent.enabled) return;
            FaceTowards(_agent.desiredVelocity, 6f);
        }

        /// Yaw-only turn toward `dir` at a rate of `sharpness` (per second, exponential ease).
        private void FaceTowards(Vector3 dir, float sharpness)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.001f) return;
            Quaternion rot = Quaternion.LookRotation(dir);
            transform.rotation = Quaternion.Slerp(transform.rotation, rot, Runner.DeltaTime * sharpness);
        }

        private bool HasValidTarget()
        {
            if (_targetTransform == null) return false;
            if (!_targetTransform.gameObject.activeInHierarchy) return false;
            var np = ResolveNetworkPlayer(_targetTransform);
            // Unresolvable player = INVALID target (a dead player's ghost transform must never pass)
            if (np == null || !np.IsValid) return false;
            if (!np.IsAlive) return false;
            return true;
        }

        /// Targeting (changed 2026-09-19): the zombie chases the NEAREST living player instead of a
        /// random one. With two players the random pick sent half the horde to whoever was far
        /// away and standing still, which from the other player's side looked like zombies
        /// ignoring them. Nearest-with-stickiness reads better in co-op: the pack surrounds the
        /// closer player and splits naturally when you separate.
        private void PickNearestTarget()
        {
            System.Collections.Generic.List<Transform> candidates = new();

            // Local player of THIS client (the master): its real body, not a network proxy.
            var ahp = AutoHandPlayer.Instance;
            if (ahp != null && ahp.gameObject.activeInHierarchy)
                candidates.Add(ahp.headCamera != null ? ahp.headCamera.transform : ahp.transform);

            // Remote players: the avatars registered by NetworkRig.Spawned (debt D3, no scene scan).
            var nm = NetworkManager.instance;
            if (nm != null)
            {
                foreach (var rig in nm.Rigs)
                {
                    if (rig == null || !rig.gameObject.activeInHierarchy) continue;
                    if (rig.Object == null || !rig.Object.IsValid) continue;
                    bool esLocal = rig.Object.StateAuthority == Runner.LocalPlayer ||
                                   rig.Object.InputAuthority == Runner.LocalPlayer;
                    if (esLocal) continue;
                    candidates.Add(rig.transform);
                }
            }

            // Nearest living player in the XZ plane (height is irrelevant for ground chasers)
            Transform best = null;
            float bestSq = float.MaxValue;
            float currentSq = float.MaxValue;
            Vector3 me = transform.position;
            foreach (var t in candidates)
            {
                var np = ResolveNetworkPlayer(t);
                if (np == null || !np.IsValid || !np.IsAlive) continue;   // only LIVING networked players

                Vector3 d = t.position - me; d.y = 0f;
                float sq = d.sqrMagnitude;
                if (t == _targetTransform) currentSq = sq;
                if (sq < bestSq) { bestSq = sq; best = t; }
            }

            if (best == null)
            {
                _targetTransform = null;
                return;
            }

            // Stickiness: keep the current target unless someone else is clearly closer, so two
            // players at similar range do not make the zombie flip between them every retarget.
            const float switchMargin = 1.5f;   // metres
            if (_targetTransform != null && currentSq != float.MaxValue && best != _targetTransform)
            {
                float currentDist = Mathf.Sqrt(currentSq), bestDist = Mathf.Sqrt(bestSq);
                if (currentDist - bestDist < switchMargin) return;   // not worth switching
            }

            _targetTransform = best;
        }

        // ── Path requests (fix 2026-09-19: "zombies stand still / take turns moving") ──────────
        //
        // The chase used to call SetDestination(head position) EVERY tick (32/s). Unity computes
        // paths asynchronously with a per-frame iteration budget shared by all agents; each new
        // request cancels the pending one, so with a long path two zombies starved each other
        // and alternated between "moving" and "hasPath = false". The head is also 1.7 m above the
        // NavMesh, which gave PathPartial. Now: sample the target onto the NavMesh, and only
        // re-request when the destination moved or a short interval passed, never while pending.
        private Vector3 _lastRequestedDest = new Vector3(float.NaN, 0f, 0f);
        private double _nextRepathAt;
        private const float RepathMoveThreshold = 0.5f;    // metres the target must move to re-path
        private const float RepathInterval = 0.5f;          // seconds; safety refresh even if static
        private UnityEngine.AI.NavMeshPath _path;            // reused: CalculatePath allocates nothing into it

        private void RequestPathTo(Vector3 targetPos)
        {
            if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh) return;

            bool moved = float.IsNaN(_lastRequestedDest.x)
                         || Vector3.Distance(targetPos, _lastRequestedDest) > RepathMoveThreshold;
            bool due = Runner.SimulationTime >= _nextRepathAt;
            if (!moved && !due && _agent.hasPath) return;    // keep following the path we have

            _path ??= new UnityEngine.AI.NavMeshPath();

            // Synchronous CalculatePath instead of SetDestination: the result is known this tick,
            // so nothing waits on Unity's per-frame async budget (which starved two zombies into
            // taking turns) and we can react to a partial path immediately.
            if (!TryPathTo(targetPos, _path))
            {
                // The arena NavMesh has 100+ disconnected islands (rubble, roofs, ledges). If the
                // player stands on one, the point right under them is unreachable. Try a ring of
                // spots around them and take the first COMPLETE path: the zombie then walks to the
                // foot of the rubble instead of giving up at the edge of a partial path.
                Vector3 best = Vector3.zero; bool found = false;
                for (int i = 0; i < 8 && !found; i++)
                {
                    float a = i * Mathf.PI * 2f / 8f;
                    Vector3 cand = targetPos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 1.6f;
                    if (TryPathTo(cand, _path)) { best = cand; found = true; }
                }
                if (!found) TryPathTo(targetPos, _path);       // fall back to the partial path
            }

            if (_path.status != UnityEngine.AI.NavMeshPathStatus.PathInvalid)
                _agent.SetPath(_path);

            _lastRequestedDest = targetPos;
            _nextRepathAt = Runner.SimulationTime + RepathInterval;
        }

        /// Ground-projects 'to' onto the NavMesh and computes a path from the agent. True only
        /// when the path is COMPLETE (the destination really is on our connected mesh).
        private bool TryPathTo(Vector3 to, UnityEngine.AI.NavMeshPath path)
        {
            if (!UnityEngine.AI.NavMesh.SamplePosition(to, out var hit, 2.5f, UnityEngine.AI.NavMesh.AllAreas)) return false;
            if (!_agent.CalculatePath(hit.position, path)) return false;
            return path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete;
        }

        private void UpdateAnimSpeed()
        {
            float v = 0f;
            if (_agent != null && _agent.enabled && runSpeed > 0.01f)
                v = Mathf.Clamp01(_agent.velocity.magnitude / runSpeed);
            AnimSpeed = v;
        }

            private void DealDamageToTarget()
        {
            if (_targetTransform == null) return;
            var np = ResolveNetworkPlayer(_targetTransform);
            if (np != null && np.IsValid && np.IsAlive)
                np.ApplyDamage(new DamageInfo(attackDamage, np.Position, transform.position));   // position drives the directional hit indicator
        }

        private IPlayerState ResolveNetworkPlayer(Transform t)
        {
            if (t == null) return null;
            if (NetworkSession.Current == null) return null;

            var no = t.GetComponentInParent<NetworkObject>();
            if (no != null)
            {
                // Remote rigs may have InputAuthority = None in Shared Mode; fall back to StateAuthority
                if (no.InputAuthority != PlayerRef.None)
                    return NetworkSession.Current.GetPlayer(no.InputAuthority);
                if (no.StateAuthority != PlayerRef.None)
                    return NetworkSession.Current.GetPlayer(no.StateAuthority);
                return null;   // networked but unowned: never mistake it for the local player
            }

            // No NetworkObject in parents -> local AutoHandPlayer rig of THIS client
            return NetworkManager.instance.GetPlayer(Runner.LocalPlayer);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        public void RPC_TakeDamage(int amount, NetworkBool headshot = default, RpcInfo info = default)
        {
            // Pure, unit-tested rule: a hit on a corpse is not applied (so it can never overwrite the
            // killer), health clamps at zero, and negative amounts never heal.
            var outcome = ZombieDamageRules.Apply(Health, State == ZombieState.Dead, amount);
            if (!outcome.Applied) return;
            Health = outcome.Health;
            // info.Source is the PlayerRef that sent this RPC (ourselves when InvokeLocal runs it
            // on the master). Recorded before the death write so both land in the same snapshot.
            LastDamager = info.Source;
            if (!outcome.Killed)
                StaggerTimer = TickTimer.CreateFromSeconds(Runner, 0.18f);
            else
            {
                DiedByHeadshot = headshot;
                State = ZombieState.Dead;
                OnAnyDied?.Invoke(this);
                AnimSpeed = 0f;
                DespawnTimer = TickTimer.CreateFromSeconds(Runner, corpseDuration);
                if (_agent != null && _agent.enabled)
                {
                    _agent.ResetPath();
                    _agent.enabled = false;
                }
            }
        }
    }
}
