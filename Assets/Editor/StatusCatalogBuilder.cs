using UnityEditor;
using UnityEngine;

// One-shot: create/refresh the shared StatusCatalog asset with the v1 effects in StatusKind order.
// Run: Tools > Combat > Build Status Catalog. Re-running resets the 5 entries to these defaults (tune in Inspector after).
public static class StatusCatalogBuilder
{
    const string CatalogPath = "Assets/_Combat/StatusCatalog.asset";

    [MenuItem("Tools/Combat/Build Status Catalog")]
    public static void Build()
    {
        var catalog = AssetDatabase.LoadAssetAtPath<StatusCatalog>(CatalogPath);
        bool created = catalog == null;
        if (created) catalog = ScriptableObject.CreateInstance<StatusCatalog>();
        catalog.maxHp = 100;
        catalog.entries = new[]
        {
            new StatusCatalog.Entry { name = "HitStun",        durationSeconds = 0.3f, blocksMove = true,  blocksAttack = true,  moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 1 },
            new StatusCatalog.Entry { name = "AttackCooldown", durationSeconds = 0f,   blocksMove = false, blocksAttack = true,  moveScale = 1f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 0 },
            new StatusCatalog.Entry { name = "Poison",         durationSeconds = 3f,   blocksMove = false, blocksAttack = false, moveScale = 1f, periodSeconds = 0.5f, amountPerTick = 5, policy = StackPolicy.Stack, maxStacks = 5, visualId = 2 },
            new StatusCatalog.Entry { name = "Freeze",         durationSeconds = 1.5f, blocksMove = true,  blocksAttack = false, moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 3 },
            new StatusCatalog.Entry { name = "Slow",           durationSeconds = 2f,   blocksMove = false, blocksAttack = false, moveScale = 0.5f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 4 },
        };
        if (created) AssetDatabase.CreateAsset(catalog, CatalogPath); else EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        Debug.Log($"[StatusCatalog] {(created ? "created" : "updated")} with {catalog.entries.Length} effects, maxHp={catalog.maxHp}");
    }
}
