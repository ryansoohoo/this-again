using UnityEditor;
using UnityEngine;

// Sets up (or removes) the weapon-shine material on the player rig (Ghost.prefab) for the WeaponBack/WeaponFront
// layers. The project runs the URP 2D Renderer, so the AllIn1 material MUST use the URP-2D shader variant — the
// built-in variant renders sprites brighter (color-space/pipeline mismatch). GLOW_ON is enabled here; AttackView
// drives the per-keyframe _Glow. Tune the color in the AllIn1 material inspector.
//   Tools > Minifantasy > Setup Weapon Shine   — create/fix the material + assign it to the weapon layers
//   Tools > Minifantasy > Remove Weapon Shine  — revert the weapon layers to the plain character material
public static class WeaponShineSetupTool
{
    const string PrefabPath = "Assets/_Prefabs/Ghost.prefab";
    const string MatPath = "Assets/Materials/WeaponShine.mat";
    const string PlainMatPath = "Assets/Materials/CharacterUnlit.mat";
    const string ShaderName = "AllIn1SpriteShader/AllIn1Urp2dRenderer";   // URP 2D Renderer variant (this project)
    static readonly string[] WeaponPaths = { "AttackRig/WeaponBack", "AttackRig/WeaponFront" };

    [MenuItem("Tools/Minifantasy/Setup Weapon Shine")]
    public static void Setup()
    {
        var shader = Shader.Find(ShaderName);
        if (shader == null) { Debug.LogError($"[WeaponShine] shader '{ShaderName}' not found. Aborting."); return; }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        bool created = mat == null;
        if (created) mat = new Material(shader) { name = "WeaponShine" };
        else mat.shader = shader;                                  // fix to the correct URP 2D variant
        mat.EnableKeyword("GLOW_ON");
        mat.EnableKeyword("HSV_ON");
        mat.SetColor("_GlowColor", Color.white);   // small white glow on Hit
        mat.SetFloat("_Glow", 0f);                 // AttackView drives _Glow / _HsvBright / _HsvSaturation per phase
        mat.SetFloat("_GlowGlobal", 1f);
        mat.SetFloat("_HsvShift", 0f);             // neutral hue (the shader default is 180 = hue-inverted!)
        mat.SetFloat("_HsvSaturation", 1f);        // neutral until follow-through
        mat.SetFloat("_HsvBright", 1f);
        if (created) AssetDatabase.CreateAsset(mat, MatPath); else EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();

        int n = AssignWeaponMaterial(mat);
        Debug.Log($"[WeaponShine] material {(created ? "created" : "fixed")} to URP 2D shader; assigned to {n} weapon renderer(s).");
    }

    [MenuItem("Tools/Minifantasy/Remove Weapon Shine")]
    public static void Remove()
    {
        var plain = AssetDatabase.LoadAssetAtPath<Material>(PlainMatPath);
        if (plain == null) { Debug.LogError($"[WeaponShine] plain material '{PlainMatPath}' not found. Aborting."); return; }
        int n = AssignWeaponMaterial(plain);
        Debug.Log($"[WeaponShine] reverted {n} weapon renderer(s) to {plain.name} (no shine; matches the rest of the character).");
    }

    static int AssignWeaponMaterial(Material mat)
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            int assigned = 0;
            foreach (var path in WeaponPaths)
            {
                var t = root.transform.Find(path);
                var sr = t != null ? t.GetComponent<SpriteRenderer>() : null;
                if (sr != null) { sr.sharedMaterial = mat; assigned++; }
                else Debug.LogWarning($"[WeaponShine] '{path}' SpriteRenderer not found on the prefab.");
            }
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            return assigned;
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }
}
