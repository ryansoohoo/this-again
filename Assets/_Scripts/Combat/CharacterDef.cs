using UnityEngine;

// Shared character data: base combat stats + faction for one KIND of character (the player, or an AI type). Players
// reference one of these (wired on Game); each AI type gets its own. CreateStats() seeds the runtime Stats a
// Combatant resolves from. v1 carries maxHp + faction; damage/defense/moveSpeed/mass slot in later (add a field
// here + a StatKind + a SetBase line). Built/tuned via Tools > Combat > Build Character Defs.
[CreateAssetMenu(menuName = "Minifantasy/Character Def", fileName = "CharacterDef")]
public sealed class CharacterDef : ScriptableObject
{
    [Header("Identity")]
    public string displayName = "Character";
    public Faction faction = Faction.Player;

    [Header("Base stats")]
    public int maxHp = 100;

    // Build the runtime Stats seeded with this def's base values.
    public Stats CreateStats()
    {
        var s = new Stats();
        s.SetBase(StatKind.MaxHp, maxHp);
        return s;
    }
}
