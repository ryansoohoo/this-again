using UnityEngine;

// Live-tunable knobs for structure-site placement (serialized on Game; persisted via JsonPref like the
// biome/ground/water settings). Sites sit on a jittered region grid: the world is divided into
// blockSize x blockSize cell blocks, each of which deterministically rolls AT MOST one site.
[System.Serializable]
public class StructureSettings
{
    [Min(2)] public int blockSize = 14;                          // region block edge in cells (smaller = denser)
    [Range(0f, 1f)] public float siteChance = 0.6f;             // chance a block contains a site
    public Color markerColor = new Color(1f, 0.85f, 0.2f, 1f);  // minimap dot color for a site cell
}
