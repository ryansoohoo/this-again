using System.Collections.Generic;
using UnityEngine;

// Server-only TEST scaffolding: keeps one stationary "goblin" Combatant (faction Enemy) in each active underworld
// room so the hittable foundation has a visible, solid target. Spawned when a player first enters a room, despawned
// when the room empties, reset to full HP when killed (respawn-fresh). It does NOT move/attack/aggro — purely a
// punching bag. This is NOT the real AI/spawner; the deterministic spawner + instruction-priority brain replace it.
public sealed class DummySpawner
{
    readonly Dictionary<Vector2Int, Combatant> _byRoom = new();

    // Ensure the room at this regionKey has its goblin dummy (idempotent — safe to call on every player enter).
    public void EnsureRoomDummy(PlayerRegistry reg, Vector2Int region)
    {
        if (_byRoom.ContainsKey(region)) return;
        var gm = Game.Instance; if (gm == null) return;
        var def = gm.GoblinCharacter; if (def == null) return;
        var cell = new Vector2Int(region.x + Underworld.RoomSize / 2, region.y + Underworld.RoomSize / 2 + 4);
        var g = new Combatant
        {
            entityId = GoblinId(region),
            faction = def.faction,
            regionKey = region,
            inInstance = true,
            worldPos = gm.CellCenter(cell.x, cell.y),
            stats = def.CreateStats(),
        };
        g.hp = g.stats.GetInt(StatKind.MaxHp);
        reg.AddCombatant(g);
        _byRoom[region] = g;
    }

    // After a player leaves/disconnects from a region, despawn its dummy if no in-instance player remains there.
    public void RemoveDummyIfRoomEmpty(PlayerRegistry reg, Vector2Int region)
    {
        if (!_byRoom.TryGetValue(region, out var g)) return;
        foreach (var p in reg.Players.Values)
            if (p.inInstance && p.regionKey == region) return;   // someone is still in this room
        reg.RemoveCombatant(g.entityId);
        _byRoom.Remove(region);
    }

    // Respawn-fresh: any dummy at 0 HP resets to full HP + clears effects. Called each server fixed tick.
    public void TickRespawn()
    {
        foreach (var g in _byRoom.Values)
            if (g.hp <= 0) { g.hp = g.stats.GetInt(StatKind.MaxHp); g.status.Clear(); }
    }

    // Deterministic NPC id from the room origin: unique per room (room coords are positive band cells < 100000)
    // and offset by NpcIdBase so it never collides with an NGO clientId.
    static ulong GoblinId(Vector2Int r) => Combatant.NpcIdBase + (ulong)(uint)r.x * 100000UL + (ulong)(uint)r.y;
}
