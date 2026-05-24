using System;
using UnityEngine;

// Tunable area-of-interest knobs. Plain data, serialized on Game and persisted via JsonPref
// ("replication.json"); edited in the TunerPanels "Replication" accordion. Lives in the pure Aoi asmdef so
// AreaOfInterestSystem and its unit tests use it without referencing the game assembly.
[Serializable]
public class ReplicationSettings
{
    [Min(1f)]      public float showRadius = 96f;    // start observing a player within this many cells
    [Min(1f)]      public float hideRadius = 128f;   // stop observing past this many cells (hysteresis: hide > show)
    [Range(1, 30)] public int   snapshotHz = 30;     // server -> client snapshot send rate
}
