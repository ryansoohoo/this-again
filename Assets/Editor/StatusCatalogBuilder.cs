using UnityEditor;
using UnityEngine;

// Tools > Combat > Build Status Catalog. Creates/updates the per-effect StatusEffectAsset files and assembles
// the shared StatusCatalog in StatusKind order. Re-running resets GAMEPLAY fields to these defaults but leaves
// VISUAL fields (sprites/tint) alone so hand-authored FX survive. Phase 1 ships the original 5 kinds; Bleed/Fire
// (Phase 2) and Fear (Phase 3) extend the Specs[] table below.
public static class StatusCatalogBuilder
{
    const string CatalogPath = "Assets/_Combat/StatusCatalog.asset";
    const string EffectDir = "Assets/_Combat/Effects";

    struct Spec
    {
        public StatusKind kind; public float dur; public bool bMove, bAtk; public float moveScale;
        public float period; public int perTick; public StackPolicy policy; public byte maxStacks; public byte vis;
        public ForcedMoveKind forced; public float forcedScale;
    }

    static Spec[] Specs() => new[]
    {
        new Spec { kind = StatusKind.HitStun,        dur = 0.3f, bMove = true,  bAtk = true,  moveScale = 0f,   policy = StackPolicy.Refresh, maxStacks = 1, vis = 1 },
        new Spec { kind = StatusKind.AttackCooldown, dur = 0f,   bMove = false, bAtk = true,  moveScale = 1f,   policy = StackPolicy.Refresh, maxStacks = 1, vis = 0 },
        new Spec { kind = StatusKind.Poison,         dur = 3f,   bMove = false, bAtk = false, moveScale = 1f,   period = 0.5f, perTick = 5, policy = StackPolicy.Stack,   maxStacks = 5, vis = 2 },
        new Spec { kind = StatusKind.Freeze,         dur = 1.5f, bMove = true,  bAtk = false, moveScale = 0f,   policy = StackPolicy.Refresh, maxStacks = 1, vis = 3 },
        new Spec { kind = StatusKind.Slow,           dur = 2f,   bMove = false, bAtk = false, moveScale = 0.5f, policy = StackPolicy.Refresh, maxStacks = 1, vis = 4 },
        new Spec { kind = StatusKind.Bleed, dur = 4f,   bMove = false, bAtk = false, moveScale = 1f, period = 0.5f, perTick = 3, policy = StackPolicy.Stack,   maxStacks = 5, vis = 5 },
        new Spec { kind = StatusKind.Fire,  dur = 2.5f, bMove = false, bAtk = false, moveScale = 1f, period = 0.4f, perTick = 6, policy = StackPolicy.Refresh, maxStacks = 1, vis = 6 },
    };

    [MenuItem("Tools/Combat/Build Status Catalog")]
    public static void Build()
    {
        if (!AssetDatabase.IsValidFolder(EffectDir)) AssetDatabase.CreateFolder("Assets/_Combat", "Effects");
        var specs = Specs();
        var assets = new StatusEffectAsset[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            var sp = specs[i];
            string path = $"{EffectDir}/{sp.kind}.asset";
            var a = AssetDatabase.LoadAssetAtPath<StatusEffectAsset>(path);
            bool created = a == null;
            if (created) { a = ScriptableObject.CreateInstance<StatusEffectAsset>(); }
            a.kind = sp.kind;
            a.durationSeconds = sp.dur; a.blocksMove = sp.bMove; a.blocksAttack = sp.bAtk; a.moveScale = sp.moveScale;
            a.periodSeconds = sp.period; a.amountPerTick = sp.perTick; a.policy = sp.policy; a.maxStacks = sp.maxStacks;
            a.visualId = sp.vis; a.forcedMove = sp.forced; a.forcedMoveScale = sp.forcedScale <= 0f ? 1f : sp.forcedScale;
            if (created) AssetDatabase.CreateAsset(a, path); else EditorUtility.SetDirty(a);
            assets[i] = a;
        }

        var catalog = AssetDatabase.LoadAssetAtPath<StatusCatalog>(CatalogPath);
        bool catCreated = catalog == null;
        if (catCreated) catalog = ScriptableObject.CreateInstance<StatusCatalog>();
        catalog.maxHp = 100;
        catalog.effects = assets;
        if (catCreated) AssetDatabase.CreateAsset(catalog, CatalogPath); else EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        Debug.Log($"[StatusCatalog] {(catCreated ? "created" : "updated")} with {assets.Length} effects, maxHp={catalog.maxHp}");
    }
}
