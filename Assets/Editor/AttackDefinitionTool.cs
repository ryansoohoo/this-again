using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Inspector helpers for AttackDefinition: populate the three Sprite[] (row-major) + columnsPerRow from assigned
// sliced sheets, fill the 4 cardinal/diagonal direction vectors, and validate columns vs the phase lists.
[CustomEditor(typeof(AttackDefinition))]
public sealed class AttackDefinitionTool : Editor
{
    Texture2D bodySheet, frontSheet, backSheet;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var def = (AttackDefinition)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Populate frames from sliced sheets", EditorStyles.boldLabel);
        bodySheet = (Texture2D)EditorGUILayout.ObjectField("Body sheet", bodySheet, typeof(Texture2D), false);
        frontSheet = (Texture2D)EditorGUILayout.ObjectField("Weapon front (_f)", frontSheet, typeof(Texture2D), false);
        backSheet = (Texture2D)EditorGUILayout.ObjectField("Weapon back (_b)", backSheet, typeof(Texture2D), false);

        if (GUILayout.Button("Populate Sprite[] + columnsPerRow"))
        {
            Undo.RecordObject(def, "Populate attack frames");
            def.bodyFrames = LoadSprites(bodySheet);
            def.weaponFrontFrames = LoadSprites(frontSheet);
            def.weaponBackFrames = LoadSprites(backSheet);
            def.columnsPerRow = ColumnsOf(bodySheet);
            EditorUtility.SetDirty(def);
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Fill directions: Cardinal (E,N,W,S)"))
            SetDirs(def, new[] { new Vector2(1, 0), new Vector2(0, 1), new Vector2(-1, 0), new Vector2(0, -1) });
        if (GUILayout.Button("Fill directions: Diagonal (NE,NW,SW,SE)"))
            SetDirs(def, new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(-1, -1), new Vector2(1, -1) });

        EditorGUILayout.Space();
        if (GUILayout.Button("Validate"))
            Validate(def);
    }

    static Sprite[] LoadSprites(Texture2D tex)
    {
        if (tex == null) return new Sprite[0];
        string path = AssetDatabase.GetAssetPath(tex);
        var subs = AssetDatabase.LoadAllAssetsAtPath(path);
        var list = new List<Sprite>();
        foreach (var o in subs) if (o is Sprite s) list.Add(s);
        list.Sort((a, b) => SliceIndex(a.name).CompareTo(SliceIndex(b.name))); // SpriteGridSliceTool names "{base}_{idx}"
        return list.ToArray();
    }

    static int SliceIndex(string name)
    {
        int u = name.LastIndexOf('_');
        return (u >= 0 && int.TryParse(name.Substring(u + 1), out int n)) ? n : 0;
    }

    static int ColumnsOf(Texture2D tex) => tex == null ? 4 : Mathf.Max(1, tex.width / 32);

    static void SetDirs(AttackDefinition def, Vector2[] dirs)
    {
        Undo.RecordObject(def, "Fill directions");
        def.directions = new DirectionEntry[dirs.Length];
        for (int i = 0; i < dirs.Length; i++) def.directions[i] = new DirectionEntry { canonicalDir = dirs[i], row = i };
        EditorUtility.SetDirty(def);
        Debug.Log("[AttackDef] filled directions; confirm each entry's row matches the sheet.");
    }

    static void Validate(AttackDefinition def)
    {
        int rows = def.directions != null ? def.directions.Length : 0;
        int expected = def.columnsPerRow * rows;
        Check(def.bodyFrames != null && def.bodyFrames.Length == expected, $"bodyFrames len {def.bodyFrames?.Length} != {expected}");
        Check(def.weaponFrontFrames == null || def.weaponFrontFrames.Length == 0 || def.weaponFrontFrames.Length == expected, "weaponFrontFrames length mismatch");
        Check(def.weaponBackFrames == null || def.weaponBackFrames.Length == 0 || def.weaponBackFrames.Length == expected, "weaponBackFrames length mismatch");
        ValidateCols(def.anticipation, def.columnsPerRow, "anticipation");
        ValidateCols(def.tapAnticipation, def.columnsPerRow, "tapAnticipation");
        ValidateCols(def.hit, def.columnsPerRow, "hit");
        ValidateCols(def.followThrough, def.columnsPerRow, "followThrough");
        Debug.Log("[AttackDef] validation complete.");
    }

    static void ValidateCols(TimedFrame[] list, int cols, string name)
    {
        if (list == null) return;
        for (int i = 0; i < list.Length; i++)
            Check(list[i].column >= 0 && list[i].column < cols, $"{name}[{i}].column {list[i].column} out of range 0..{cols - 1}");
    }

    static void Check(bool ok, string msg) { if (!ok) Debug.LogError("[AttackDef] " + msg); }
}
