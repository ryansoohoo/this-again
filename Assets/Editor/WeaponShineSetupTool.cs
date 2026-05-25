using UnityEditor;
using UnityEngine;

// One-shot: create the shared AllIn1 glow material for the weapon layers and assign it to WeaponBack/WeaponFront
// on Ghost.prefab. The glow keyword (GLOW_ON) is enabled here; AttackView drives the per-renderer _Glow intensity
// during the swing. Tune the glow COLOR/look in the AllIn1 material inspector (Assets/Materials/WeaponShine.mat),
// the peak/curve on the AttackView component. Built-in render pipeline → the default AllIn1 shader.
// Re-runnable. Run via Tools > Minifantasy > Setup Weapon Shine.
public static class WeaponShineSetupTool
{
    const string PrefabPath = "Assets/_Prefabs/Ghost.prefab";
    const string MatPath = "Assets/Materials/WeaponShine.mat";
    const string ShaderName = "AllIn1SpriteShader/AllIn1SpriteShader";

    [MenuItem("Tools/Minifantasy/Setup Weapon Shine")]
    public static void Setup()
    {
        var shader = Shader.Find(ShaderName);
        if (shader == null) { Debug.LogError($"[WeaponShine] shader '{ShaderName}' not found (AllIn1 plugin missing?). Aborting."); return; }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        bool created = mat == null;
        if (created) mat = new Material(shader) { name = "WeaponShine" };
        else mat.shader = shader;
        mat.EnableKeyword("GLOW_ON");
        mat.SetColor("_GlowColor", new Color(1f, 0.95f, 0.7f, 1f));   // warm white; tune in the AllIn1 inspector
        mat.SetFloat("_Glow", 0f);                                    // base 0 — AttackView drives this during the swing
        mat.SetFloat("_GlowGlobal", 1f);
        if (created) AssetDatabase.CreateAsset(mat, MatPath); else EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();

        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            int assigned = 0;
            foreach (var path in new[] { "AttackRig/WeaponBack", "AttackRig/WeaponFront" })
            {
                var t = root.transform.Find(path);
                var sr = t != null ? t.GetComponent<SpriteRenderer>() : null;
                if (sr != null) { sr.sharedMaterial = mat; assigned++; }
                else Debug.LogWarning($"[WeaponShine] '{path}' SpriteRenderer not found on the prefab.");
            }
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log($"[WeaponShine] material {(created ? "created" : "updated")} ({MatPath}); GLOW_ON enabled; assigned to {assigned} weapon renderer(s). Tune _GlowColor in the AllIn1 inspector + shinePeak/curve on AttackView.");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }
}
