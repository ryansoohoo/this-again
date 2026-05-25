using UnityEngine;

// Two fBm fields: an "elevation" field carves mountain peaks > rocky foothills > lowland; within the lowland
// a separate "moisture" field splits forest vs grass. Deterministic from the seed, so Netcode clients agree.
public sealed class GroundGenerator
{
    readonly float scale, mountainLevel, rockyLevel, forestLevel;
    readonly int octaves;
    readonly float eOx, eOy, mOx, mOy;

    public GroundGenerator(int seed, GroundSettings s)
    {
        scale = Mathf.Max(s.scale, 0.0001f);
        octaves = Mathf.Clamp(s.octaves, 1, 6);
        mountainLevel = s.mountainLevel; rockyLevel = s.rockyLevel; forestLevel = s.forestLevel;
        var rng = new System.Random(seed * 131071 + 99);   // distinct from the biome generator's offsets
        eOx = Noise.Offset(rng); eOy = Noise.Offset(rng); mOx = Noise.Offset(rng); mOy = Noise.Offset(rng);
    }

    public GroundType At(int x, int y)
    {
        float elev = Noise.Fbm(x, y, eOx, eOy, scale, octaves);
        if (elev > mountainLevel) return GroundType.Mountain;
        if (elev > rockyLevel) return GroundType.Rocky;
        float moist = Noise.Fbm(x, y, mOx, mOy, scale, octaves);
        return moist >= forestLevel ? GroundType.Forest : GroundType.Grass;
    }
}
