using System.Collections.Generic;
using UnityEngine;

namespace VRZ.Core
{

    /// Object pool for short-lived prefab instances (impact particles, muzzle effects...).
    ///
    /// Why: at ~600 rpm across two players the game was doing ~20 Instantiate/Destroy per second
    /// of particle systems -- allocation + GC churn that reads as micro-stutter on a headset,
    /// precisely during firefights. Instances are created once, parked inactive under a
    /// persistent root, and handed out again on demand.
    ///
    /// Usage:  PrefabPool.Spawn(prefab, pos, rot, lifetime)  -- auto-returns after `lifetime`
    ///         PrefabPool.Release(instance)                   -- manual return
    public static class PrefabPool
    {
        private static readonly Dictionary<GameObject, Stack<GameObject>> Free = new();
        private static readonly Dictionary<GameObject, GameObject> PrefabOf = new();   // instance -> prefab
        private static Transform _root;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            // Domain reload may be disabled in the editor: never trust static state across plays.
            Free.Clear();
            PrefabOf.Clear();
            _root = null;
        }

        private static Transform Root
        {
            get
            {
                if (_root == null)
                {
                    var go = new GameObject("[PrefabPool]");
                    Object.DontDestroyOnLoad(go);
                    _root = go.transform;
                }
                return _root;
            }
        }

        /// Takes an instance of `prefab` from the pool (or creates one), places it, activates it
        /// and replays any ParticleSystems on it.
        public static GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, float lifetime = -1f)
        {
            if (prefab == null) return null;

            if (!Free.TryGetValue(prefab, out var stack))
            {
                stack = new Stack<GameObject>();
                Free[prefab] = stack;
            }

            GameObject instance = null;
            while (stack.Count > 0 && instance == null)
                instance = stack.Pop();               // skips instances destroyed by a scene unload

            if (instance == null)
            {
                instance = Object.Instantiate(prefab, Root);
                PrefabOf[instance] = prefab;
            }

            instance.transform.SetPositionAndRotation(position, rotation);
            instance.SetActive(true);
            foreach (var ps in instance.GetComponentsInChildren<ParticleSystem>(true))
                ps.Play(true);

            if (lifetime > 0f)
            {
                var timer = instance.GetComponent<PooledLifetime>() ?? instance.AddComponent<PooledLifetime>();
                timer.Arm(lifetime);
            }
            return instance;
        }

        /// Returns an instance to its pool. Safe to call on non-pooled objects (they get destroyed).
        public static void Release(GameObject instance)
        {
            if (instance == null) return;
            if (!PrefabOf.TryGetValue(instance, out var prefab))
            {
                Object.Destroy(instance);
                return;
            }

            foreach (var ps in instance.GetComponentsInChildren<ParticleSystem>(true))
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            instance.SetActive(false);
            instance.transform.SetParent(Root, false);
            Free[prefab].Push(instance);
        }

        /// Returns the instance to the pool after a delay. Added automatically by Spawn(lifetime).
        private class PooledLifetime : MonoBehaviour
        {
            private float _remaining;

            public void Arm(float seconds) { _remaining = seconds; enabled = true; }

            private void Update()
            {
                _remaining -= Time.deltaTime;
                if (_remaining > 0f) return;
                enabled = false;
                Release(gameObject);
            }
        }
    }
}
