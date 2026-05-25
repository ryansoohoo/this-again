using UnityEngine;

// Shared deterministic noise primitives for the terrain generators (land/water + ground cover). Pure -> the
// same seed yields the same field on every client, with no replication. Previously each generator carried its
// own copy of the fBm loop and the offset draw.
public static class Noise
{
    // One coordinate-offset draw in [0, 1000) from a seeded RNG — the generators' offset convention. Advances
    // the RNG exactly one NextDouble(), so existing seeds keep their exact coastlines.
    public static float Offset(System.Random r) => (float)r.NextDouble() * 1000f;

    // Fractal Brownian motion: sum `oct` octaves of Perlin at rising frequency / falling amplitude. Returns ~0..1.
    public static float Fbm(float x, float y, float ox, float oy, float baseFreq, int oct, float lacunarity = 2f, float persistence = 0.5f)
    {
        float freq = baseFreq, amp = 1f, sum = 0f, norm = 0f;
        for (int o = 0; o < oct; o++)
        {
            sum += amp * Mathf.PerlinNoise(ox + x * freq, oy + y * freq);
            norm += amp;
            amp *= persistence;
            freq *= lacunarity;
        }
        return sum / norm;
    }
}
