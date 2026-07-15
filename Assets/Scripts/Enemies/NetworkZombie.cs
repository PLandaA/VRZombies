using UnityEngine;
using UnityEngine.AI;
using Fusion;
using Autohand;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(NetworkObject))]
/// Authority-driven zombie AI: NavMesh chase/attack FSM with networked state, health and animation sync.
public class NetworkZombie : NetworkBehaviour
{
    public enum ZombieState : byte { Idle = 0, Chasing = 1, Attacking = 2, Dead = 3 }

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
    [Networked] public float AnimSpeed { get; private set; }
    [Networked] private int AttackTick { get; set; }
    [Networked] private TickTimer AttackCooldownTimer { get; set; }
    [Networked] private TickTimer RetargetTimer { get; set; }
    [Networked] private TickTimer DespawnTimer { get; set; }

    private NavMeshAgent _agent;
    private Transform _targetTransform;
    private ChangeDetector _changes;

    public bool IsDead => State == ZombieState.Dead;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
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

        if (!HasValidTarget())
        {
            if (RetargetTimer.ExpiredOrNotRunning(Runner))
            {
                PickRandomTarget();
                RetargetTimer = TickTimer.CreateFromSeconds(Runner, retargetInterval);
            }
            if (_agent != null && _agent.enabled) _agent.ResetPath();
            State = ZombieState.Idle;
            UpdateAnimSpeed();
            return;
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

        if (distSqXZ <= attackRange * attackRange)
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

            if (AttackCooldownTimer.ExpiredOrNotRunning(Runner))
            {
                AttackCooldownTimer = TickTimer.CreateFromSeconds(Runner, attackCooldown);
                AttackTick = Runner.Tick;
                DealDamageToTarget();
            }
        }
        else
        {
            State = ZombieState.Chasing;
            if (_agent != null && _agent.enabled)
                _agent.SetDestination(targetPos);
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
            else if (change == nameof(State) && State == ZombieState.Dead)
                animator.SetTrigger(dieParam);
        }
    }

    private bool HasValidTarget()
    {
        if (_targetTransform == null) return false;
        if (!_targetTransform.gameObject.activeInHierarchy) return false;
        var np = ResolveNetworkPlayer(_targetTransform);
        if (np != null && np.Object != null && np.Object.IsValid && np.IsDead) return false;
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
            bool dead = np != null && np.Object != null && np.Object.IsValid && np.IsDead;
            if (!dead) alive.Add(t);
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
            np.RPC_TakeDamage(attackDamage);
    }

    private NetworkPlayer ResolveNetworkPlayer(Transform t)
    {
        if (t == null) return null;
        if (NetworkManager.instance == null) return null;

        var no = t.GetComponentInParent<NetworkObject>();
        if (no != null && no.InputAuthority != PlayerRef.None)
            return NetworkManager.instance.GetPlayer(no.InputAuthority);

        return NetworkManager.instance.GetPlayer(Runner.LocalPlayer);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RPC_TakeDamage(int amount)
    {
        if (State == ZombieState.Dead) return;
        Health = Mathf.Max(0, Health - amount);
        if (Health <= 0)
        {
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
