using UnityEngine;

// Live-tunable land/water knobs: serialized on the Game object, mutated by TunerPanels, saved/loaded as one
// JSON blob (PlayerPrefs). Climate sub-classification was removed — terrain is a binary land/water field.
[System.Serializable]
public class BiomeSettings
{
    public int seed = 1337;
    public float biomeScale = 0.08f;                        // domain-warp frequency (smaller = larger warp features)
    public float warpStrength = 8f;                         // domain-warp distortion, in cells
    [Range(0f, 1f)] public float seaLevel = 0.40f;          // continentalness below this -> ocean
    public float waterScale = 0.03f;                        // LOW-freq continent field (smaller = bigger continents/oceans)
    public float continentContrast = 2.5f;                 // pushes the continent field to extremes (solid land, wide ocean)
    [Range(1f, 3f)] public float minimapBrightness = 1.6f;  // minimap colors x this (brighter than the in-game map)
}

// Deterministic land/water generator. One LOW-frequency continentalness field (fBm + a contrast curve)
// carves wide oceans vs. solid continents; domain warping nudges the sample position so coastlines read
// organic, not blobby. Same seed -> identical map; all knobs are live-tunable via TunerPanels.
public sealed class BiomeGenerator
{
    const float Lacunarity = 2f, Persistence = 0.5f;
    const int ContinentOctaves = 2;   // few octaves -> smooth, big water bodies (not speckled)

    readonly float scale, waterScale, warp, sea, contrast;
    readonly float cOx, cOy, wOx, wOy, w2Ox, w2Oy;

    public BiomeGenerator(BiomeSettings s)
    {
        scale = Mathf.Max(s.biomeScale, 0.0001f);
        waterScale = Mathf.Max(s.waterScale, 0.0001f);
        warp = s.warpStrength; sea = s.seaLevel; contrast = Mathf.Max(s.continentContrast, 0.01f);
        var rng = new System.Random(s.seed);
        R(rng); R(rng); R(rng); R(rng);   // (kept) the old temp/moisture offset draws, so existing seeds keep their exact coastlines
        cOx = R(rng); cOy = R(rng); wOx = R(rng); wOy = R(rng); w2Ox = R(rng); w2Oy = R(rng);
    }

    static float R(System.Random r) => (float)r.NextDouble() * 1000f;

    // True = land, false = open water.
    public bool IsLand(int x, int y)
    {
        float wx = x, wy = y;
        if (warp > 0f)
        {
            wx += (Mathf.PerlinNoise(wOx + x * scale, wOy + y * scale) - 0.5f) * 2f * warp;
            wy += (Mathf.PerlinNoise(w2Ox + x * scale, w2Oy + y * scale) - 0.5f) * 2f * warp;
        }
        float cont = Fbm(wx, wy, cOx, cOy, waterScale, ContinentOctaves);
        cont = Mathf.Clamp01((cont - 0.5f) * contrast + 0.5f);
        return cont >= sea;
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
