using UnityEditor;
using UnityEngine;

// Tools > Minifantasy > Setup Effect FX. Re-runnable: adds OverlayFx (in AttackRig), HitFxBody, TickFxBody to
// the player rig prefab and wires AttackView.overlay + StatusFxView.hitRenderer/tickRenderer. The new renderers
// are NOT named like the body layers, so PlayerView's body-toggling ignores them.
public static class EffectFxSetupTool
{
    const string PrefabPath = "Assets/_Prefabs/Ghost.prefab";   // <-- set to the confirmed Ghost.prefab path

    [MenuItem("Tools/Minifantasy/Setup Effect FX")]
    public static void Setup()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var body = FindByName(root.transform, "Body");
            if (body == null) { Debug.LogError("[EffectFx] no Body renderer found"); return; }
            var bodySr = body.GetComponent<SpriteRenderer>();

            var attackView = root.GetComponent<AttackView>();
            var rig = FindByName(root.transform, "AttackRig");
            var overlay = EnsureChild(rig != null ? rig : root.transform, "OverlayFx", bodySr, sortingBump: 2);
            var hit = EnsureChild(root.transform, "HitFxBody", bodySr, sortingBump: 3);
            var tick = EnsureChild(root.transform, "TickFxBody", bodySr, sortingBump: 1);

            var fxView = root.GetComponent<StatusFxView>();
            if (fxView == null) fxView = root.AddComponent<StatusFxView>();

            Wire(attackView, "overlay", overlay);
            Wire(fxView, "hitRenderer", hit);
            Wire(fxView, "tickRenderer", tick);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[EffectFx] prefab wired: OverlayFx, HitFxBody, TickFxBody + StatusFxView");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    static Transform FindByName(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t) { var r = FindByName(c, name); if (r != null) return r; }
        return null;
    }

    static SpriteRenderer EnsureChild(Transform parent, string name, SpriteRenderer copyFrom, int sortingBump)
    {
        var existing = FindByName(parent, name);
        SpriteRenderer sr;
        if (existing != null) sr = existing.GetComponent<SpriteRenderer>();
        else
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            sr = go.AddComponent<SpriteRenderer>();
        }
        if (copyFrom != null)
        {
            sr.sharedMaterial = copyFrom.sharedMaterial;
            sr.sortingLayerID = copyFrom.sortingLayerID;
            sr.sortingOrder = copyFrom.sortingOrder + sortingBump;
        }
        sr.enabled = false;
        return sr;
    }

    static void Wire(Object target, string field, SpriteRenderer value)
    {
        if (target == null) { Debug.LogError($"[EffectFx] missing component for field {field}"); return; }
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p == null) { Debug.LogError($"[EffectFx] no serialized field '{field}' on {target.GetType().Name}"); return; }
        p.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
