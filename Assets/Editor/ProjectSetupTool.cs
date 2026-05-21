using UnityEditor;
using UnityEngine;

// Editor utility: ensures the sorting layers the player rendering relies on exist.
// Order (back -> front): Default (grid) < Shadow < Entities < Effects, so the player and its
// FX always draw on top of the grid mesh. Run via Tools > Minifantasy > Add Sorting Layers.
public static class ProjectSetupTool
{
    [MenuItem("Tools/Minifantasy/Add Sorting Layers")]
    public static void AddSortingLayers()
    {
        AddSortingLayer("Shadow", 100);
        AddSortingLayer("Entities", 200);
        AddSortingLayer("Effects", 300);
        AssetDatabase.SaveAssets();
        Debug.Log("[ProjectSetupTool] Sorting layers ensured: Shadow, Entities, Effects.");
    }

    static void AddSortingLayer(string layerName, int uniqueId)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (assets == null || assets.Length == 0) { Debug.LogError("[ProjectSetupTool] TagManager not found."); return; }
        var so = new SerializedObject(assets[0]);
        var layers = so.FindProperty("m_SortingLayers");
        if (layers == null) { Debug.LogError("[ProjectSetupTool] m_SortingLayers missing."); return; }

        for (int i = 0; i < layers.arraySize; i++)
        {
            var nm = layers.GetArrayElementAtIndex(i).FindPropertyRelative("name");
            if (nm != null && nm.stringValue == layerName) return;   // already present -> idempotent
        }

        int idx = layers.arraySize;
        layers.InsertArrayElementAtIndex(idx);
        var el = layers.GetArrayElementAtIndex(idx);
        el.FindPropertyRelative("name").stringValue = layerName;
        el.FindPropertyRelative("uniqueID").intValue = uniqueId;
        var locked = el.FindPropertyRelative("locked");
        if (locked != null) locked.boolValue = false;
        so.ApplyModifiedProperties();
    }
}
