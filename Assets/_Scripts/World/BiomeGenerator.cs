using UnityEngine;

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
