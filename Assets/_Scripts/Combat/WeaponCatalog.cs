using UnityEngine;

// Shared byte-id <-> AttackDefinition map. The wire carries a 1-byte id; the server resolves timing/lunge and
// remotes resolve frames from the same ordered list. LocalPlayer.weapons[] becomes a view onto Weapons.
[CreateAssetMenu(menuName = "Minifantasy/Weapon Catalog", fileName = "WeaponCatalog")]
public sealed class WeaponCatalog : ScriptableObject
{
    public AttackDefinition[] weapons;

    public AttackDefinition Get(byte id) => weapons != null && id < weapons.Length ? weapons[id] : null;

    public int IndexOf(AttackDefinition def)
    {
        if (weapons == null) return -1;
        for (int i = 0; i < weapons.Length; i++) if (weapons[i] == def) return i;
        return -1;
    }
}
