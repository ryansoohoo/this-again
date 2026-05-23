using System.Collections.Generic;
using UnityEngine;

// Construction inputs for World (top-level, not nested, so Game can build it without `World.Config` clashing
// with Game's `World` property).
public sealed class WorldConfig
{
    public BiomeSettings biome;
    public GroundSettings ground;
    public Texture2D summerSheet;
    public BiomeTiles grass, forest, rocky, mountain;
    public StructureSet structures;
    public StructureSettings structureSettings;
    public Sprite defaultGroundSprite;
    public Color32 defaultGroundColor;
}

// The terrain pipeline: answers "what is at cell (x,y)?" A pure query layer over deterministic noise
// generators (land/water, ground-cover, structure sites) + the per-biome tile assets. Same seed -> identical
// answers on every Netcode client (no replication). Game builds it; WorldView samples it for the mesh/minimap;
// Player/Pathfinder use IsWalkable; EncounterManager uses SiteAt.
public sealed class World
{
    readonly WorldConfig cfg;
    BiomeGenerator gen;
    GroundGenerator groundGen;
    StructureGenerator structureGen;
    readonly Dictionary<Vector2Int, bool> landCache = new();   // generated land(true)/water(false); grows as you explore
    readonly HashSet<Sprite> warnedForeign = new();

    public World(WorldConfig cfg) { this.cfg = cfg; Rebuild(); }

    // Rebuild the generators + clear the cache (after a live settings change).
    public void Rebuild()
    {
        gen = new BiomeGenerator(cfg.biome);
        groundGen = new GroundGenerator(cfg.biome.seed, cfg.ground);
        structureGen = new StructureGenerator(cfg.biome.seed, cfg.structureSettings ?? new StructureSettings(),
                                              cfg.structures, IsLand, (x, y) => groundGen.At(x, y));
        landCache.Clear();
    }

    public bool IsLand(int cx, int cy)
    {
        var key = new Vector2Int(cx, cy);
        if (landCache.TryGetValue(key, out var v)) return v;
        v = gen.IsLand(cx, cy);
        landCache[key] = v;
        return v;
    }

    public bool IsWalkable(int cx, int cy) => IsLand(cx, cy);   // everything but open water is walkable

    // The structure site occupying a cell, or null. Public so the encounter layer can ask "am I on a site?".
    public StructureSite SiteAt(int cx, int cy) => structureGen != null ? structureGen.SiteAt(cx, cy) : null;

    // Per-cell interior-land sprite (renderer's all-land case): a structure site's tile wins; otherwise
    // classify cover, roll coverage, pick a weighted biome variant. Null -> the built-in blank ground tile.
    public Sprite LandSprite(int cx, int cy)
    {
        var site = SiteAt(cx, cy);
        if (site != null)
        {
            var s = PickVariant(site.Def.variants, cx, cy, 7777);
            if (s != null) return s;
            // structure art unassigned/foreign -> render as normal ground (the site still triggers encounters)
        }
        var gt = groundGen.At(cx, cy);
        if (Hash01(cx, cy, cfg.biome.seed, 1009 + (int)gt) >= CoverageFor(gt)) return cfg.defaultGroundSprite;
        var picked = PickVariant(BiomeFor(gt)?.variants, cx, cy, (int)gt);
        return picked != null ? picked : cfg.defaultGroundSprite;
    }

    // Minimap color for a land cell: a structure site shows as a marker dot; else its cover-biome color, or
    // the blank color when coverage rolls it out.
    public Color32 LandColor(int cx, int cy)
    {
        if (cfg.structureSettings != null && SiteAt(cx, cy) != null) return (Color32)cfg.structureSettings.markerColor;
        var gt = groundGen.At(cx, cy);
        if (Hash01(cx, cy, cfg.biome.seed, 1009 + (int)gt) >= CoverageFor(gt)) return cfg.defaultGroundColor;
        var b = BiomeFor(gt);
        return b != null ? (Color32)b.minimapColor : cfg.defaultGroundColor;
    }

    BiomeTiles BiomeFor(GroundType gt) => gt switch
    {
        GroundType.Forest   => cfg.forest,
        GroundType.Rocky    => cfg.rocky,
        GroundType.Mountain => cfg.mountain,
        _                   => cfg.grass,
    };

    float CoverageFor(GroundType gt) => gt switch
    {
        GroundType.Forest   => cfg.ground.forestCoverage,
        GroundType.Rocky    => cfg.ground.rockyCoverage,
        GroundType.Mountain => cfg.ground.mountainCoverage,
        _                   => cfg.ground.grassCoverage,
    };

    // Weighted-random variant pick made deterministic by hashing (cell, seed, salt) -> every client agrees.
    Sprite PickVariant(BiomeTileVariant[] variants, int cx, int cy, int salt)
    {
        if (variants == null) return null;
        float total = 0f;
        for (int i = 0; i < variants.Length; i++) if (IsUsable(variants[i])) total += variants[i].weight;
        if (total <= 0f) return null;
        float r = Hash01(cx, cy, cfg.biome.seed, salt) * total;
        for (int i = 0; i < variants.Length; i++)
        {
            if (!IsUsable(variants[i])) continue;
            r -= variants[i].weight;
            if (r < 0f) return variants[i].sprite;
        }
        for (int i = variants.Length - 1; i >= 0; i--) if (IsUsable(variants[i])) return variants[i].sprite;
        return null;
    }

    // Usable only if a positive-weight sprite sliced from the summer sheet (a foreign texture would index the
    // wrong atlas on the shared single-material terrain mesh).
    bool IsUsable(BiomeTileVariant v)
    {
        if (v == null || v.sprite == null || v.weight <= 0f) return false;
        if (cfg.summerSheet != null && v.sprite.texture != cfg.summerSheet) { WarnForeign(v.sprite); return false; }
        return true;
    }

    void WarnForeign(Sprite s)
    {
        if (warnedForeign.Add(s))
            Debug.LogWarning($"[World] Biome sprite '{s.name}' is not from the summer sheet; ignoring it. Slice biome tiles from world_map_tiles_SUMMER.");
    }

    // Deterministic 0..1 hash of (cell, seed, salt). Pure -> identical variant choice on every client.
    static float Hash01(int x, int y, int seed, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791) ^ (uint)(salt * 2654435761);
            h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
