using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Tools > Combat > Assign Attack Effect Overlays. Loads the Minifantasy "Attack Effects" swing overlays (per
// attack motion, in each status effect's color, front+back) at 16 PPU into StatusEffectAsset.attackOverlays, and
// sets each weapon's AttackDefinition.attackMotion. Re-runnable; tweak the maps + re-run to retune.
public static class EffectAttackOverlaySetup
{
    const string Root = "Assets/_Imported/Weapons/Minifantasy_Magic_Weapons_And_Effects_v1.0/Minifantasy_Magic_Weapons_And_Effects_Assets/Standalone Effects/Attack Effects/";
    const string EffectDir = "Assets/_Combat/Effects/";
    const string WeaponDir = "Assets/_Combat/";

    static readonly (StatusKind kind, string color)[] Colors =
    {
        (StatusKind.Fire, "fire"), (StatusKind.Freeze, "ice"), (StatusKind.Bleed, "bleeding"),
        (StatusKind.Fear, "fear"), (StatusKind.Poison, "poisson"), (StatusKind.Slow, "sickness"),
    };

    static readonly (string weapon, AttackMotion motion)[] Motions =
    {
        ("Slash", AttackMotion.SlashM), ("Dagger", AttackMotion.SlashM),
        ("Axe", AttackMotion.SlashL), ("Longsword", AttackMotion.SlashL), ("Waraxe", AttackMotion.SlashL),
        ("Spear", AttackMotion.Pierce), ("Pitchfork", AttackMotion.Pierce),
        ("Whip", AttackMotion.Lash), ("Flail", AttackMotion.Flail),
        ("Bow", AttackMotion.Shot), ("Slingshot", AttackMotion.Shot),
    };

    static readonly AttackMotion[] AllMotions = { AttackMotion.SlashL, AttackMotion.SlashM, AttackMotion.Pierce, AttackMotion.Lash, AttackMotion.Flail, AttackMotion.Shot };

    [MenuItem("Tools/Combat/Assign Attack Effect Overlays")]
    public static void Assign()
    {
        foreach (var (kind, color) in Colors)
        {
            var asset = AssetDatabase.LoadAssetAtPath<StatusEffectAsset>($"{EffectDir}{kind}.asset");
            if (asset == null) { Debug.LogWarning($"[AtkFx] no effect asset {kind}"); continue; }
            asset.fxColor = color;
            var list = new List<MotionOverlay>();
            foreach (var m in AllMotions)
            {
                var front = LoadLayer(m, color, true);
                var back  = LoadLayer(m, color, false);
                if (front.Length > 0 || back.Length > 0)
                    list.Add(new MotionOverlay { motion = m, front = front, back = back });
            }
            asset.attackOverlays = list.ToArray();
            EditorUtility.SetDirty(asset);
            Debug.Log($"[AtkFx] {kind} ({color}): " + string.Join(", ", list.Select(o => $"{o.motion} f{o.front.Length}/b{o.back.Length}")));
        }

        foreach (var (weapon, motion) in Motions)
        {
            var def = AssetDatabase.LoadAssetAtPath<AttackDefinition>($"{WeaponDir}{weapon}.asset");
            if (def == null) { Debug.LogWarning($"[AtkFx] no weapon asset {weapon}"); continue; }
            def.attackMotion = motion;
            EditorUtility.SetDirty(def);
            Debug.Log($"[AtkFx] {weapon} -> {motion}");
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[AtkFx] done.");
    }

    // SlashL/SlashM/Shot have Front Layer/Back Layer; Pierce/Lash/Flail are single (front only, no back).
    static Sprite[] LoadLayer(AttackMotion m, string color, bool front)
    {
        string path = m switch
        {
            AttackMotion.SlashL => front ? $"{Root}SlashL/Front Layer/SlashL_{color}_f.png" : $"{Root}SlashL/Back Layer/SlashL_{color}_b.png",
            AttackMotion.SlashM => front ? $"{Root}SlashM/Front Layer/SlashM_{color}_f.png" : $"{Root}SlashM/Back Layer/SlashM_{color}_b.png",
            AttackMotion.Shot   => front ? $"{Root}Shot/Front Layer/Shot_{color}_f.png"     : $"{Root}Shot/Back Layer/Shot_{color}_b.png",
            AttackMotion.Pierce => front ? $"{Root}Pierce/Pierce_{color}.png" : null,
            AttackMotion.Lash   => front ? $"{Root}Lash/Lash_{color}.png" : null,
            AttackMotion.Flail  => front ? $"{Root}Flail/Flail_{color}.png" : null,
            _ => null,
        };
        if (path == null) return System.Array.Empty<Sprite>();
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) { Debug.LogWarning($"[AtkFx] missing sheet {path}"); return System.Array.Empty<Sprite>(); }
        if (importer.spritePixelsPerUnit != 16f || importer.filterMode != FilterMode.Point)
        {
            importer.spritePixelsPerUnit = 16f; importer.filterMode = FilterMode.Point; importer.SaveAndReimport();
        }
        var sprites = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToList();
        sprites.Sort((a, b) => TrailingInt(a.name).CompareTo(TrailingInt(b.name)));
        return sprites.ToArray();
    }

    static int TrailingInt(string s) { int i = s.Length; while (i > 0 && char.IsDigit(s[i - 1])) i--; return i < s.Length && int.TryParse(s.Substring(i), out var n) ? n : 0; }
}
