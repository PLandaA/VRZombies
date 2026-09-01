using UnityEngine;
using VRZ.Core;
using VRZ.Network;
using VRZ.Player;
using VRZ.Weapons;
using VRZ.FX;
using VRZ.World;

namespace VRZ.Enemies
{

    /// Marker for headshot detection: lives on a trigger sphere parented to the zombie's head bone.
    /// NetworkAutoGun multiplies damage when the bullet ray hits a collider carrying this.
    public class HeadshotHitbox : MonoBehaviour { }
}
