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
            def.bodyFrames = WeaponSheet.LoadSprites(bodySheet);
            def.weaponFrontFrames = WeaponSheet.LoadSprites(frontSheet);
            def.weaponBackFrames = WeaponSheet.LoadSprites(backSheet);
            def.columnsPerRow = WeaponSheet.Columns(bodySheet);
            EditorUtility.SetDirty(def);
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Fill directions: Cardinal (E,W,S,N)"))
            SetDirs(def, AttackDirections.Cardinal);
        if (GUILayout.Button("Fill directions: Diagonal (SE,SW,NE,NW)"))
            SetDirs(def, AttackDirections.Diagonal);

        EditorGUILayout.Space();
        if (GUILayout.Button("Validate"))
            Validate(def);
    }

    static void SetDirs(AttackDefinition def, Vector2[] dirs)
    {
        Undo.RecordObject(def, "Fill directions");
        def.directions = AttackDirections.Entries(dirs);
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
