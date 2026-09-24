using System.Collections;
using UnityEngine;
using Fusion;
using Autohand;
using VRZ.Enemies;

namespace VRZ.Player
{

    /// Local (non-networked) VR belt that follows the player's head at waist height with yaw-only rotation.
    /// Holds two grenade PlacePoints and restocks them at match start and on every wave intermission.
    /// Placed grenades are NOT parented (their NetworkTransform must keep syncing in world space for the
    /// remote client) — instead this script snaps them to their PlacePoint pose every frame.
    public class PlayerBelt : MonoBehaviour
    {
        [Header("Follow")]
        [Tooltip("Head camera transform of the local Auto Hand rig")]
        [SerializeField] private Transform head;

        [Tooltip("Vertical offset from the head down to the waist (negative)")]
        [SerializeField] private float heightOffset = -0.55f;

        [Tooltip("Pulls the belt backward along the look direction so it hugs the waist (the head sits ahead of the spine)")]
        [SerializeField] private float backOffset = 0.06f;

        [Tooltip("Yaw follow speed (degrees-ish smoothing factor)")]
        [SerializeField] private float yawFollowSpeed = 6f;

        [Header("Grenades")]
        [Tooltip("Networked grenade prefab spawned onto the belt")]
        [SerializeField] private NetworkObject grenadePrefab;

        [Tooltip("Belt PlacePoints that hold the grenades")]
        [SerializeField] private PlacePoint[] placePoints;

        [Tooltip("Hard cap: grenades granted per round. The belt refills up to this on each intermission and NEVER mid-round.")]
        [SerializeField] private int maxGrenadesPerRound = 2;

        private NetworkRunner _runner;
        private ZombieSpawner _waveSystem;
        private int _spawnedThisRound;

        /// Grenades this belt spawned and still owns (for the safety net in LateUpdate).
        private readonly System.Collections.Generic.List<Grabbable> _mine = new();

        private void Start()
        {
            StartCoroutine(InitialStock());
        }

        private void OnDestroy()
        {
            if (_waveSystem != null)
                _waveSystem.OnIntermissionStarted.RemoveListener(OnIntermission);
        }

        private IEnumerator InitialStock()
        {
            // The belt only makes sense in the arena. With the local character unified into one
            // prefab (R11), this component also lives in the lobby, where there is no wave system:
            // stay idle there instead of spawning two grenades next to the tutorial.
            if (ZombieSpawner.Current == null)
            {
                Debug.Log("[PlayerBelt] No ZombieSpawner in this scene: belt idle.");
                yield break;
            }

            // Wait for the Fusion session to be up
            while (_runner == null || !_runner.IsRunning)
            {
                _runner = FindFirstObjectByType<NetworkRunner>();
                yield return new WaitForSeconds(0.5f);
            }

            // Small settle delay so PlacePoints and physics are ready
            yield return new WaitForSeconds(1f);

            _waveSystem = ZombieSpawner.Current;   // self-registered, see ZombieSpawner.Current
            if (_waveSystem != null)
                _waveSystem.OnIntermissionStarted.AddListener(OnIntermission);
            else
                Debug.LogWarning("[PlayerBelt] ZombieSpawner not found - no intermission restock.");

            Restock();
        }

        private void OnIntermission(int nextWave)
        {
            _spawnedThisRound = 0;   // new round -> fresh grenade budget
            Restock();
        }

        /// Spawns a fresh grenade on every empty PlacePoint, capped by the per-round budget.
        /// Runs on the LOCAL client only, so each player owns their own belt grenades.
        private void Restock()
        {
            if (grenadePrefab == null || placePoints == null || _runner == null || !_runner.IsRunning)
            {
                Debug.LogWarning("[PlayerBelt] Missing grenadePrefab/placePoints or runner not running.");
                return;
            }

            int spawned = 0;
            foreach (var pp in placePoints)
            {
                if (_spawnedThisRound >= maxGrenadesPerRound) break;   // round budget exhausted
                if (pp == null || pp.GetPlacedObject() != null) continue;

                var no = _runner.Spawn(grenadePrefab, pp.transform.position, pp.transform.rotation);
                if (no == null) continue;

                var grab = no.GetComponent<Grabbable>();
                if (grab != null)
                {
                    pp.Place(grab);
                    _mine.Add(grab);
                }
                spawned++;
                _spawnedThisRound++;
            }

            if (spawned > 0)
                Debug.Log("[PlayerBelt] Restocked " + spawned + " grenade(s) (" + _spawnedThisRound + "/" + maxGrenadesPerRound + " this round).");
        }

