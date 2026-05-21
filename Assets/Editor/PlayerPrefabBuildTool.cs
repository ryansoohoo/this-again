using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEngine;

// Editor utility: assembles Assets/Prefabs/Player.prefab from authored parts (sliced sprites +
// Player.controller + PlayerController). Layered children render the character on top of the grid:
//   Shadow (Shadow layer) < Body (Entities, drives anim) < Weapon (Entities +10) < Effects (Effects).
// Weapon/Effects are empty seams for weapons-on-top and shader FX. Sprites use an UNLIT material
// (Sprites/Default, same as the grid) so they render fullbright regardless of the scene's Light 2D
// target layers. The root is scaled so the 32px@16PPU sprite (=2 cells) fits ~one grid cell.
// Run via Tools > Minifantasy > Build Player Prefab.
public static class PlayerPrefabBuildTool
{
    const string Base = "Assets/Minifantasy_Dungeon_v2.3_Commercial_Version/Minifantasy_Dungeon_v2.3_Commercial_Version/Minifantasy_Dungeon_Assets/Characters/Human/";
    const string PrefabPath = "Assets/Prefabs/Player.prefab";
    const string MatPath = "Assets/Materials/CharacterUnlit.mat";
    const float PlayerScale = 1f;       // 32px @ 16 PPU = 2 world units; x1 -> ~2 cells (visible char ~1 cell)

    [MenuItem("Tools/Minifantasy/Build Player Prefab")]
    public static void Build()
    {
        var mat = GetUnlitMaterial();
        var root = new GameObject("Player");
        root.transform.localScale = Vector3.one * PlayerScale;

        var anim = root.AddComponent<Animator>();
        anim.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Animations/Player.controller");
        anim.applyRootMotion = false;

        MakeChild(root, "Shadow", LoadFrame(Base + "_Shadows/Idle.png", 0), "Shadow", 0, mat);
        var body = MakeChild(root, "Body", LoadFrame(Base + "Idle.png", 0), "Entities", 0, mat);
        MakeChild(root, "Weapon", null, "Entities", 10, mat);
        MakeChild(root, "Effects", null, "Effects", 0, mat);

        root.AddComponent<NetworkObject>();

        var nt = root.AddComponent<NetworkTransform>();
        nt.SyncPositionX = true; nt.SyncPositionY = true; nt.SyncPositionZ = false;
        nt.SyncRotAngleX = false; nt.SyncRotAngleY = false; nt.SyncRotAngleZ = false;
        nt.SyncScaleX = false; nt.SyncScaleY = false; nt.SyncScaleZ = false;
        nt.InLocalSpace = false;
        nt.Interpolate = true;

        var pc = root.AddComponent<PlayerController>();
        var so = new SerializedObject(pc);
        so.FindProperty("animator").objectReferenceValue = anim;
        so.ApplyModifiedProperties();

        if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
        bool hasBodySprite = body.GetComponent<SpriteRenderer>().sprite != null;
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();

        Debug.Log(prefab != null
            ? $"[PlayerPrefab] built at {PrefabPath} (scale {PlayerScale}, Body sprite: {hasBodySprite}, unlit mat: {mat != null})"
            : "[PlayerPrefab] FAILED to save prefab.");
    }

    static GameObject MakeChild(GameObject parent, string name, Sprite sprite, string sortingLayer, int order, Material mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingLayerName = sortingLayer;
        sr.sortingOrder = order;
        if (mat != null) sr.sharedMaterial = mat;
        return go;
    }

    static Material GetUnlitMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (mat == null)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Materials")) AssetDatabase.CreateFolder("Assets", "Materials");
            mat = new Material(Shader.Find("Sprites/Default"));
            AssetDatabase.CreateAsset(mat, MatPath);
        }
        return mat;
    }

    static Sprite LoadFrame(string sheetPath, int index)
    {
        var all = AssetDatabase.LoadAllAssetsAtPath(sheetPath);
        foreach (var o in all)
            if (o is Sprite s && FrameIndex(s.name) == index) return s;
        return null;
    }

    static int FrameIndex(string spriteName)
    {
        int u = spriteName.LastIndexOf('_');
        if (u >= 0 && int.TryParse(spriteName.Substring(u + 1), out int v)) return v;
        return 0;
    }
}
