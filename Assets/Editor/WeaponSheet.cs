using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Shared weapon sprite-sheet conventions + loaders for the editor authoring tools. One place for the 32px cell
// / 16 PPU constants and the "load all slices, sorted by trailing _index" logic every tool needs.
public static class WeaponSheet
{
    public const int Cell = 32;  // sliced frame size in pixels
    public const int PPU = 16;   // import pixels-per-unit

    public static Sprite[] LoadSprites(string assetPath)
    {
        var list = new List<Sprite>();
        if (!string.IsNullOrEmpty(assetPath))
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                if (o is Sprite s) list.Add(s);
        list.Sort((a, b) => SliceIndex(a.name).CompareTo(SliceIndex(b.name)));
        return list.ToArray();
    }

    public static Sprite[] LoadSprites(Texture2D tex) => tex == null ? new Sprite[0] : LoadSprites(AssetDatabase.GetAssetPath(tex));

    public static int Columns(string assetPath) => Columns(AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath));

    public static int Columns(Texture2D tex) => tex == null ? 4 : Mathf.Max(1, tex.width / Cell);

    // Slices are named "{baseName}_{index}"; sort by that trailing index so frames come back row-major.
    static int SliceIndex(string name)
    {
        int u = name.LastIndexOf('_');
        return (u >= 0 && int.TryParse(name.Substring(u + 1), out int n)) ? n : 0;
    }
}
