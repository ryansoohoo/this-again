using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// One-shot: gather every AttackDefinition in the project into the shared WeaponCatalog asset (the wire's byte id
// <-> def map). Catalog order is just the wire id and is shared by server + clients (one asset), so any stable
// order works; the number-key loadout (LocalPlayer.weapons[]) is separate. Run: Tools > Combat > Build Weapon Catalog.
public static class WeaponCatalogBuilder
{
    const string CatalogPath = "Assets/_Combat/WeaponCatalog.asset";

    [MenuItem("Tools/Combat/Build Weapon Catalog")]
    public static void Build()
    {
        var defs = new List<AttackDefinition>();
        foreach (var guid in AssetDatabase.FindAssets("t:AttackDefinition"))
        {
            var def = AssetDatabase.LoadAssetAtPath<AttackDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if (def != null) defs.Add(def);
        }
        defs.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.Ordinal));   // stable wire-id order

        var catalog = AssetDatabase.LoadAssetAtPath<WeaponCatalog>(CatalogPath);
        bool created = catalog == null;
        if (created) catalog = ScriptableObject.CreateInstance<WeaponCatalog>();
        catalog.weapons = defs.ToArray();
        if (created) AssetDatabase.CreateAsset(catalog, CatalogPath); else EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        Debug.Log($"[WeaponCatalog] {(created ? "created" : "updated")} with {defs.Count} weapons: {string.Join(", ", defs.ConvertAll(d => d.name))}");
    }
}
