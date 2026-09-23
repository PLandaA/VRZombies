using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace VRZ.Network
{
    /// Object pool for Fusion spawns (netcode debt D1).
    ///
    /// Fusion 2 ships no pooling: NetworkObjectProviderDefault does Instantiate on Spawn and
    /// Destroy on Despawn. This subclass keeps the two hooks it exposes, InstantiatePrefab and
    /// DestroyPrefabInstance, and recycles instances of OPTED-IN prefabs only. Opt-in matters:
    /// Fusion only guarantees [Networked] state on a reused object; every script on the prefab
    /// must clear its own per-life state in Spawned (the zombie does, via OnLocalReset; the
    /// grenade and magazine do not yet). Registered on the runner through
    /// StartGameArgs.ObjectProvider in NetworkManager.ConnectGame, so both clients pool: the
    /// master recycles what it spawns and the partner recycles the proxies Fusion creates.
    public class PooledObjectProvider : NetworkObjectProviderDefault
    {
        private readonly Dictionary<NetworkPrefabId, Stack<NetworkObject>> _pools = new();
        private readonly HashSet<NetworkPrefabId> _pooledPrefabs = new();
        private Transform _poolRoot;

        /// Fusion keeps a prefab's network GUID on its NetworkObjectPrefabData component.
        private static NetworkPrefabId IdOf(NetworkRunner runner, NetworkObject prefab)
        {
            var data = prefab.GetComponent<NetworkObjectPrefabData>();
            return data != null ? runner.Prefabs.GetId(data.Guid) : default;
        }

        /// Opt a prefab into pooling and optionally pre-instantiate 'prewarm' inactive copies so
        /// the first spawns of a match pay no Instantiate either. Call once per prefab, from a
        /// spawner's Spawned (the runner must exist).
        public void EnablePooling(NetworkRunner runner, NetworkObject prefab, int prewarm)
        {
            if (runner == null || prefab == null) return;
            var id = IdOf(runner, prefab);
            if (!id.IsValid) { Debug.LogWarning("[Pool] Prefab not registered with Fusion: " + prefab.name); return; }

            _pooledPrefabs.Add(id);
            var pool = GetPool(id);
            for (int i = pool.Count; i < prewarm; i++)
            {
                var inst = Instantiate(prefab, PoolRoot);
                inst.gameObject.SetActive(false);
                pool.Push(inst);
            }
        }

        protected override NetworkObject InstantiatePrefab(NetworkRunner runner, NetworkObject prefab)
        {
            var id = IdOf(runner, prefab);
            if (!_pooledPrefabs.Contains(id))
                return base.InstantiatePrefab(runner, prefab);

            var pool = GetPool(id);
            // Drop entries Unity destroyed behind our back (a scene unload while they were parked).
            while (pool.Count > 0 && pool.Peek() == null) pool.Pop();

            NetworkObject inst;
            if (pool.Count > 0)
            {
                inst = pool.Pop();
                inst.transform.SetParent(null);   // AcquirePrefabInstance moves it to the runner scene next
                inst.gameObject.SetActive(true);
            }
            else
            {
                inst = Instantiate(prefab);        // pool empty: grow, it will come back on despawn
            }
            return inst;
        }

        protected override void DestroyPrefabInstance(NetworkRunner runner, NetworkPrefabId prefabId, NetworkObject instance)
        {
            if (!_pooledPrefabs.Contains(prefabId) || instance == null)
            {
                base.DestroyPrefabInstance(runner, prefabId, instance);
                return;
            }
            // Park it: inactive, under our root (out of the game scene so scene changes cannot
            // destroy it), ready for the next InstantiatePrefab. Scripts already ran Despawned.
            instance.gameObject.SetActive(false);
            instance.transform.SetParent(PoolRoot, false);
            GetPool(prefabId).Push(instance);
        }

        private Stack<NetworkObject> GetPool(NetworkPrefabId id)
        {
            if (!_pools.TryGetValue(id, out var pool)) { pool = new Stack<NetworkObject>(); _pools[id] = pool; }
            return pool;
        }

        private Transform PoolRoot
        {
            get
            {
                if (_poolRoot == null)
                {
                    // Lives under the NetworkManager (DontDestroyOnLoad), so parked instances
                    // survive Lobby <-> Arena loads and die with the manager on shutdown.
                    var go = new GameObject("ObjectPool");
                    go.transform.SetParent(transform, false);
                    _poolRoot = go.transform;
                }
                return _poolRoot;
            }
        }

        private void OnDestroy()
        {
            _pools.Clear();
            _pooledPrefabs.Clear();
        }
    }
}
