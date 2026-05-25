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
    const float TickSeconds = 1.0f;   // every combat effect lasts 1s MIN (one tick), counted PER-EFFECT from its own apply time: a stack == one 1-second tick (a DoT fires flat dmg, then a stack drops). Also drives the per-tick FX animation length. Tune this one knob.

    struct Spec
    {
        public StatusKind kind; public float dur; public bool bMove, bAtk; public float moveScale;
        public float period; public int perTick; public StackPolicy policy; public byte maxStacks; public byte vis;
        public ForcedMoveKind forced; public float forcedScale; public byte vprio;
    }

    static Spec[] Specs() => new[]
    {
        // Timed gates (period 0 => single duration, refresh on re-apply). HitStun is charge-scaled via durationOverride.
        new Spec { kind = StatusKind.HitStun,        dur = 0.3f, bMove = true,  bAtk = true,  moveScale = 0f, maxStacks = 1, vis = 1 },
        new Spec { kind = StatusKind.AttackCooldown, dur = 0f,   bMove = false, bAtk = true,  moveScale = 1f, maxStacks = 1, vis = 0 },
        // Tick-stacked combat effects: period = the global tick; a stack = one pending tick; flat perTick damage; gate live on apply.
        new Spec { kind = StatusKind.Poison, period = TickSeconds, perTick = 3, moveScale = 1f,   maxStacks = 10, vis = 2, vprio = 30 },
        new Spec { kind = StatusKind.Freeze, period = TickSeconds, bMove = true, moveScale = 0f,   maxStacks = 10, vis = 3, vprio = 60 },
        new Spec { kind = StatusKind.Slow,   period = TickSeconds, moveScale = 0.5f, maxStacks = 10, vis = 4, vprio = 20 },
        new Spec { kind = StatusKind.Bleed,  period = TickSeconds, perTick = 4, moveScale = 1f,   maxStacks = 10, vis = 5, vprio = 40 },
        new Spec { kind = StatusKind.Fire,   period = TickSeconds, perTick = 5, moveScale = 1f,   maxStacks = 10, vis = 6, vprio = 50 },
        new Spec { kind = StatusKind.Fear,   period = TickSeconds, bMove = true, bAtk = true, moveScale = 0f, maxStacks = 10, vis = 7, forced = ForcedMoveKind.FleeFrozen, forcedScale = 0.8f, vprio = 70 },
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
            a.visualId = sp.vis; a.visualPriority = sp.vprio; a.forcedMove = sp.forced; a.forcedMoveScale = sp.forcedScale <= 0f ? 1f : sp.forcedScale;
            if (created) AssetDatabase.CreateAsset(a, path); else EditorUtility.SetDirty(a);
            assets[i] = a;
        }

        var catalog = AssetDatabase.LoadAssetAtPath<StatusCatalog>(CatalogPath);
        bool catCreated = catalog == null;
        if (catCreated) catalog = ScriptableObject.CreateInstance<StatusCatalog>();
        catalog.effects = assets;
        if (catCreated) AssetDatabase.CreateAsset(catalog, CatalogPath); else EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        Debug.Log($"[StatusCatalog] {(catCreated ? "created" : "updated")} with {assets.Length} effects");
    }
}
