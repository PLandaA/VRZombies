using UnityEngine;
using Fusion;

namespace VRZ.Player
{

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
                head.localScale = Vector3.one * 0.001f;
            else
                Debug.LogWarning("[LocalAvatarHider] Head bone not found.");

            // The neck stump is what you see when looking down. Collapse it too -- but only after
            // VRIK has initiated (it samples bone lengths on its first Update; collapsing the neck
            // before that would poison the spine solve).
            StartCoroutine(HideNeckAfterIKInit());

            // Hide the avatar's own hands too: locally your AutoHand hands ARE your hands;
            // the avatar's copies would double up. Remote players still see them.
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t.name.EndsWith(":RightHand") || t.name.EndsWith(":LeftHand"))
                    t.localScale = Vector3.one * 0.001f;
        }

        private System.Collections.IEnumerator HideNeckAfterIKInit()
        {
            yield return null;
            yield return null;
            yield return null;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name.EndsWith(":Neck"))
                {
                    t.localScale = Vector3.one * 0.001f;
                    break;
                }
            }
        }
    }
}
