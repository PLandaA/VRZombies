using UnityEngine;
using Fusion;

/// Hides the avatar's own head and hand bones for the LOCAL player (standard VR first-person
/// trick): you see your AutoHand physical hands instead, while REMOTE players see the avatar's
/// full body including its hands following the replicated IK targets.
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

        // Hide the avatar's own hands too: locally your AutoHand hands ARE your hands;
        // the avatar's copies would double up. Remote players still see them.
        int hiddenHands = 0;
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name.EndsWith(":RightHand") || t.name.EndsWith(":LeftHand"))
            {
                t.localScale = Vector3.one * 0.001f;
                hiddenHands++;
            }
        }
        Debug.Log("[LocalAvatarHider] Own avatar hands hidden: " + hiddenHands);
    }
}

