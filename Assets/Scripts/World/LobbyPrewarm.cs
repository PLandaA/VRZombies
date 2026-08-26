using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// Lobby-time asset prewarm: instantiates the game scene's heavy prefabs hidden below the map
/// for a few frames so their shaders compile, textures upload and meshes load DURING the lobby
/// instead of hitching on first sight in the arena. Networking components are stripped from the
/// temporary copies (they are visual-only). Also raises background loading priority so Fusion's
/// scene load streams faster.
public class LobbyPrewarm : MonoBehaviour
{
    [Tooltip("Heavy prefabs from the game scene (zombies, FX, weapons)")]
    [SerializeField] private GameObject[] prewarmPrefabs;

    [Tooltip("Hidden position where the copies render for a few frames")]
    [SerializeField] private Vector3 hiddenPosition = new Vector3(0f, -60f, 0f);

    private void Start()
    {
        Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.High;
        StartCoroutine(Prewarm());
    }

    private IEnumerator Prewarm()
    {
        yield return null;   // let the lobby settle one frame

        var holder = new GameObject("__PrewarmHolder");
        holder.SetActive(false);   // children instantiate WITHOUT running Awake

        var spawned = new List<GameObject>();
        foreach (var prefab in prewarmPrefabs)
        {
            if (prefab == null) continue;
            var go = Instantiate(prefab, hiddenPosition, Quaternion.identity, holder.transform);
            StripLogic(go);
            spawned.Add(go);
        }

        holder.SetActive(true);    // render hidden below the map: shaders compile now
        yield return null;
        yield return null;
        yield return null;

        Destroy(holder);
        Debug.Log("[LobbyPrewarm] Prewarmed " + spawned.Count + " prefab(s).");
    }

    /// Makes the copies inert WITHOUT destroying components (RequireComponent dependencies
    /// forbid arbitrary removal order). Disabled Behaviours never OnEnable/Update; Awake runs
    /// but is harmless with the full component set present. The Animator stays on so skinned
    /// meshes actually animate one frame -- that warms the GPU skinning path too.
    private void StripLogic(GameObject go)
    {
        foreach (var b in go.GetComponentsInChildren<Behaviour>(true))
        {
            if (b is Animator) continue;
            b.enabled = false;
        }
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
        {
            rb.isKinematic = true;
        }
        foreach (var c in go.GetComponentsInChildren<Collider>(true))
        {
            c.enabled = false;
        }
    }
}
