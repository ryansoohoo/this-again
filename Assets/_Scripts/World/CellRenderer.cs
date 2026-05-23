using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Renders a square WINDOW of the infinite world as ONE mesh under a single MeshRenderer, using DUAL-GRID
// autotiling on the BINARY land/water field. The display grid is offset half a cell: each display tile sits
// on a cell CORNER and reads the 4 cells around that corner; the 4-bit land mask (TL=1,TR=2,BL=4,BR=8)
// picks one of 16 tiles. Interior land (case 15) draws a per-cell biome sprite resolved by World (or a
// built-in ground tile when a cell has no biome); the 14 mixed cases are the static coastline; open water
// (case 0) is the water sprite (animated by the water shader; WaterMaterial pushes its params).
// Two submeshes / materials: [0] static summer sheet (ground + coast), [1] water sheet.
public static class CellRenderer
{
    const int Tile = 16;                       // tile size in px (both sheets)
    const int SummerW = 112, SummerH = 320;    // world_map_tiles_SUMMER.png  (7 x 20 tiles)
    const int WaterW = 64, WaterH = 48;        // simple_water_spritesheet.png (4 frames x 3 styles)

    // Dual-grid case -> tile (col,row) in the SUMMER sheet. Index = TL*1 + TR*2 + BL*4 + BR*8 (land bit set;
    // corners in image space, row 0 = top). Derived by auto-classifying the pack's coast tiles' corners.
    // Case 0 (all water) is drawn from the WATER sheet, so its entry here is only a fallback.
    // Cases 1..14 = coastline pieces (from the pack's coast demo, whole-tile analyzed). Case 0 = open water
    // (drawn from the WATER sheet). Case 15 = solid ground: the coast region has NO water-free tile, so the
    // ground fill is a dedicated solid land tile (1,0) color-matched to the coast tiles' land.
    static readonly int[] CaseCol = { 1, 6, 4, 5, 6, 6, 2, 0, 4, 2, 4, 3, 5, 0, 3, 0 };
    static readonly int[] CaseRow = { 13,14,14,14,12,13,14,15,12,12,13,15,12,18,18, 1 };

    // Built-in "normal ground" tile (col,row) used for the all-land case (15) when a cell resolves to no biome
    // sprite (no BiomeTiles asset assigned, or it has no usable variants). This is the blank/default ground.
    const int FallbackGroundCol = 1, FallbackGroundRow = 5;

    // Creates the "Grid" GameObject with two materials: [0] static summer sheet, [1] water sheet.
    public static MeshFilter Build(Transform parent, Texture2D summerTex, Texture2D waterTex)
    {
        var go = new GameObject("Grid", typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);
        var sprite = Shader.Find("Sprites/Default");
        var waves = Shader.Find("Custom/WaterWaves");           // scrolling-noise wave animation (per-tile frame select)
        var mats = new Material[2];
        mats[0] = new Material(sprite) { name = "Terrain", mainTexture = summerTex };
        mats[1] = new Material(waves != null ? waves : sprite) { name = "Water", mainTexture = waterTex };
        go.GetComponent<MeshRenderer>().sharedMaterials = mats;
        return go.GetComponent<MeshFilter>();
    }

    // Half-texel-inset UV rect of a tile in a sheet (avoids bleeding adjacent tiles). Row 0 = TOP (V flipped).
    static void TileUV(int col, int row, int w, int h, out float uMin, out float uMax, out float vMin, out float vMax)
    {
        const float inset = 0.5f;
        uMin = (col * Tile + inset) / w;
        uMax = ((col + 1) * Tile - inset) / w;
        vMax = 1f - (row * Tile + inset) / h;          // top edge  (high V)
        vMin = 1f - ((row + 1) * Tile - inset) / h;    // bottom edge (low V)
    }

    // Half-texel-inset UV rect from a sprite's pixel rect in its sheet (bottom-left origin, no V flip needed).
    static void RectUV(Rect r, int w, int h, out float uMin, out float uMax, out float vMin, out float vMax)
    {
        const float inset = 0.5f;
        uMin = (r.x + inset) / w;
        uMax = (r.x + r.width - inset) / w;
        vMin = (r.y + inset) / h;
        vMax = (r.y + r.height - inset) / h;
    }

