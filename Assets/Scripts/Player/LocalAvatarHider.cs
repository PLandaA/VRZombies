using UnityEngine;
using Fusion;

/// Hides the avatar's own head bone for the local player (standard VR first-person trick).
public class LocalAvatarHider : NetworkBehaviour
{
    public override void Spawned()
    {
        if (!Object.HasStateAuthority) return;

        Transform head = null;
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name.EndsWith(":Head"))
            {
                head = t;
                break;
            }
        }
        if (head == null)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name.EndsWith("Head") && !t.name.Contains("Top") && t.name != "Head")
                {
                    head = t;
                    break;
                }
            }
        }

        if (head != null)
        {
            head.localScale = Vector3.one * 0.001f;
            Debug.Log("[LocalAvatarHider] Own head hidden (" + head.name + ")");
        }
        else
        {
            Debug.LogWarning("[LocalAvatarHider] Head bone not found.");
        }
    }
}
