using System.Collections.Generic;
using UnityEngine;

// Character stat identity. The value is an index into the base-stat array — add a new stat here AND bump
// StatKinds.Count. Only MaxHp exists in v1; Damage/Defense/MoveSpeed/Mass come with the stats/items features.
public enum StatKind : byte { MaxHp = 0 }

public static class StatKinds { public const int Count = 1; }   // == number of StatKind values

public enum ModOp : byte { Add, Mul }

// One runtime stat modifier (item / level / long-lived buff). sourceId lets a source remove exactly its own mods.
public struct StatModifier { public StatKind stat; public ModOp op; public float value; public int sourceId; }

// Per-combatant resolved stats: base values (seeded from CharacterDef) + a list of modifiers. Resolution is pure
// and order-independent — effective = (base + Σadd) × Πmul — so it is deterministic regardless of insertion order.
// This is the PERSISTENT base-stat layer; transient combat effects (hitstun/slow/DoT) live in StatusState, not here.
public sealed class Stats
{
    readonly float[] _base = new float[StatKinds.Count];
    readonly List<StatModifier> _mods = new();

    public Stats() { }

    public void SetBase(StatKind stat, float value) => _base[(int)stat] = value;

    public void AddModifier(StatModifier m) => _mods.Add(m);

    public void RemoveBySource(int sourceId)
    {
        for (int i = _mods.Count - 1; i >= 0; i--)
            if (_mods[i].sourceId == sourceId) _mods.RemoveAt(i);
    }

    public float Get(StatKind stat)
    {
        float add = 0f, mul = 1f;
        for (int i = 0; i < _mods.Count; i++)
        {
            var m = _mods[i];
            if (m.stat != stat) continue;
            if (m.op == ModOp.Add) add += m.value; else mul *= m.value;
        }
        return (_base[(int)stat] + add) * mul;
    }

    public int GetInt(StatKind stat) => Mathf.RoundToInt(Get(stat));
}
