using UnityEngine;

// Shared byte-id <-> ConsumableDefinition map, parallel to WeaponCatalog. Wired on Game.
[CreateAssetMenu(menuName = "Minifantasy/Consumable Catalog", fileName = "ConsumableCatalog")]
public sealed class ConsumableCatalog : ScriptableObject
{
    public ConsumableDefinition[] entries;

    public ConsumableDefinition Get(byte id) =>
        entries != null && id < entries.Length ? entries[id] : null;

    public int IndexOf(ConsumableDefinition def)
    {
        if (entries == null) return -1;
        for (int i = 0; i < entries.Length; i++) if (entries[i] == def) return i;
        return -1;
    }
}
