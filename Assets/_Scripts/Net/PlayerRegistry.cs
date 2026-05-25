using System.Collections.Generic;
using UnityEngine;

// Server-only authoritative state for one connected player. Nothing here replicates; the snapshot stream is
// the only thing that leaves the server. Mirrors the old PlayerMovement server fields (motion + instance
// flag) but as plain data keyed by clientId.
public sealed class ServerPlayer
{
    public readonly PlayerMotion motion = new();
    public Vector2 worldPos;
    public Vector2Int regionKey;          // (0,0) overworld; else underworld room interior origin
    public bool inInstance;
    public Vector2Int overworldReturnCell;
    public Vector2 submittedInput;        // latest owner intent (replaces the moveInput NetworkVariable)
    public bool snap;                     // set on teleport; cleared after the next snapshot carries it

    public uint lastProcessedTick;        // highest contiguous client tick the server has simulated (free move)
    public Vector2 lastInput;             // last applied free-move input (reference/debug)
    public RingBuffer<InputCommand> serverInputs;// received tick-stamped commands (free/in-instance only; lazily created)

    public readonly StatusState status = new(); // active status effects; reduces to the gate the sim consumes
    public int hp;                              // server-authoritative HP; set on instance enter (Task 8)

    // Authoritative attack state (in-instance only). Stepped by AttackSimSystem via the shared InstanceStep.
    public AttackState attackState;
    public PhaseScales attackScales = PhaseScales.One;
    public byte weaponId;                 // equipped weapon (catalog id), set from each InputCommand
    public int enchantDefId = -1;         // (debug) extra on-strike status effect from the 'enchant' command; -1 = none
    public AttackPhase prevAttackPhase;   // for transition detection (events + hit seam)
    public System.Collections.Generic.Queue<AttackEvent> pendingEvents;  // drained into per-viewer event RPCs each snapshot
}

// All connected players, keyed by clientId. Server-only.
public sealed class PlayerRegistry
{
    public readonly Dictionary<ulong, ServerPlayer> Players = new();

    public ServerPlayer Add(ulong clientId, Vector2Int cell, Vector2 worldPos)
    {
        var sp = new ServerPlayer { worldPos = worldPos, regionKey = Vector2Int.zero };
        sp.motion.cell = cell;
        Players[clientId] = sp;
        return sp;
    }

    public void Remove(ulong clientId) => Players.Remove(clientId);
    public bool TryGet(ulong clientId, out ServerPlayer sp) => Players.TryGetValue(clientId, out sp);
}
