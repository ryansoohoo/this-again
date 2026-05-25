using UnityEngine;

// Deterministic land/water generator. One LOW-frequency continentalness field (fBm + a contrast curve)
// carves wide oceans vs. solid continents; domain warping nudges the sample position so coastlines read
// organic, not blobby. Same seed -> identical map; all knobs are live-tunable via TunerPanels.
public sealed class BiomeGenerator
{
    const int ContinentOctaves = 2;   // few octaves -> smooth, big water bodies (not speckled)

    readonly float scale, waterScale, warp, sea, contrast;
    readonly float cOx, cOy, wOx, wOy, w2Ox, w2Oy;

    public BiomeGenerator(BiomeSettings s)
    {
        scale = Mathf.Max(s.biomeScale, 0.0001f);
        waterScale = Mathf.Max(s.waterScale, 0.0001f);
        warp = s.warpStrength; sea = s.seaLevel; contrast = Mathf.Max(s.continentContrast, 0.01f);
        var rng = new System.Random(s.seed);
        Noise.Offset(rng); Noise.Offset(rng); Noise.Offset(rng); Noise.Offset(rng);   // (kept) the old temp/moisture offset draws, so existing seeds keep their exact coastlines
        cOx = Noise.Offset(rng); cOy = Noise.Offset(rng); wOx = Noise.Offset(rng); wOy = Noise.Offset(rng); w2Ox = Noise.Offset(rng); w2Oy = Noise.Offset(rng);
    }

    // True = land, false = open water.
    public bool IsLand(int x, int y)
    {
        float wx = x, wy = y;
        if (warp > 0f)
        {
            wx += (Mathf.PerlinNoise(wOx + x * scale, wOy + y * scale) - 0.5f) * 2f * warp;
            wy += (Mathf.PerlinNoise(w2Ox + x * scale, w2Oy + y * scale) - 0.5f) * 2f * warp;
        }
        float cont = Noise.Fbm(wx, wy, cOx, cOy, waterScale, ContinentOctaves);
        cont = Mathf.Clamp01((cont - 0.5f) * contrast + 0.5f);
        return cont >= sea;
    }
}
