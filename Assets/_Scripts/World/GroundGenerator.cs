using UnityEngine;

// Two fBm fields: an "elevation" field carves mountain peaks > rocky foothills > lowland; within the lowland
// a separate "moisture" field splits forest vs grass. Deterministic from the seed, so Netcode clients agree.
public sealed class GroundGenerator
{
    const float Lacunarity = 2f, Persistence = 0.5f;
    readonly float scale, mountainLevel, rockyLevel, forestLevel;
    readonly int octaves;
    readonly float eOx, eOy, mOx, mOy;

    public GroundGenerator(int seed, GroundSettings s)
    {
        scale = Mathf.Max(s.scale, 0.0001f);
        octaves = Mathf.Clamp(s.octaves, 1, 6);
        mountainLevel = s.mountainLevel; rockyLevel = s.rockyLevel; forestLevel = s.forestLevel;
        var rng = new System.Random(seed * 131071 + 99);   // distinct from the biome generator's offsets
        eOx = R(rng); eOy = R(rng); mOx = R(rng); mOy = R(rng);
    }

    static float R(System.Random r) => (float)r.NextDouble() * 1000f;

    public GroundType At(int x, int y)
    {
        float elev = Fbm(x, y, eOx, eOy);
        if (elev > mountainLevel) return GroundType.Mountain;
        if (elev > rockyLevel) return GroundType.Rocky;
        float moist = Fbm(x, y, mOx, mOy);
        return moist >= forestLevel ? GroundType.Forest : GroundType.Grass;
    }

    float Fbm(float x, float y, float ox, float oy)
    {
        float freq = scale, amp = 1f, sum = 0f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Mathf.PerlinNoise(ox + x * freq, oy + y * freq);
            norm += amp;
            amp *= Persistence;
            freq *= Lacunarity;
        }
        return sum / norm;
    }
}
