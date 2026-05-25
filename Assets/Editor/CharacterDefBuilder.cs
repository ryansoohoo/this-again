using UnityEditor;
using UnityEngine;

// Tools > Combat > Build Character Defs. Creates/updates the shared CharacterDef assets — the Player and the first
// enemy, Goblin — with default stats. Re-running RESETS stats to these defaults, so tune values in the Inspector
// afterward. After building, wire Assets/_Combat/Characters/Player.asset onto the Game component's Player Character
// field (GridManager prefab instance).
public static class CharacterDefBuilder
{
    const string Dir = "Assets/_Combat/Characters";

    struct Spec { public string file; public string name; public Faction faction; public int maxHp; }

    static Spec[] Specs() => new[]
    {
        new Spec { file = "Player", name = "Player", faction = Faction.Player, maxHp = 100 },
        new Spec { file = "Goblin", name = "Goblin", faction = Faction.Enemy,  maxHp = 60  },
    };

    [MenuItem("Tools/Combat/Build Character Defs")]
    public static void Build()
    {
        if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/_Combat", "Characters");
        foreach (var sp in Specs())
        {
            string path = $"{Dir}/{sp.file}.asset";
            var a = AssetDatabase.LoadAssetAtPath<CharacterDef>(path);
            bool created = a == null;
            if (created) a = ScriptableObject.CreateInstance<CharacterDef>();
            a.displayName = sp.name; a.faction = sp.faction; a.maxHp = sp.maxHp;
            if (created) AssetDatabase.CreateAsset(a, path); else EditorUtility.SetDirty(a);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[CharacterDef] built Player (100 HP) + Goblin (60 HP)");
    }
}