        private void LateUpdate()
        {
            if (head == null) return;

            // Waist position under the head, pulled back toward the spine
            Vector3 yawFwdForPos = head.forward;
            yawFwdForPos.y = 0f;
            yawFwdForPos = yawFwdForPos.sqrMagnitude > 0.001f ? yawFwdForPos.normalized : Vector3.forward;
            transform.position = head.position + Vector3.up * heightOffset - yawFwdForPos * backOffset;

            // Yaw-only smooth follow of the head direction
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f)
            {
                Quaternion targetYaw = Quaternion.LookRotation(fwd.normalized, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetYaw, Time.deltaTime * yawFollowSpeed);
            }

            // Keep placed grenades glued to their PlacePoints (unparented for network sync)
            if (placePoints == null) return;
            foreach (var pp in placePoints)
            {
                if (pp == null) continue;
                var placed = pp.GetPlacedObject();
                if (placed == null) continue;

                // Move the PHYSICS pose too (2026-09-21, "ghost grenade in the middle of the map"):
                // PlacePoint.CheckPlaceObjectLoop releases a placed object whose Rigidbody no longer
                // overlaps the point (10 cm). Writing only the transform leaves the collider one
                // physics step behind; a snap turn or a quick step put it out of range, AutoHand
                // un-placed the grenade and it fell where the player had been. Rigidbody.position on
                // a kinematic body updates the physics pose immediately, so the overlap test holds.
                var body = placed.body;
                if (body != null)
                {
                    body.position = pp.transform.position;
                    body.rotation = pp.transform.rotation;
                }
                placed.rootTransform.SetPositionAndRotation(pp.transform.position, pp.transform.rotation);
            }

            RecoverDroppedGrenades();
        }

        /// Safety net: a belt grenade that is not placed, not in a hand and not armed was not
        /// thrown (throwing arms it 0.25 s after release); AutoHand let go of it. Put it back on an
        /// empty PlacePoint instead of leaving a dud on the floor for the partner to stare at.
        ///
        /// Grace period (bug 2026-09-21, "thrown grenades came back to the belt"): a grenade that
        /// was JUST released also matches all three conditions until ArmIfThrown runs. Only act
        /// once it has been loose for longer than the arming delay, with margin.
        private const float LooseGraceSeconds = 1.5f;
        private readonly System.Collections.Generic.Dictionary<Grabbable, float> _looseSince = new();

        private void RecoverDroppedGrenades()
        {
            for (int i = _mine.Count - 1; i >= 0; i--)
            {
                var grab = _mine[i];
                if (grab == null) { _mine.RemoveAt(i); _looseSince.Remove(grab); continue; }   // exploded / despawned
                if (grab.placePoint != null || grab.IsHeld()) { _looseSince.Remove(grab); continue; }   // where it should be

                var nade = grab.GetComponent<VRZ.Weapons.NetworkGrenade>();
                if (nade == null || !nade.Object || !nade.Object.IsValid) { _mine.RemoveAt(i); _looseSince.Remove(grab); continue; }
                if (nade.Armed || nade.Exploded) { _mine.RemoveAt(i); _looseSince.Remove(grab); continue; }   // thrown: not ours to touch

                if (!_looseSince.TryGetValue(grab, out float since)) { _looseSince[grab] = Time.time; continue; }
                if (Time.time - since < LooseGraceSeconds) continue;   // may still be arming

                foreach (var pp in placePoints)
                {
                    if (pp == null || pp.GetPlacedObject() != null) continue;
                    Debug.LogWarning("[PlayerBelt] Grenade came loose (not thrown); re-placing it on " + pp.name + ".");
                    if (grab.body != null) { grab.body.linearVelocity = Vector3.zero; grab.body.angularVelocity = Vector3.zero; }
                    pp.Place(grab);
                    _looseSince.Remove(grab);
                    break;
                }
            }
        }
    }
}
