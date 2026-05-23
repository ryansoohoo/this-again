using UnityEngine;

// Ground-cover variety for LAND tiles (purely visual; gameplay only cares about water vs not-water).
// A deterministic multi-noise classifier (GroundGenerator) splits interior land into four cover types.
public enum GroundType { Grass, Forest, Rocky, Mountain }

// Tunable knobs for the ground-cover noise (serialized on Game).
[System.Serializable]
public class GroundSettings
{
    public float scale = 0.05f;                          // region size (smaller = larger patches)
    [Range(1, 6)] public int octaves = 3;
    [Range(0f, 1f)] public float mountainLevel = 0.66f;  // elevation above -> Mountain (peaks; rare)
    [Range(0f, 1f)] public float rockyLevel = 0.56f;     // elevation above -> Rocky (foothills)
    [Range(0f, 1f)] public float forestLevel = 0.50f;    // lowland moisture at/above -> Forest, else Grass

    // Per-biome coverage: fraction of a biome's cells that actually GET the biome. The rest render as the
    // blank/normal ground tile. 1 = solid (no blank); lower = more scattered blank.
    [Range(0f, 1f)] public float grassCoverage = 0.3f;
    [Range(0f, 1f)] public float forestCoverage = 1f;
    [Range(0f, 1f)] public float rockyCoverage = 1f;
    [Range(0f, 1f)] public float mountainCoverage = 1f;
}
