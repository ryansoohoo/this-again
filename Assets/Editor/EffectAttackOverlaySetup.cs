using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

// Tools > Combat > Assign Attack Effect Overlays. Loads the Minifantasy "Attack Effects" swing overlays (per
// attack motion, in each status effect's color, front+back) at 16 PPU into StatusEffectAsset.attackOverlays, and
// sets each weapon's AttackDefinition.attackMotion. Re-runnable; tweak the maps + re-run to retune.
//
// The pack sheets are laid out 1:1 over the Minifantasy weapon attack grids: uniform 32x32 cells, directional
// rows x frame columns (e.g. SlashM 128x128 = 4 cols x 4 rows = 16 frames, same as the weapon's 4x4 sword sheet).
// We GRID-slice them to 32x32 row-major from the TOP (NOT the importer's auto tight-box slice) so the overlay
// frame index lines up with the weapon's `i = row * columnsPerRow + column`. See AttackView.DriveOverlay.
public static class EffectAttackOverlaySetup
{
    const string Root = "Assets/_Imported/Weapons/Minifantasy_Magic_Weapons_And_Effects_v1.0/Minifantasy_Magic_Weapons_And_Effects_Assets/Standalone Effects/Attack Effects/";
    const string EffectDir = "Assets/_Combat/Effects/";
    const string WeaponDir = "Assets/_Combat/";
    const int CellSize = 32;   // each frame is 32x32 px, matching the weapon attack cell grid
    const float PPU = 16f;

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
        if (!SliceSheet(path)) return System.Array.Empty<Sprite>();
        var sprites = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToList();
        sprites.Sort((a, b) => TrailingInt(a.name).CompareTo(TrailingInt(b.name)));
        return sprites.ToArray();
    }

    // Grid-slice the sheet into uniform 32x32 cells, row-major from the TOP (cell (r,c) at y = h-(r+1)*32), naming
    // each <baseName>_<idx> with idx = r*cols + c so the frame index matches the weapon's row*columnsPerRow+column.
    // Center pivot, 16 PPU, Point filter. Reuses the SpriteDataProviderFactories pattern from EffectFxSpriteSetup.
    // Returns false if the sheet is missing or not divisible by the cell size.
    static bool SliceSheet(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) { Debug.LogWarning($"[AtkFx] missing sheet {path}"); return false; }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Multiple;
        importer.spritePixelsPerUnit = PPU;
        importer.filterMode = FilterMode.Point;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;

        importer.GetSourceTextureWidthAndHeight(out int w, out int h);
        if (w <= 0 || h <= 0) { Debug.LogWarning($"[AtkFx] can't read dims for {path}"); return false; }
        if (w % CellSize != 0 || h % CellSize != 0)
        {
            Debug.LogWarning($"[AtkFx] {System.IO.Path.GetFileName(path)} dims {w}x{h} not divisible by {CellSize}; skipping slice.");
            importer.SaveAndReimport();
            return false;
        }

        int cols = w / CellSize, rows = h / CellSize;
        string baseName = System.IO.Path.GetFileNameWithoutExtension(path);

        var factory = new SpriteDataProviderFactories();
        factory.Init();
        var dp = factory.GetSpriteEditorDataProviderFromObject(importer);
        dp.InitSpriteEditorDataProvider();

        var rects = new List<SpriteRect>(cols * rows);
        int idx = 0;
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            rects.Add(new SpriteRect
            {
                name      = $"{baseName}_{idx}",
                rect      = new Rect(c * CellSize, h - (r + 1) * CellSize, CellSize, CellSize),
                alignment = SpriteAlignment.Center,
                pivot     = new Vector2(0.5f, 0.5f),
                border    = Vector4.zero,
                spriteID  = GUID.Generate(),
            });
            idx++;
        }

        dp.SetSpriteRects(rects.ToArray());

        // Write the name<->fileId table so Unity 6 keeps stable sprite ids across reimport (ISpriteNameFileIdDataProvider).
        var nameIdProvider = dp.GetDataProvider<ISpriteNameFileIdDataProvider>();
        if (nameIdProvider != null)
        {
            var pairs = rects.Select(rc => new SpriteNameFileIdPair(rc.name, rc.spriteID)).ToList();
            nameIdProvider.SetNameFileIdPairs(pairs);
        }

        dp.Apply();
        importer.SaveAndReimport();
        return true;
    }

    static int TrailingInt(string s) { int i = s.Length; while (i > 0 && char.IsDigit(s[i - 1])) i--; return i < s.Length && int.TryParse(s.Substring(i), out var n) ? n : 0; }
}
