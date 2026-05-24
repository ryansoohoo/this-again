using System;
using UnityEngine;

// Tunable knobs for free (underworld) movement + client prediction. Plain data, persisted via JsonPref
// ("movement.json"). moveSpeed is the single source of truth for the free path on BOTH client and server,
// so prediction and the authoritative sim integrate identically.
[Serializable]
public class MovementSettings
{
    [Min(0.1f)] public float moveSpeed = 4f;             // units/sec for free movement (matches the grid moveSpeed)
    [Min(0f)]   public float reconcileEpsilon = 0.05f;   // world-unit error below which no correction is applied
    [Min(0.01f)]public float correctionSmoothTime = 0.1f;// seconds to visually absorb a correction
    [Min(8)]    public int   inputBufferCapacity = 128;  // ring size; rounded up to a power of two at use
}
