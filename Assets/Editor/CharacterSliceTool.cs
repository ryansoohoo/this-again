using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

// Editor utility: re-slices the Minifantasy Human sheets into a uniform 32x32 grid with a
// CENTER pivot, Point filter, no compression, and PPU 16 (so the art's pixel density matches
// the 16-PPU world tiles). The asset shipped tight-cropped (~9px rects), which makes animation
// jitter; uniform cells fix that. Run via Tools > Minifantasy > Slice Human Sheets.
public static class CharacterSliceTool
{
    const string HumanDir = "Assets/Minifantasy_Dungeon_v2.3_Commercial_Version/Minifantasy_Dungeon_v2.3_Commercial_Version/Minifantasy_Dungeon_Assets/Characters/Human";
    const int Cell = 32;
    const float PPU = 16f;

    [MenuItem("Tools/Minifantasy/Slice Human Sheets")]
    public static void SliceHuman()
    {
        var paths = new List<string>();
        CollectPng(HumanDir, paths);
        CollectPng(HumanDir + "/_Shadows", paths);

        int ok = 0, total = 0, frames = 0;
        foreach (var p in paths)
        {
            total++;
            int n = SliceOne(p);
            if (n > 0) { ok++; frames += n; }
        }
        AssetDatabase.Refresh();
        Debug.Log($"[CharacterSliceTool] Sliced {ok}/{total} sheets, {frames} frames @ {Cell}px, PPU {PPU}, center pivot.");
    }

    static void CollectPng(string dir, List<string> outList)
    {
        if (!Directory.Exists(dir)) { Debug.LogWarning($"[Slice] missing dir: {dir}"); return; }
        foreach (var f in Directory.GetFiles(dir, "*.png", SearchOption.TopDirectoryOnly))
            outList.Add(f.Replace('\\', '/'));
    }

    static int SliceOne(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) { Debug.LogWarning($"[Slice] no importer: {assetPath}"); return 0; }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Multiple;
        importer.spritePixelsPerUnit = PPU;
        importer.filterMode = FilterMode.Point;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.isReadable = false;

        int w, h;
        importer.GetSourceTextureWidthAndHeight(out w, out h);
        if (w <= 0 || h <= 0)
        {
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (t != null) { w = t.width; h = t.height; }
        }
        if (w <= 0 || h <= 0 || w % Cell != 0 || h % Cell != 0)
        {
            Debug.LogWarning($"[Slice] {Path.GetFileName(assetPath)} bad dims {w}x{h}; skipped.");
            return 0;
        }

        int cols = w / Cell, rows = h / Cell;
        string baseName = Path.GetFileNameWithoutExtension(assetPath);

        var factory = new SpriteDataProviderFactories();
        factory.Init();
        var dp = factory.GetSpriteEditorDataProviderFromObject(importer);
        dp.InitSpriteEditorDataProvider();

        var rects = new List<SpriteRect>();
        int idx = 0;
        for (int r = 0; r < rows; r++)          // r = 0 is the TOP row, so frame 0 = top-left (matches Unity grid order)
        for (int c = 0; c < cols; c++)
        {
            rects.Add(new SpriteRect
            {
                name = $"{baseName}_{idx}",
                rect = new Rect(c * Cell, h - (r + 1) * Cell, Cell, Cell),
                alignment = SpriteAlignment.Center,
                pivot = new Vector2(0.5f, 0.5f),
                border = Vector4.zero,
                spriteID = GUID.Generate(),
            });
            idx++;
        }
        dp.SetSpriteRects(rects.ToArray());
        dp.Apply();
        importer.SaveAndReimport();
        return rects.Count;
    }
}
