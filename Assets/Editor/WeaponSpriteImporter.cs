using UnityEditor;
using UnityEngine;

// Import pipeline for weapon sprite sheets. Any texture under a weapons source folder imports as a 16 PPU,
// point-filtered, uncompressed, mip-less Sprite — so adding new weapon / element-variant sheets is repeatable
// and scalable with no manual import fixup (this is what the PPU bug needed). Grid-slicing into 32px frames +
// AttackDefinition authoring is done by WeaponBuildTool (the ISpriteEditorDataProvider path; Unity 6 removed
// the legacy TextureImporter.spritesheet slicing API).
public sealed class WeaponSpriteImporter : AssetPostprocessor
{
    // Folders whose textures go through the pipeline. Add element/weapon source folders here to extend it.
    static readonly string[] Roots = { "Minifantasy_Weapons_v3.0" };
    // Sub-paths to leave alone (charged attacks are out of scope).
    static readonly string[] Excludes = { "Charged_Attacks" };
    const string ReimportRoot = "Assets/_Imported/Minifantasy_Weapons_v3.0";

    void OnPreprocessTexture()
    {
        if (!InScope(assetPath)) return;
        var t = (TextureImporter)assetImporter;
        t.textureType = TextureImporterType.Sprite;
        t.spritePixelsPerUnit = WeaponSheet.PPU;
        t.filterMode = FilterMode.Point;
        t.textureCompression = TextureImporterCompression.Uncompressed;
        t.mipmapEnabled = false;
    }

    static bool InScope(string path)
    {
        bool rooted = false;
        foreach (var r in Roots) if (path.Contains(r)) { rooted = true; break; }
        if (!rooted) return false;
        foreach (var e in Excludes) if (path.Contains(e)) return false;
        return true;
    }

    // Re-run the pipeline settings over the existing weapon sprites (does not touch slicing).
    [MenuItem("Tools/Combat/Reimport Weapon Sprites")]
    public static void ReimportAll()
    {
        AssetDatabase.ImportAsset(ReimportRoot, ImportAssetOptions.ImportRecursive);
        Debug.Log("[WeaponSprites] reimported weapon sprites through the import pipeline.");
    }
}
