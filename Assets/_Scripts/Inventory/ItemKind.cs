// Category tag for an inventory slot. Drives lookup against the matching catalog (WeaponCatalog or
// ConsumableCatalog). 0 = None (empty slot). Future categories take 3+.
public enum ItemKind : byte
{
    None       = 0,
    Weapon     = 1,
    Consumable = 2,
}
