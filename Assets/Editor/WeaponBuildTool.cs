using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

// Batch-builds AttackDefinition assets for every weapon from a config table: slices each sheet (32px grid,
// 16 PPU, point filter), populates the frame arrays, and sets phases/directions/curve. Idempotent — already
// sliced sheets are left alone (so it won't reshuffle Sword's sprite IDs). Run: Tools > Combat > Build All Weapons.
public static class WeaponBuildTool
{
    const string OutFolder = "Assets/_Combat";
    const string Root = "Assets/_Imported/Minifantasy_Weapons_v3.0/Minifantasy_Weapons_Assets";

    enum Conv { Diagonal, Cardinal }

    struct Cfg { public string id, body, front, back; public Conv conv; public bool rotate; }

    static List<Cfg> Configs() => new List<Cfg>
    {
        // Slash (diagonal, 4 cols) — Sword already authored; Axe/Dagger share the Slash body sheet.
        new Cfg{ id="Axe",    body="Slash_Attacks/_Characters/Slash_Character_human.png", front="Slash_Attacks/Axe/Slash_axe_f.png",       back="Slash_Attacks/Axe/Slash_axe_b.png",       conv=Conv.Diagonal, rotate=false },
        new Cfg{ id="Dagger", body="Slash_Attacks/_Characters/Slash_Character_human.png", front="Slash_Attacks/Dagger/Slash_dagger_f.png", back="Slash_Attacks/Dagger/Slash_dagger_b.png", conv=Conv.Diagonal, rotate=false },
        // Thrust (cardinal, 3 cols) — rotates to the cursor (8-way).
        new Cfg{ id="Spear",     body="Thrust_Attacks/_Characters/Thrust_Characters_human.png", front="Thrust_Attacks/Spear/Thrust_spear_f.png",         back="Thrust_Attacks/Spear/Thrust_spear_b.png",         conv=Conv.Cardinal, rotate=true },
        new Cfg{ id="Pitchfork", body="Thrust_Attacks/_Characters/Thrust_Characters_human.png", front="Thrust_Attacks/Pitchfork/Thrust_pitchfork_f.png", back="Thrust_Attacks/Pitchfork/Thrust_pitchfork_b.png", conv=Conv.Cardinal, rotate=true },
        // Swing (cardinal, 3 cols) — single weapon sheet, no back layer; rotates the cardinal swing to the cursor (8-way, incl. diagonals).
        new Cfg{ id="Flail", body="Swing_Attacks/_Characters/Swing_Characters_human.png", front="Swing_Attacks/Swing_flail.png", back=null, conv=Conv.Cardinal, rotate=true },
        new Cfg{ id="Whip",  body="Swing_Attacks/_Characters/Swing_Characters_human.png", front="Swing_Attacks/Swing_whip.png",  back=null, conv=Conv.Cardinal, rotate=true },
        // Two-Handed (diagonal, 6 cols).
        new Cfg{ id="Longsword", body="Two_Handed_Attacks/_Characters/TwoHanded_Characters_human.png", front="Two_Handed_Attacks/Longsword/TwoHanded_longsword_f.png", back="Two_Handed_Attacks/Longsword/TwoHanded_longsword_b.png", conv=Conv.Diagonal, rotate=false },
        new Cfg{ id="Waraxe",    body="Two_Handed_Attacks/_Characters/TwoHanded_Characters_human.png", front="Two_Handed_Attacks/Waraxe/TwoHanded_waraxe_f.png",       back="Two_Handed_Attacks/Waraxe/TwoHanded_waraxe_b.png",       conv=Conv.Diagonal, rotate=false },
        // Ranged (cardinal, 8 cols) — draw animation only (projectile deferred); rotates to the cursor.
        new Cfg{ id="Bow",       body="Ranged_Attacks/_Characters/Shot_Characters_human.png", front="Ranged_Attacks/Bow/Shot_bow_f.png",             back="Ranged_Attacks/Bow/Shot_bow_b.png",             conv=Conv.Cardinal, rotate=true },
        new Cfg{ id="Slingshot", body="Ranged_Attacks/_Characters/Shot_Characters_human.png", front="Ranged_Attacks/Slingshot/Shot_slingshot_f.png", back="Ranged_Attacks/Slingshot/Shot_slingshot_b.png", conv=Conv.Cardinal, rotate=true },
    };

