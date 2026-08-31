using UnityEngine;
using UnityEngine.AI;
using Fusion;
using Autohand;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(NetworkObject))]
/// Authority-driven zombie AI: NavMesh chase/attack FSM with networked state, health and animation sync.
public class NetworkZombie : NetworkBehaviour
{
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
            AudioSource.PlayClipAtPoint(deathSounds[i], transform.position, 1f);
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
        bool moving = _agent != null && _agent.isActiveAndEnabled && _agent.velocity.sqrMagnitude > 0.25f;
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

    public override void Spawned()
    {
        _changes = GetChangeDetector(ChangeDetector.Source.SimulationState);
        if (_agent != null)
        {
            _agent.speed = runSpeed;
            _agent.acceleration = 16f;
            _agent.enabled = Object.HasStateAuthority;
        }
        if (Object.HasStateAuthority)
        {
            Health = maxHealth;
            State = ZombieState.Idle;
            RetargetTimer = TickTimer.None;
        }
        base.Spawned();
    }

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
                UpdateAnimSpeed();
                return;
            }
        if (!HasValidTarget())
        {
            _targetTransform = null;

            // Retarget quickly while targetless (a living player may remain)
            if (RetargetTimer.ExpiredOrNotRunning(Runner))
            {
                PickRandomTarget();
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
                UpdateAnimSpeed();
                return;
            }

            // Reacquired someone: restore full chase speed (wander/game-over slow it down)
            if (_agent != null) _agent.speed = runSpeed;
        }

        if (RetargetTimer.ExpiredOrNotRunning(Runner))
        {
            PickRandomTarget();
            RetargetTimer = TickTimer.CreateFromSeconds(Runner, retargetInterval * 3f);
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

            Vector3 lookDir = targetPos - transform.position;
            lookDir.y = 0;
            if (lookDir.sqrMagnitude > 0.001f)
            {
                Quaternion rot = Quaternion.LookRotation(lookDir);
                transform.rotation = Quaternion.Slerp(transform.rotation, rot, Runner.DeltaTime * 8f);
            }

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
                _agent.SetDestination(targetPos);
            }
        }

        UpdateAnimSpeed();
    }

    public override void Render()
    {
        if (animator == null) return;
        animator.SetFloat(speedParam, AnimSpeed);
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

    private bool HasValidTarget()
    {
        if (_targetTransform == null) return false;
        if (!_targetTransform.gameObject.activeInHierarchy) return false;
        var np = ResolveNetworkPlayer(_targetTransform);
        // Unresolvable player = INVALID target (a dead player's ghost transform must never pass)
        if (np == null || np.Object == null || !np.Object.IsValid) return false;
        if (np.IsDead) return false;
        return true;
    }

    private void PickRandomTarget()
    {
        System.Collections.Generic.List<Transform> candidates = new();

        var autoHandPlayers = UnityEngine.Object.FindObjectsByType<AutoHandPlayer>(FindObjectsSortMode.None);
        foreach (var ahp in autoHandPlayers)
        {
            if (ahp == null || !ahp.gameObject.activeInHierarchy) continue;
            if (ahp.headCamera != null)
                candidates.Add(ahp.headCamera.transform);
            else
                candidates.Add(ahp.transform);
        }

        var networkRigs = UnityEngine.Object.FindObjectsByType<NetworkRig>(FindObjectsSortMode.None);
        foreach (var rig in networkRigs)
        {
            if (rig == null || !rig.gameObject.activeInHierarchy) continue;
            if (rig.Object == null || !rig.Object.IsValid) continue;
            bool esLocal = rig.Object.StateAuthority == Runner.LocalPlayer ||
                           rig.Object.InputAuthority == Runner.LocalPlayer;
            if (esLocal) continue;
            candidates.Add(rig.transform);
        }

        System.Collections.Generic.List<Transform> alive = new();
        foreach (var t in candidates)
        {
            var np = ResolveNetworkPlayer(t);
            // Only chase targets that resolve to a LIVING networked player
            if (np != null && np.Object != null && np.Object.IsValid && !np.IsDead)
                alive.Add(t);
        }

        if (alive.Count == 0)
        {
            _targetTransform = null;
            return;
        }

        int idx = Random.Range(0, alive.Count);
        _targetTransform = alive[idx];
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
        if (np != null && np.Object != null && np.Object.IsValid && !np.IsDead)
            np.RPC_TakeDamage(attackDamage, transform.position);   // position drives the directional hit indicator
    }

    private NetworkPlayer ResolveNetworkPlayer(Transform t)
    {
        if (t == null) return null;
        if (NetworkManager.instance == null) return null;

        var no = t.GetComponentInParent<NetworkObject>();
        if (no != null)
        {
            // Remote rigs may have InputAuthority = None in Shared Mode; fall back to StateAuthority
            if (no.InputAuthority != PlayerRef.None)
                return NetworkManager.instance.GetPlayer(no.InputAuthority);
            if (no.StateAuthority != PlayerRef.None)
                return NetworkManager.instance.GetPlayer(no.StateAuthority);
            return null;   // networked but unowned: never mistake it for the local player
        }

        // No NetworkObject in parents -> local AutoHandPlayer rig of THIS client
        return NetworkManager.instance.GetPlayer(Runner.LocalPlayer);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_TakeDamage(int amount, NetworkBool headshot = default)
    {
        if (State == ZombieState.Dead) return;
        Health = Mathf.Max(0, Health - amount);
        if (Health > 0)
            StaggerTimer = TickTimer.CreateFromSeconds(Runner, 0.18f);
        if (Health <= 0)
        {
            DiedByHeadshot = headshot;
            State = ZombieState.Dead;
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
