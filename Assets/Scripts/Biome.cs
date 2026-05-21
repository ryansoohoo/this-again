using UnityEngine;

// Flat-2D terrain biomes (climate + continentalness; no 3D elevation). Each biome renders as a floor-tile
// sprite. The flat colors below are only a fallback when a biome's tile is unassigned.
public enum Biome { Dirt, Grass, Mushroom, Sand, GrassSwamp, Stone, Water }

public static class Biomes
{
    // Water gradient endpoints (shallow near shore -> deep offshore), used by the in-game mesh.
    // GridManager sets these live each build from a tint x brightness-level (tunable, no recompile).
    public static Color32 WaterShallow = new Color32( 5, 12, 17, 255);
    public static Color32 WaterDeep    = new Color32( 1,  3,  4, 255);

    // Per-biome ground color drawn behind that biome's non-water tiles (the sparse floor sprites sit
    // on top). GridManager sets these live = each tile's average art color x brightness (no recompile).
    public static Color32[] GroundColors;

    public static Color32 ColorOf(Biome b)
    {
        switch (b)
        {
            case Biome.Dirt:       return new Color32(120,  85,  55, 255);
            case Biome.Grass:      return new Color32(110, 180,  70, 255);
            case Biome.Mushroom:   return new Color32(150,  80, 140, 255);
            case Biome.Sand:       return new Color32(224, 205, 124, 255);
            case Biome.GrassSwamp: return new Color32( 70, 120,  70, 255);
            case Biome.Stone:      return new Color32(130, 130, 138, 255);
            case Biome.Water:      return new Color32( 50, 120, 200, 255);  // flat fallback; water normally uses the gradient
            default:               return new Color32(255,   0, 255, 255);
        }
    }
}

// All live-tunable biome knobs in one place: serialized on GridManager, mutated by BiomeTuner,
// saved/loaded as a single JSON blob. Defaults match the original GridManager inspector defaults.
[System.Serializable]
public class BiomeSettings
{
    public int seed = 1337;
    public float biomeScale = 0.08f;                                 // smaller = larger biome regions
    [Range(1, 8)] public int octaves = 4;                            // fBm detail (more = finer)
    public float warpStrength = 8f;                                  // domain-warp distortion, in cells
    [Range(0f, 1f)] public float seaLevel = 0.40f;                   // continentalness below -> ocean
    public float waterScale = 0.03f;                                 // LOW freq continent field (smaller = bigger continents/oceans)
    public float continentContrast = 2.5f;                          // pushes the continent field to extremes (solid land, wide ocean)
    public Color waterTint = new Color(0.235f, 0.588f, 0.843f, 1f);  // base water hue (Inspector picker)
    [Range(0f, 1f)] public float waterShoreLevel = 0.08f;           // shore water brightness (live)
    [Range(0f, 1f)] public float waterDeepLevel = 0.02f;            // deep water brightness (live)
    public int waterDepthCells = 12;                                // shore->deep ramp length (bigger = vaster-looking oceans)
    [Range(0f, 1f)] public float landBackgroundLevel = 0.5f;        // ground = each tile's avg art color x this (0.5 = darken 50%)
    [Range(1f, 3f)] public float minimapBrightness = 1.6f;          // minimap colors x this (brighter than the in-game map)
    [Range(0f, 1f)] public float coldThreshold = 0.40f;            // temp below -> cold (Stone/Mushroom)
    [Range(0f, 1f)] public float hotThreshold = 0.60f;             // temp above -> hot (Sand/GrassSwamp)
    [Range(0f, 1f)] public float wetThreshold = 0.50f;            // moisture at/above -> wet
}