    [MenuItem("Tools/Combat/Build All Weapons")]
    public static void BuildAll()
    {
        if (!AssetDatabase.IsValidFolder(OutFolder)) AssetDatabase.CreateFolder("Assets", "_Combat");

        var configs = Configs();
        // Slice every unique sheet first (skips already-sliced).
        var sheets = new HashSet<string>();
        foreach (var c in configs) { sheets.Add(Abs(c.body)); sheets.Add(Abs(c.front)); if (!string.IsNullOrEmpty(c.back)) sheets.Add(Abs(c.back)); }
        foreach (var s in sheets) Slice(s);
        AssetDatabase.Refresh();

        int n = 0;
        foreach (var c in configs) { BuildOne(c); n++; }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[WeaponBuild] built {n} weapon definitions in {OutFolder} (Sword left as-is).");
    }

    static string Abs(string rel) => $"{Root}/{rel}";

    static void BuildOne(Cfg c)
    {
        int cols = WeaponSheet.Columns(Abs(c.body));
        var def = ScriptableObject.CreateInstance<AttackDefinition>();
        def.bodyFrames = WeaponSheet.LoadSprites(Abs(c.body));
        def.weaponFrontFrames = WeaponSheet.LoadSprites(Abs(c.front));
        def.weaponBackFrames = string.IsNullOrEmpty(c.back) ? new Sprite[0] : WeaponSheet.LoadSprites(Abs(c.back));
        def.columnsPerRow = cols;

        // Generic phase split: first frame = tap; all but last two = anticipation; second-last = hit; last = follow-through.
        var anti = new List<int>();
        for (int i = 0; i <= cols - 3 && i < cols; i++) anti.Add(i);
        if (anti.Count == 0) anti.Add(0);
        def.anticipation = Frames(anti.ToArray(), 0.1f);
        def.tapAnticipation = Frames(new[] { 0 }, 0.08f);
        def.hit = Frames(new[] { Mathf.Max(0, cols - 2) }, 0.1f);
        def.followThrough = Frames(new[] { cols - 1 }, 0.12f);

        def.feintCooldown = 0.5f;
        def.rotateToAim = c.rotate;                  // rotate to cursor (pointing weapons); else snap
        def.aimSnapDegrees = c.rotate ? 45f : 0f;    // rotating weapons snap their aim to 8 directions
        def.lungeCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
        def.directions = Dirs(c.conv);
        def.attackId = c.id.ToLowerInvariant();

        string path = $"{OutFolder}/{c.id}.asset";
        var existing = AssetDatabase.LoadAssetAtPath<AttackDefinition>(path);
        if (existing != null) { EditorUtility.CopySerialized(def, existing); EditorUtility.SetDirty(existing); }  // preserve GUID (keeps weapon refs valid)
        else AssetDatabase.CreateAsset(def, path);
    }

    static TimedFrame[] Frames(int[] cols, float dur)
    {
        var arr = new TimedFrame[cols.Length];
        for (int i = 0; i < cols.Length; i++) arr[i] = new TimedFrame { column = cols[i], duration = dur };
        return arr;
    }

    static DirectionEntry[] Dirs(Conv conv) =>
        AttackDirections.Entries(conv == Conv.Diagonal ? AttackDirections.Diagonal : AttackDirections.Cardinal);

    // Slice a sheet into a 32px grid at 16 PPU (point filter). Skips sheets already sliced at 16 PPU so existing
    // sprite IDs (and the assets that reference them, e.g. Sword) are preserved.
    static void Slice(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) { Debug.LogWarning($"[WeaponBuild] no importer: {assetPath}"); return; }

        if (importer.spriteImportMode == SpriteImportMode.Multiple && importer.spritePixelsPerUnit == WeaponSheet.PPU)
        {
            int count = 0;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(assetPath)) if (o is Sprite) count++;
            if (count > 0) return;   // already sliced — leave its sprite IDs intact
        }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Multiple;
        importer.spritePixelsPerUnit = WeaponSheet.PPU;
        importer.filterMode = FilterMode.Point;
        importer.textureCompression = TextureImporterCompression.Uncompressed;

        importer.GetSourceTextureWidthAndHeight(out int w, out int h);
        if (w <= 0 || h <= 0) { var t = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath); if (t != null) { w = t.width; h = t.height; } }
        int cols = w / WeaponSheet.Cell, rows = h / WeaponSheet.Cell;
        string baseName = Path.GetFileNameWithoutExtension(assetPath);

        var factory = new SpriteDataProviderFactories();
        factory.Init();
        var dp = factory.GetSpriteEditorDataProviderFromObject(importer);
        dp.InitSpriteEditorDataProvider();

        var rects = new List<SpriteRect>(cols * rows);
        int idx = 0;
        for (int r = 0; r < rows; r++)
        for (int col = 0; col < cols; col++)
        {
            rects.Add(new SpriteRect
            {
                name = $"{baseName}_{idx}",
                rect = new Rect(col * WeaponSheet.Cell, h - (r + 1) * WeaponSheet.Cell, WeaponSheet.Cell, WeaponSheet.Cell),
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
    }
}
