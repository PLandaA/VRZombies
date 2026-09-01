using System.Collections;
using UnityEngine;
using Fusion;
using Autohand;
using VRZ.Core;
using VRZ.Network;
using VRZ.Weapons;
using VRZ.Enemies;
using VRZ.FX;
using VRZ.World;

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
            // Wait for the Fusion session to be up
            while (_runner == null || !_runner.IsRunning)
            {
                _runner = FindFirstObjectByType<NetworkRunner>();
                yield return new WaitForSeconds(0.5f);
            }

            // Small settle delay so PlacePoints and physics are ready
            yield return new WaitForSeconds(1f);

            _waveSystem = FindFirstObjectByType<ZombieSpawner>();
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
                    pp.Place(grab);
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
                placed.rootTransform.SetPositionAndRotation(pp.transform.position, pp.transform.rotation);
            }
        }
    }
}
