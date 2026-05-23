using UnityEngine;

// Data-only asset: the pool of ground-tile sprites a single biome may use, each with a relative weight
// (rarity). One asset per biome (e.g. Forest, Rocky, Mountain). World picks one variant per land cell
// deterministically (hash of cell + seed) so all Netcode clients agree without replication. Holds NO logic —
// just sprite data. All sprites must be slices of the summer sheet bound to the terrain material.
[CreateAssetMenu(fileName = "Biome", menuName = "World/Biome Tiles")]
public class BiomeTiles : ScriptableObject
{
    [Tooltip("Color this biome shows as on the minimap (the tile's representative color)")]
    public Color minimapColor = Color.white;

    [Min(0), Tooltip("Extra time to walk across a tile of this biome, in tenths of a second (0 = normal). " +
                     "1 = +0.1s, 10 = +1s. A normal tile crosses in ~0.25s. Used by movement + click-to-move routing.")]
    public int extraMoveCost;

    public BiomeTileVariant[] variants;
}

// One weighted tile choice. weight is a relative frequency: higher = appears more often. A variant with
// weight 0 (or a null sprite) is skipped. Picking is weighted-random but deterministic per cell.
[System.Serializable]
public class BiomeTileVariant
{
    public Sprite sprite;
    [Min(0f), Tooltip("Relative frequency (rarity) — higher = more common")]
    public float weight = 1f;
}
