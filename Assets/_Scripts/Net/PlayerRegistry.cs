using System.Collections.Generic;
using UnityEngine;

// Server-only authoritative state for one connected player. Combat/hittable state is inherited from Combatant;
// this class adds only the player/networking/input fields. Nothing here replicates; the snapshot stream is the
// only thing that leaves the server.
public sealed class ServerPlayer : Combatant
{
    public readonly PlayerMotion motion = new();
    public Vector2Int overworldReturnCell;
    public Vector2 submittedInput;        // latest owner intent (replaces the moveInput NetworkVariable)
    public bool snap;                     // set on teleport; cleared after the next snapshot carries it

    public uint lastProcessedTick;        // highest contiguous client tick the server has simulated (free move)
    public Vector2 lastInput;             // last applied free-move input (reference/debug)
    public RingBuffer<InputCommand> serverInputs;// received tick-stamped commands (free/in-instance only; lazily created)
}

// All combatants, keyed for two access patterns: Players (clients — networking/AOI/input, keyed by clientId) and
// Combatants (all — the hit + collision sweeps). Players register into both. Server-only.
public sealed class PlayerRegistry
{
    public readonly Dictionary<ulong, ServerPlayer> Players = new();
    public readonly List<Combatant> Combatants = new();

    public ServerPlayer Add(ulong clientId, Vector2Int cell, Vector2 worldPos, CharacterDef def)
    {
        var sp = new ServerPlayer { entityId = clientId, worldPos = worldPos, regionKey = Vector2Int.zero };
        sp.faction = def != null ? def.faction : Faction.Player;
        sp.stats = def != null ? def.CreateStats() : FallbackStats();
        sp.motion.cell = cell;
        Players[clientId] = sp;
        Combatants.Add(sp);
        return sp;
    }

    public void Remove(ulong clientId)
    {
        if (Players.TryGetValue(clientId, out var sp)) Combatants.Remove(sp);
        Players.Remove(clientId);
    }

    public bool TryGet(ulong clientId, out ServerPlayer sp) => Players.TryGetValue(clientId, out sp);

    // Default stats when no CharacterDef is wired (keeps the old 100 HP so behavior degrades gracefully + visibly).
    static Stats FallbackStats() { var s = new Stats(); s.SetBase(StatKind.MaxHp, 100); return s; }
}
