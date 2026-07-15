using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Fusion;

/// Input struct sent from the hardware rig to the network each tick (head, hands, character pose).
public struct CharacterInputData : INetworkInput
{
    public Vector3 characterPosition;
    public Quaternion characterRotation;

    public Vector3 headPosition;
    public Quaternion headRotation;

    public Vector3 handRightPosition;
    public Quaternion handRightRotation;

    public Vector3 handLeftPosition;
    public Quaternion handLeftRotation;

}
