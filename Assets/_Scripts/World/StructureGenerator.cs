using System;
using UnityEngine;

// Deterministic structure-site placement on a jittered region grid: the world is divided into
// blockSize x blockSize cell blocks; each block hashes to AT MOST one site at a jittered cell inside it.
// SiteAt(cx,cy) computes only the containing block's single candidate, so it is O(1) per cell. Pure and
// seed-driven -> every Netcode client agrees with no replication. Picks the structure TYPE + NAME here;
// World picks the sprite variant (so summer-sheet validation lives in one place, in World.IsUsable).
public sealed class StructureGenerator
{
    const int SaltPresence = 50001, SaltX = 50002, SaltY = 50003, SaltType = 50004, SaltName = 50005;

    readonly int seed, blockSize;
    readonly float siteChance;
    readonly StructureSet set;
    readonly Func<int, int, bool> isLand;
    readonly Func<int, int, GroundType> groundAt;

    public StructureGenerator(int seed, StructureSettings s, StructureSet set,
                              Func<int, int, bool> isLand, Func<int, int, GroundType> groundAt)
    {
        this.seed = seed;
        this.blockSize = Mathf.Max(2, s.blockSize);
        this.siteChance = Mathf.Clamp01(s.siteChance);
        this.set = set;
        this.isLand = isLand;
        this.groundAt = groundAt;
    }

    // The site occupying cell (cx,cy), or null. Only the containing block's single candidate can match.
    public StructureSite SiteAt(int cx, int cy)
    {
        if (set == null || set.defs == null || set.defs.Length == 0) return null;

        int bx = FloorDiv(cx, blockSize), by = FloorDiv(cy, blockSize);
        if (Hash01(bx, by, SaltPresence) >= siteChance) return null;

        int sx = bx * blockSize + (int)(Hash01(bx, by, SaltX) * blockSize);
        int sy = by * blockSize + (int)(Hash01(bx, by, SaltY) * blockSize);
        if (sx != cx || sy != cy) return null;
        if (!IsInlandLand(sx, sy)) return null;   // must be land AND not on the coastline

        var def = PickDef(groundAt(sx, sy), sx, sy);
        if (def == null) return null;

        return new StructureSite(def, new Vector2Int(sx, sy), PickName(sx, sy));
    }

    // Land that isn't on the coastline: the cell and all 8 neighbours are land, so no coast art touches the
    // structure tile (the dual-grid renderer draws coast only where a corner's 4 cells are mixed land/water).
    bool IsInlandLand(int x, int y)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
            if (!isLand(x + dx, y + dy)) return false;
        return true;
    }

    StructureDef PickDef(GroundType gt, int x, int y)
    {
        float total = 0f;
        for (int i = 0; i < set.defs.Length; i++)
            if (Accepts(set.defs[i], gt)) total += Mathf.Max(0f, set.defs[i].spawnWeight);
        if (total <= 0f) return null;

        float r = Hash01(x, y, SaltType) * total;
        for (int i = 0; i < set.defs.Length; i++)
        {
            if (!Accepts(set.defs[i], gt)) continue;
            r -= Mathf.Max(0f, set.defs[i].spawnWeight);
            if (r < 0f) return set.defs[i];
        }
        return null;
    }

    static bool Accepts(StructureDef d, GroundType gt)
    {
        if (d == null || d.validOn == null) return false;
        for (int i = 0; i < d.validOn.Length; i++) if (d.validOn[i] == gt) return true;
        return false;
    }

    string PickName(int x, int y)
    {
        if (set.namePool == null || set.namePool.Length == 0) return "an unnamed place";
        int idx = (int)(Hash01(x, y, SaltName) * set.namePool.Length);
        if (idx >= set.namePool.Length) idx = set.namePool.Length - 1;
        return set.namePool[idx];
    }

    // Integer floor division so blocks tile correctly across the origin (C# '/' truncates toward zero).
    static int FloorDiv(int a, int b) => (a >= 0 ? a : a - b + 1) / b;

    // Deterministic 0..1 hash of (cell, salt) for this generator's seed, via the shared mixer.
    float Hash01(int x, int y, int salt) => WorldHash.Unit(x, y, seed, salt);
}

// A placed structure site: which type, where, and its deterministic name. World picks the sprite variant.
public sealed class StructureSite
{
    public readonly StructureDef Def;
    public readonly Vector2Int Cell;
    public readonly string Name;
    public StructureSite(StructureDef def, Vector2Int cell, string name) { Def = def; Cell = cell; Name = name; }
}
