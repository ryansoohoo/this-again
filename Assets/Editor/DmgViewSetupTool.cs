using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// One-shot: wire the directional hurt animation onto the player rig (Ghost.prefab). Adds a "DmgBody" child
// SpriteRenderer (draw settings copied from Body) + a DmgView on the root, and assigns the 16 sliced Dmg frames
// (Dmg_0..15 == SE,SW,NE,NW x 4, row-major). Re-runnable. Run via Tools > Minifantasy > Setup Dmg View.
public static class DmgViewSetupTool
{
    const string PrefabPath = "Assets/_Prefabs/Ghost.prefab";
    const string DmgSheet = "Assets/_Imported/Minifantasy_Dungeon_v2.3_Commercial_Version/Minifantasy_Dungeon_v2.3_Commercial_Version/Minifantasy_Dungeon_Assets/Characters/Human/Dmg.png";

    [MenuItem("Tools/Minifantasy/Setup Dmg View")]
    public static void Setup()
    {
        var frames = LoadFrames(DmgSheet);
        if (frames.Length != 16) { Debug.LogError($"[DmgView] expected 16 Dmg sprites at {DmgSheet}, found {frames.Length}. Aborting."); return; }

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var body = root.transform.Find("Body");
            if (body == null) { Debug.LogError("[DmgView] Ghost.prefab has no 'Body' child. Aborting."); return; }
            var bodySr = body.GetComponent<SpriteRenderer>();

            // DmgBody child (sibling of Body), draw settings + transform copied from Body.
            var dmgTf = root.transform.Find("DmgBody");
            if (dmgTf == null) { var go = new GameObject("DmgBody"); dmgTf = go.transform; dmgTf.SetParent(root.transform, false); }
            dmgTf.localPosition = body.localPosition;
            dmgTf.localRotation = body.localRotation;
            dmgTf.localScale = body.localScale;
            var dmgSr = dmgTf.GetComponent<SpriteRenderer>();
            if (dmgSr == null) dmgSr = dmgTf.gameObject.AddComponent<SpriteRenderer>();
            if (bodySr != null)
            {
                dmgSr.sortingLayerID = bodySr.sortingLayerID;
                dmgSr.sortingOrder = bodySr.sortingOrder;
                dmgSr.sharedMaterial = bodySr.sharedMaterial;
                dmgSr.maskInteraction = bodySr.maskInteraction;
            }
            dmgSr.sprite = frames[0];
            dmgSr.enabled = false;

            // DmgView on the root, wired up.
            var view = root.GetComponent<DmgView>();
            if (view == null) view = root.AddComponent<DmgView>();
            var so = new SerializedObject(view);
            so.FindProperty("playerView").objectReferenceValue = root.GetComponent<PlayerView>();
            so.FindProperty("animator").objectReferenceValue = root.GetComponent<Animator>();
            so.FindProperty("dmgRenderer").objectReferenceValue = dmgSr;
            so.FindProperty("columns").intValue = 4;
            var fp = so.FindProperty("frames");
            fp.arraySize = frames.Length;
            for (int i = 0; i < frames.Length; i++) fp.GetArrayElementAtIndex(i).objectReferenceValue = frames[i];
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[DmgView] wired DmgView + DmgBody on {PrefabPath}: {frames.Length} frames, sortingOrder {dmgSr.sortingOrder}.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    static Sprite[] LoadFrames(string sheetPath)
    {
        var all = AssetDatabase.LoadAllAssetsAtPath(sheetPath);
        var sprites = new List<Sprite>();
        foreach (var o in all) if (o is Sprite s) sprites.Add(s);
        sprites.Sort((a, b) => Idx(a.name).CompareTo(Idx(b.name)));
        return sprites.ToArray();
    }

    static int Idx(string n)
    {
        int u = n.LastIndexOf('_');
        return (u >= 0 && int.TryParse(n.Substring(u + 1), out int v)) ? v : 0;
    }
}
