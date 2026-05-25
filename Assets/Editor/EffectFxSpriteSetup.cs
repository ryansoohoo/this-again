using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

// Tools > Combat > Assign Effect FX Sprites. Populates each StatusEffectAsset's attackOverlay/hit/tick sprite
// arrays + tint from the (already-present) PixeLike2 fx sheets, themed per effect. The sheets are 96x16 px
// single-row strips; slices them into 16x16 cells (6 frames) at 16 PPU (Point filter) so FX render at the
// 16-PPU character/weapon scale. Re-runnable; tweak tints / swap sheets after.
public static class EffectFxSpriteSetup
{
    const string FxRoot = "Assets/_Imported/PixeLike2_AssetPack/fx/";
    const string EffectDir = "Assets/_Combat/Effects/";
    const int CellSize = 16;   // each frame is 16x16 px
    const float PPU = 16f;

    struct Map { public StatusKind kind; public string slash, impact, burn; public Color tint; }

    static Map[] Maps() => new[]
    {
        new Map { kind = StatusKind.Fire,   slash = "themed/fx_fire_slash",  impact = "themed/fx_fire_impact",  burn = "themed/fx_fire_burn",  tint = new Color(1f, 0.55f, 0.15f) },
        new Map { kind = StatusKind.Bleed,  slash = "fx_red_slash",          impact = "fx_red_impact",          burn = "fx_red_burn",          tint = new Color(1f, 0.25f, 0.25f) },
        new Map { kind = StatusKind.Fear,   slash = "fx_purple_slash",       impact = "fx_purple_impact",       burn = "fx_purple_burn",       tint = new Color(0.7f, 0.4f, 1f) },
        new Map { kind = StatusKind.Poison, slash = "fx_green_slash",        impact = "fx_green_impact",        burn = "fx_green_burn",        tint = new Color(0.5f, 1f, 0.4f) },
        new Map { kind = StatusKind.Freeze, slash = "themed/fx_frost_slash", impact = "themed/fx_frost_impact", burn = "themed/fx_frost_burn", tint = new Color(0.6f, 0.8f, 1f) },
        new Map { kind = StatusKind.Slow,   slash = "fx_gray_slash",         impact = "fx_gray_impact",         burn = "fx_gray_burn",         tint = new Color(0.7f, 0.7f, 0.78f) },
    };

    [MenuItem("Tools/Combat/Assign Effect FX Sprites")]
    public static void Assign()
    {
        // Pass 1: slice + reimport every sheet we'll use.
        var allPaths = new HashSet<string>();
        foreach (var m in Maps())
        {
            allPaths.Add($"{FxRoot}{m.slash}.png");
            allPaths.Add($"{FxRoot}{m.impact}.png");
            allPaths.Add($"{FxRoot}{m.burn}.png");
        }
        foreach (var path in allPaths)
            SliceSheet(path);

        // Pass 2: assign sprites to each effect asset.
        foreach (var m in Maps())
        {
            var asset = AssetDatabase.LoadAssetAtPath<StatusEffectAsset>($"{EffectDir}{m.kind}.asset");
            if (asset == null) { Debug.LogWarning($"[EffectFx] no asset for {m.kind}"); continue; }
            asset.attackOverlayFx = LoadFrames($"{FxRoot}{m.slash}.png");
            asset.hitFx           = LoadFrames($"{FxRoot}{m.impact}.png");
            asset.tickFx          = LoadFrames($"{FxRoot}{m.burn}.png");
            asset.tintColor       = m.tint;
            EditorUtility.SetDirty(asset);
            Debug.Log($"[EffectFx] {m.kind}: overlay={asset.attackOverlayFx.Length} hit={asset.hitFx.Length} tick={asset.tickFx.Length}");
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[EffectFx] done assigning effect FX sprites.");
    }

    // Slice the sheet into CellSize x CellSize cells, set PPU=16 + Point filter, then SaveAndReimport.
    static void SliceSheet(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) { Debug.LogWarning($"[EffectFx] missing sheet {path}"); return; }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Multiple;
        importer.spritePixelsPerUnit = PPU;
        importer.filterMode = FilterMode.Point;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;

        importer.GetSourceTextureWidthAndHeight(out int w, out int h);
        if (w <= 0 || h <= 0) { Debug.LogWarning($"[EffectFx] can't read dims for {path}"); return; }
        if (w % CellSize != 0 || h % CellSize != 0)
        {
            Debug.LogWarning($"[EffectFx] {System.IO.Path.GetFileName(path)} dims {w}x{h} not divisible by {CellSize}; skipping slice.");
            importer.SaveAndReimport();
            return;
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
        dp.Apply();
        importer.SaveAndReimport();
    }

    static Sprite[] LoadFrames(string path)
    {
        var sprites = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToList();
        sprites.Sort((a, b) => TrailingInt(a.name).CompareTo(TrailingInt(b.name)));
        return sprites.ToArray();
    }

    static int TrailingInt(string s)
    {
        int i = s.Length; while (i > 0 && char.IsDigit(s[i - 1])) i--;
        return i < s.Length && int.TryParse(s.Substring(i), out var n) ? n : 0;
    }
}