    // Window mesh in two passes so the per-cell interior ground lands on CELL CENTERS (where the player stands),
    // while coast/water keep the dual-grid CORNER phase (which already lines up with cell centers). Pass 1 draws
    // each land cell's biome sprite as a cell-centered quad; pass 2 draws the dual-grid corners, skipping the
    // all-land case (15) since pass 1 already filled interiors. Pass-1 ground tris go into the land submesh
    // BEFORE pass-2 coast tris, so the opaque coast art paints over the ground at shorelines (painter's order).
    // Submesh 0 = ground + coast (summer sheet); submesh 1 = open water (case 0, animated). landSpriteAt(cx,cy)
    // gives a cell's biome variant, or null for the built-in ground tile.
    public static Mesh BuildWindowMesh(Func<int, int, bool> isLand, Func<int, int, Sprite> landSpriteAt, Vector2Int center, int radius, float cellWorld)
    {
        int cellSpan = 2 * radius + 3, cornerSpan = 2 * radius + 2;   // cells read by the corner pass; corner quads
        int maxQuads = cellSpan * cellSpan + cornerSpan * cornerSpan; // upper bound (water cells / case 15 don't emit)

        var verts = new List<Vector3>(maxQuads * 4);
        var uvs = new List<Vector2>(maxQuads * 4);
        var colors = new List<Color32>(maxQuads * 4);
        var landTris = new List<int>(maxQuads * 6);                   // pass 1 ground, then pass 2 coast (order == draw order)
        var waterTris = new List<int>(cornerSpan * cornerSpan * 6);
        var white = new Color32(255, 255, 255, 255);
        float cw = cellWorld, half = 0.5f * cw;

        // Emit one quad centered at world (wx,wy), spanning +-half, with the given UV rect, into tris.
        void Quad(float wx, float wy, float uMin, float uMax, float vMin, float vMax, List<int> tris)
        {
            int v = verts.Count;
            verts.Add(new Vector3(wx - half, wy - half, 0f));
            verts.Add(new Vector3(wx + half, wy - half, 0f));
            verts.Add(new Vector3(wx - half, wy + half, 0f));
            verts.Add(new Vector3(wx + half, wy + half, 0f));
            uvs.Add(new Vector2(uMin, vMin)); uvs.Add(new Vector2(uMax, vMin));
            uvs.Add(new Vector2(uMin, vMax)); uvs.Add(new Vector2(uMax, vMax));
            colors.Add(white); colors.Add(white); colors.Add(white); colors.Add(white);
            tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);
            tris.Add(v + 1); tris.Add(v + 2); tris.Add(v + 3);
        }

        // Pass 1: interior ground, one quad per land cell, centered on the cell (== the player's CellCenter).
        for (int cx = center.x - radius - 1; cx <= center.x + radius + 1; cx++)
        for (int cy = center.y - radius - 1; cy <= center.y + radius + 1; cy++)
        {
            if (!isLand(cx, cy)) continue;
            float uMin, uMax, vMin, vMax;
            Sprite s = landSpriteAt(cx, cy);                                                      // per-cell biome variant (or null)
            if (s != null)
                RectUV(s.textureRect, s.texture.width, s.texture.height, out uMin, out uMax, out vMin, out vMax);
            else
                TileUV(FallbackGroundCol, FallbackGroundRow, SummerW, SummerH, out uMin, out uMax, out vMin, out vMax);  // blank ground
            Quad((cx + 0.5f) * cw, (cy + 0.5f) * cw, uMin, uMax, vMin, vMax, landTris);
        }

        // Pass 2: dual-grid coast + open water on cell corners. Corner (i,j) reads cells (i-1..i, j-1..j).
        int dmin = -radius, dmax = radius + 1;          // one extra corner on the high side
        for (int di = dmin; di <= dmax; di++)
        for (int dj = dmin; dj <= dmax; dj++)
        {
            int i = center.x + di, j = center.y + dj;
            bool tl = isLand(i - 1, j);      // top-left  cell (low x, high y)
            bool tr = isLand(i,     j);      // top-right
            bool bl = isLand(i - 1, j - 1);  // bottom-left
            bool br = isLand(i,     j - 1);  // bottom-right
            int c = (tl ? 1 : 0) | (tr ? 2 : 0) | (bl ? 4 : 0) | (br ? 8 : 0);
            if (c == 15) continue;                                                                // interior: drawn by pass 1

            float uMin, uMax, vMin, vMax;
            if (c == 0)
            {
                TileUV(0, 0, WaterW, WaterH, out uMin, out uMax, out vMin, out vMax);             // open water (animated)
                Quad(i * cw, j * cw, uMin, uMax, vMin, vMax, waterTris);
            }
            else
            {
                TileUV(CaseCol[c], CaseRow[c], SummerW, SummerH, out uMin, out uMax, out vMin, out vMax);  // coastline
                Quad(i * cw, j * cw, uMin, uMax, vMin, vMax, landTris);
            }
        }

        var mesh = new Mesh { name = "GridDualGridMesh", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(landTris, 0);
        mesh.SetTriangles(waterTris, 1);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Wide overview minimap: a (2*radius+1)^2 texture centered on the player, fully revealed (no fog). Land
    // cells take their ground-cover biome's color via landColorAt(x,y); water cells are a single flat color
    // (waterColor, matching the water tile). 'at' determines water vs land.
    public static Texture2D BuildOverviewMinimap(Func<int, int, bool> isLand, Func<int, int, Color32> landColorAt,
                                                 Vector2Int center, int radius, Color32 waterColor, float brightness)
    {
        int size = 2 * radius + 1;
        int minX = center.x - radius, minY = center.y - radius;

        var land = new bool[size * size];
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
            land[lx * size + ly] = isLand(minX + lx, minY + ly);

        var px = new Color32[size * size];
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
        {
            Color32 c = land[lx * size + ly] ? landColorAt(minX + lx, minY + ly) : waterColor;
            px[ly * size + lx] = new Color32(
                (byte)Mathf.Min(255f, c.r * brightness),
                (byte)Mathf.Min(255f, c.g * brightness),
                (byte)Mathf.Min(255f, c.b * brightness), 255);
        }
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { name = "MinimapTex", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

}
