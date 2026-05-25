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
    const int MaxLandCacheEntries = 1 << 17;                    // ~131k cells; bound the memo cache over a long session
    readonly Dictionary<Vector2Int, bool> landCache = new();   // generated land(true)/water(false); flushed at the cap
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
        if (Underworld.Contains(cx, cy)) return Underworld.IsGround(cx, cy);   // off-map dungeon rooms: flat plains, water-moat walls
        var key = new Vector2Int(cx, cy);
        if (landCache.TryGetValue(key, out var v)) return v;
        v = gen.IsLand(cx, cy);
        if (landCache.Count >= MaxLandCacheEntries) landCache.Clear();         // deterministic memo: safe to flush + refill
        landCache[key] = v;
        return v;
    }

    public bool IsWalkable(int cx, int cy) => IsLand(cx, cy);   // everything but open water is walkable

    // The structure site occupying a cell, or null. Public so the encounter layer can ask "am I on a site?".
    public StructureSite SiteAt(int cx, int cy) => !Underworld.Contains(cx, cy) && structureGen != null ? structureGen.SiteAt(cx, cy) : null;

    // Per-cell interior-land sprite (renderer's all-land case): a structure site's tile wins; otherwise
    // classify cover, roll coverage, pick a weighted biome variant. Null -> the built-in blank ground tile.
    public Sprite LandSprite(int cx, int cy)
    {
        if (Underworld.Contains(cx, cy)) return cfg.defaultGroundSprite;   // dungeon plains: the plain ground tile everywhere
        var site = SiteAt(cx, cy);
        if (site != null)
        {
            var s = PickVariant(site.Def.variants, cx, cy, 7777);
            if (s != null) return s;
            // structure art unassigned/foreign -> render as normal ground (the site still triggers encounters)
        }
        var gt = groundGen.At(cx, cy);
        if (CoverageRolledOut(cx, cy, gt)) return cfg.defaultGroundSprite;
        var picked = PickVariant(BiomeFor(gt)?.variants, cx, cy, (int)gt);
        return picked != null ? picked : cfg.defaultGroundSprite;
    }

    // Minimap color for a land cell: a structure site shows as a marker dot; else its cover-biome color, or
    // the blank color when coverage rolls it out.
    public Color32 LandColor(int cx, int cy)
    {
        if (Underworld.Contains(cx, cy)) return cfg.defaultGroundColor;
        if (cfg.structureSettings != null && SiteAt(cx, cy) != null) return (Color32)cfg.structureSettings.markerColor;
        var gt = groundGen.At(cx, cy);
        if (CoverageRolledOut(cx, cy, gt)) return cfg.defaultGroundColor;
        var b = BiomeFor(gt);
        return b != null ? (Color32)b.minimapColor : cfg.defaultGroundColor;
    }

    // Extra movement cost for ENTERING a land cell, in the biome's own int units (tenths of a second).
    // Mirrors LandSprite/LandColor so cost matches what's rendered: water (not walkable) and a structure-site
    // cell cost nothing, and a cell whose coverage rolls out (renders blank ground) costs nothing — only an
    // actual biome tile charges its extraMoveCost. Deterministic, but only the authoritative server queries it.
    public int MoveCost(int cx, int cy)
    {
        if (Underworld.Contains(cx, cy)) return 0;
        if (!IsLand(cx, cy)) return 0;
        if (SiteAt(cx, cy) != null) return 0;
        var gt = groundGen.At(cx, cy);
        if (CoverageRolledOut(cx, cy, gt)) return 0;
        var b = BiomeFor(gt);
        return b != null ? Mathf.Max(0, b.extraMoveCost) : 0;
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

    // The shared coverage roll: true = this cell renders as blank ground (its cover type rolled out). One
    // source of truth so LandSprite / LandColor / MoveCost can never disagree about which cells are bare.
    bool CoverageRolledOut(int cx, int cy, GroundType gt) => Hash01(cx, cy, cfg.biome.seed, 1009 + (int)gt) >= CoverageFor(gt);

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

    // Deterministic 0..1 hash of (cell, seed, salt) via the shared mixer. Pure -> identical on every client.
    static float Hash01(int x, int y, int seed, int salt) => WorldHash.Unit(x, y, seed, salt);
}
