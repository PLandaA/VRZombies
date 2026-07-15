using UnityEngine;
using Fusion;

public class NetworkObjectFlagDebug : MonoBehaviour
{
    private void Start()
    {
        var no = GetComponent<NetworkObject>();
        if (no == null) return;
        bool allowOverride = (no.Flags & NetworkObjectFlags.AllowStateAuthorityOverride) != 0;
        Debug.Log($"[FlagDebug] {gameObject.name} | Flags={no.Flags} | AllowOverride={allowOverride}", this);
    }
}
