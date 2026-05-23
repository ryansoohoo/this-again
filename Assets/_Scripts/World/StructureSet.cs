using UnityEngine;

// Data-only asset: the catalog of structure types that can be placed on the map (forest town, farm, camp,
// ...). One asset for the whole world; you curate the sprite variants per type in the Inspector. Holds NO
// logic. All variant sprites must be slices of the summer sheet (same shared-material constraint as biome
// tiles); World validates this and falls back to plain ground for unassigned/foreign sprites.
[CreateAssetMenu(fileName = "Structures", menuName = "World/Structure Set")]
public class StructureSet : ScriptableObject
{
    public StructureDef[] defs;
    [Tooltip("Shared pool of site names; one is picked deterministically per site.")]
    public string[] namePool;
}

// One placeable structure type. `variants` reuses the biome weighted-variant type: each entry is a weighted
// summer-sheet slice; "(2 variations)" = 2 entries, "(4 variations)" = 4.
[System.Serializable]
public class StructureDef
{
    [Tooltip("Stable id, e.g. forest_town — used later as the town-memory key.")]
    public string id;
    [Tooltip("Shown in encounter text, e.g. 'the town', 'a camp'.")]
    public string label;
    [Tooltip("Ground-cover types this structure may spawn on (land only).")]
    public GroundType[] validOn;
    [Min(0f)] public float spawnWeight = 1f;
    public BiomeTileVariant[] variants;
}
