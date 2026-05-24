using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

// Editor utility: batch grid-slices every SELECTED texture (and every texture under any selected folder)
// into a uniform 32x32 grid with a CENTER pivot, using the same ISpriteEditorDataProvider path as
// CharacterSliceTool. "Slice only" - it sets Sprite + Multiple (the minimum needed to have sprites at all)
// and the grid rects, but leaves PPU / filter / compression / mipmaps exactly as they were. Run via
// Tools > Sprites > Slice Selection 32x32 after selecting the sheets in the Project window.
public static class SpriteGridSliceTool
{
    const int Cell = 32;   // grid cell size in pixels (Grid By Cell Size); change here to slice other sizes

    [MenuItem("Tools/Sprites/Slice Selection 32x32")]
    public static void SliceSelection()
    {
        var paths = SelectedTexturePaths();
        if (paths.Count == 0)
        {
            Debug.LogWarning("[GridSlice] Nothing to slice. Select one or more textures (or folders) in the Project window first.");
            return;
        }

        int ok = 0, frames = 0;
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Slice Selection 32x32", paths[i], (float)i / paths.Count);
                int n = SliceOne(paths[i]);
                if (n > 0) { ok++; frames += n; }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        AssetDatabase.Refresh();
        Debug.Log($"[GridSlice] Sliced {ok}/{paths.Count} sheet(s), {frames} frame(s) @ {Cell}px, center pivot.");
    }

    // Selected textures, plus every texture under any selected folder; de-duplicated.
    static List<string> SelectedTexturePaths()
    {
        var set = new HashSet<string>();
        foreach (var obj in Selection.objects)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) continue;

            if (AssetDatabase.IsValidFolder(path))
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { path }))
                    set.Add(AssetDatabase.GUIDToAssetPath(guid));
            }
            else if (obj is Texture2D)
            {
                set.Add(path);
            }
        }
        return new List<string>(set);
    }

    static int SliceOne(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) { Debug.LogWarning($"[GridSlice] no texture importer: {assetPath}"); return 0; }

        // Required to slice into multiple sprites; everything else (PPU / filter / compression) is left untouched.
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Multiple;

        importer.GetSourceTextureWidthAndHeight(out int w, out int h);
        if (w <= 0 || h <= 0)
        {
            var t = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (t != null) { w = t.width; h = t.height; }
        }
        if (w <= 0 || h <= 0 || w % Cell != 0 || h % Cell != 0)
        {
            Debug.LogWarning($"[GridSlice] {Path.GetFileName(assetPath)} dims {w}x{h} not divisible by {Cell}; skipped.");
            return 0;
        }

        int cols = w / Cell, rows = h / Cell;
        string baseName = Path.GetFileNameWithoutExtension(assetPath);

        var factory = new SpriteDataProviderFactories();
        factory.Init();
        var dp = factory.GetSpriteEditorDataProviderFromObject(importer);
        dp.InitSpriteEditorDataProvider();

        var rects = new List<SpriteRect>(cols * rows);
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