// Deterministic multi-noise biome generator (Minecraft-ish):
//   - PASS 1 continentalness (low-freq fBm + contrast curve) carves vast oceans vs. solid
//     continents, plus a thin coastal beach band — this pass alone defines the landmasses,
//   - PASS 2 temperature & moisture (fBm) classify the biome on whatever land survived,
//   - domain warping nudges the sample position so coastlines are organic, not blobby.
// Same seed -> identical map. All knobs are tunable live by BiomeTuner.
public sealed class BiomeGenerator
{
    const float Lacunarity = 2f, Persistence = 0.5f, BeachWidth = 0.03f;
    const int ContinentOctaves = 2;   // few octaves -> smooth, big water bodies (not speckled)

    readonly int octaves;
    readonly float scale, waterScale, warp, sea, contrast, cold, hot, wet;
    readonly float tOx, tOy, mOx, mOy, cOx, cOy, wOx, wOy, w2Ox, w2Oy;

    public BiomeGenerator(BiomeSettings s)
    {
        scale = Mathf.Max(s.biomeScale, 0.0001f);
        waterScale = Mathf.Max(s.waterScale, 0.0001f);
        octaves = Mathf.Clamp(s.octaves, 1, 8);
        warp = s.warpStrength; sea = s.seaLevel; contrast = Mathf.Max(s.continentContrast, 0.01f);
        cold = s.coldThreshold; hot = s.hotThreshold; wet = s.wetThreshold;
        var rng = new System.Random(s.seed);                // seed -> deterministic field offsets
        tOx = R(rng); tOy = R(rng); mOx = R(rng); mOy = R(rng); cOx = R(rng); cOy = R(rng);
        wOx = R(rng); wOy = R(rng); w2Ox = R(rng); w2Oy = R(rng);
    }

    static float R(System.Random r) => (float)r.NextDouble() * 1000f;

    public Biome At(int x, int y)
    {
        // Domain warp: offset the sample position by a noise vector (in cells) for organic boundaries.
        float wx = x, wy = y;
        if (warp > 0f)
        {
            wx += (Mathf.PerlinNoise(wOx + x * scale, wOy + y * scale) - 0.5f) * 2f * warp;
            wy += (Mathf.PerlinNoise(w2Ox + x * scale, w2Oy + y * scale) - 0.5f) * 2f * warp;
        }

        // PASS 1 — OCEANS & CONTINENTS: one LOW-frequency field is the whole world's skeleton.
        // The contrast curve pushes it toward 0/1 so continents read as solid landmasses and
        // oceans stay wide-open; a flat threshold on raw Perlin gives a mushy ~50/50 coast instead.
        float cont = Fbm(wx, wy, cOx, cOy, waterScale, ContinentOctaves);
        cont = Mathf.Clamp01((cont - 0.5f) * contrast + 0.5f);
        if (cont < sea) return Biome.Water;                 // ocean (renderer shades shore->deep)
        if (cont < sea + BeachWidth) return Biome.Sand;     // coastal beach

        // PASS 2 — CLIMATE: classify whatever land survived pass 1 by temperature & moisture.
        float temp  = Fbm(wx, wy, tOx, tOy, scale, octaves);
        float moist = Fbm(wx, wy, mOx, mOy, scale, octaves);
        bool isWet = moist >= wet;
        if (temp < cold) return isWet ? Biome.Mushroom : Biome.Stone;   // cold: damp fungal vs rocky
        if (temp > hot)  return isWet ? Biome.GrassSwamp : Biome.Sand;  // hot:  swamp vs desert
        return isWet ? Biome.Grass : Biome.Dirt;                         // temperate: grass vs bare earth
    }

    // Fractal Brownian motion: sum octaves of Perlin at rising frequency / falling amplitude. Returns ~0..1.
    float Fbm(float x, float y, float ox, float oy, float baseFreq, int oct)
    {
        float freq = baseFreq, amp = 1f, sum = 0f, norm = 0f;
        for (int o = 0; o < oct; o++)
        {
            sum += amp * Mathf.PerlinNoise(ox + x * freq, oy + y * freq);
            norm += amp;
            amp *= Persistence;
            freq *= Lacunarity;
        }
        return sum / norm;
    }
}
