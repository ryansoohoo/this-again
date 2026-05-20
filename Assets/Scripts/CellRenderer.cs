using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Renders the whole grid as ONE mesh under a single MeshRenderer. Cells are bucketed into one submesh
// per biome, each drawn with that biome's floor-tile texture (per-cell UVs 0..1 = one tile per cell).
// Data-oriented: flat vertex/uv/color arrays + per-biome index lists, uploaded once. No per-cell GameObjects.
public static class CellRenderer
{
    // Creates the "Grid" GameObject (mesh + one material per biome). Returns its MeshFilter so the caller
    // can swap in a fresh mesh later (live regenerate) without recreating the object or its materials.
    public static MeshFilter Build(Transform parent, IReadOnlyList<CellSnapshot> cells,
                                   float cellWorld, float halfExtent, Texture2D[] biomeTextures, int waterDepthCells)
    {
        int biomeCount = System.Enum.GetValues(typeof(Biome)).Length;
        var go = new GameObject("Grid", typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);

        var mf = go.GetComponent<MeshFilter>();
        mf.sharedMesh = BuildMesh(cells, cellWorld, halfExtent, biomeTextures, waterDepthCells);

        var shader = Shader.Find("Sprites/Default");
        var mats = new Material[biomeCount + 1];
        mats[0] = new Material(shader);                                  // submesh 0: solid ground behind the tiles (vertex color)
        for (int b = 0; b < biomeCount; b++)
            mats[b + 1] = new Material(shader) { mainTexture = TexFor(biomeTextures, b) };
        go.GetComponent<MeshRenderer>().sharedMaterials = mats;
        return mf;
    }

    // Submesh 0 = one ground quad per non-water cell (that biome tile's darkened average color), drawn
    // first; submeshes 1..N = one quad per cell, each drawing its (sparse) floor tile on top of the ground.
    public static Mesh BuildMesh(IReadOnlyList<CellSnapshot> cells, float cellWorld, float halfExtent,
                                 Texture2D[] biomeTextures, int waterDepthCells)
    {
        int biomeCount = System.Enum.GetValues(typeof(Biome)).Length;
        int n = cells.Count;
        var dist = WaterDistanceField(cells, out int gs);   // each cell's distance to nearest land

        int landCount = 0;
        for (int k = 0; k < n; k++) if (cells[k].Data.Biome != Biome.Water) landCount++;

        int totalV = n * 4 + landCount * 4;                 // detail quads + one ground quad per non-water cell
        var verts = new Vector3[totalV];
        var uvs = new Vector2[totalV];
        var colors = new Color32[totalV];
        var tris = new List<int>[biomeCount + 1];           // submesh 0 = ground, 1..biomeCount = biomes
        for (int b = 0; b < biomeCount + 1; b++) tris[b] = new List<int>();
        var white = new Color32(255, 255, 255, 255);

        int gv = n * 4;                                     // running index for ground verts
        for (int k = 0; k < n; k++)
        {
            var snap = cells[k];
            int bi = (int)snap.Data.Biome;
            float x0 = snap.Coord.x * cellWorld - halfExtent, y0 = snap.Coord.y * cellWorld - halfExtent;
            float x1 = x0 + cellWorld, y1 = y0 + cellWorld;
            int v = k * 4;
            verts[v] = new Vector3(x0, y0, 0f);
            verts[v + 1] = new Vector3(x1, y0, 0f);
            verts[v + 2] = new Vector3(x0, y1, 0f);
            verts[v + 3] = new Vector3(x1, y1, 0f);
            uvs[v] = new Vector2(0f, 0f);
            uvs[v + 1] = new Vector2(1f, 0f);
            uvs[v + 2] = new Vector2(0f, 1f);
            uvs[v + 3] = new Vector2(1f, 1f);
            Color32 col = bi == (int)Biome.Water
                ? WaterColor(dist[snap.Coord.x * gs + snap.Coord.y], waterDepthCells)       // shore->deep gradient (in-game)
                : (TexFor(biomeTextures, bi) != null ? white : Biomes.ColorOf(snap.Data.Biome));
            colors[v] = colors[v + 1] = colors[v + 2] = colors[v + 3] = col;

            var t = tris[bi + 1];                            // +1: submesh 0 is the ground
            t.Add(v); t.Add(v + 2); t.Add(v + 1);
            t.Add(v + 1); t.Add(v + 2); t.Add(v + 3);

            // Ground quad behind each non-water tile, in that biome's darkened average color.
            if (bi != (int)Biome.Water)
            {
                verts[gv] = verts[v]; verts[gv + 1] = verts[v + 1];
                verts[gv + 2] = verts[v + 2]; verts[gv + 3] = verts[v + 3];
                uvs[gv] = uvs[gv + 1] = uvs[gv + 2] = uvs[gv + 3] = Vector2.zero;
                Color32 gcol = (Biomes.GroundColors != null && bi < Biomes.GroundColors.Length)
                    ? Biomes.GroundColors[bi] : Biomes.ColorOf(snap.Data.Biome);
                colors[gv] = colors[gv + 1] = colors[gv + 2] = colors[gv + 3] = gcol;
                var gt = tris[0];
                gt.Add(gv); gt.Add(gv + 2); gt.Add(gv + 1);
                gt.Add(gv + 1); gt.Add(gv + 2); gt.Add(gv + 3);
                gv += 4;
            }
        }

        var mesh = new Mesh { name = "GridMesh", indexFormat = IndexFormat.UInt32 };  // 100x100 -> 160k verts
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.subMeshCount = biomeCount + 1;
        for (int b = 0; b < biomeCount + 1; b++) mesh.SetTriangles(tris[b], b);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Builds a gs×gs texture, one pixel per cell, matching the in-game look: land = its darkened
    // average-art ground color; water = the same shore->deep gradient. Drawn by the Minimap HUD.
    public static Texture2D BuildMinimapTexture(IReadOnlyList<CellSnapshot> cells, int waterDepthCells)
    {
        var dist = WaterDistanceField(cells, out int gs);
        var px = new Color32[gs * gs];
        for (int k = 0; k < cells.Count; k++)
        {
            var s = cells[k];
            int bi = (int)s.Data.Biome;
            px[s.Coord.y * gs + s.Coord.x] = s.Data.Biome == Biome.Water
                ? WaterColor(dist[s.Coord.x * gs + s.Coord.y], waterDepthCells)    // same shore->deep gradient as in-game
                : ((Biomes.GroundColors != null && bi < Biomes.GroundColors.Length)
                    ? Biomes.GroundColors[bi]                                        // land: same darkened average ground color
                    : Biomes.ColorOf(s.Data.Biome));
        }
        var tex = new Texture2D(gs, gs, TextureFormat.RGBA32, false)
            { name = "MinimapTex", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels32(px);
        tex.Apply(false, false);
        return tex;
    }

    // Water is always a flat color (no sprite), per design -> never use a tile texture for it.
    static Texture2D TexFor(Texture2D[] arr, int i) =>
        (i == (int)Biome.Water || arr == null || i >= arr.Length) ? null : arr[i];

    // Multi-source BFS: grid distance from each cell to the nearest non-water (land) cell.
    // Land cells = 0; water cells get their distance (used for the in-game shore->deep gradient).
    static int[] WaterDistanceField(IReadOnlyList<CellSnapshot> cells, out int gs)
    {
        int n = cells.Count;
        gs = 0;
        for (int k = 0; k < n; k++) { var c = cells[k].Coord; if (c.x > gs) gs = c.x; if (c.y > gs) gs = c.y; }
        gs += 1;

        var dist = new int[gs * gs];
        for (int i = 0; i < dist.Length; i++) dist[i] = -1;
        var q = new Queue<int>();
        for (int k = 0; k < n; k++)
        {
            var s = cells[k];
            if (s.Data.Biome != Biome.Water) { int idx = s.Coord.x * gs + s.Coord.y; dist[idx] = 0; q.Enqueue(idx); }
        }
        while (q.Count > 0)
        {
            int cur = q.Dequeue(), cx = cur / gs, cy = cur % gs, nd = dist[cur] + 1;
            if (cx > 0 && dist[cur - gs] < 0) { dist[cur - gs] = nd; q.Enqueue(cur - gs); }
            if (cx < gs - 1 && dist[cur + gs] < 0) { dist[cur + gs] = nd; q.Enqueue(cur + gs); }
            if (cy > 0 && dist[cur - 1] < 0) { dist[cur - 1] = nd; q.Enqueue(cur - 1); }
            if (cy < gs - 1 && dist[cur + 1] < 0) { dist[cur + 1] = nd; q.Enqueue(cur + 1); }
        }
        return dist;
    }

    // distToLand: cells from shore; maxDepth: cells until water is fully "deep" (bigger = vaster oceans)
    static Color32 WaterColor(int distToLand, int maxDepth)
    {
        float t = distToLand < 0 ? 1f : Mathf.Clamp01((distToLand - 1) / (float)Mathf.Max(maxDepth, 1));
        return Color32.Lerp(Biomes.WaterShallow, Biomes.WaterDeep, t);
    }

    // Alpha-weighted average color of a texture's pixels (the "average color of the art", ignoring
    // transparent areas). Uses a GPU blit + readback so the source texture needn't be Read/Write enabled.
    public static Color32 AverageColor(Texture2D tex)
    {
        if (tex == null) return new Color32(128, 128, 128, 255);
        var prev = RenderTexture.active;                 // capture BEFORE Blit (Blit sets active = rt)
        var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(tex, rt);
        RenderTexture.active = rt;
        var tmp = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false);
        tmp.ReadPixels(new Rect(0, 0, tex.width, tex.height), 0, 0);
        tmp.Apply(false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        var px = tmp.GetPixels32();
        Object.Destroy(tmp);

        double r = 0, g = 0, b = 0, a = 0;
        for (int i = 0; i < px.Length; i++)
        {
            float al = px[i].a;
            r += px[i].r * al; g += px[i].g * al; b += px[i].b * al; a += al;
        }
        if (a <= 0.0) return new Color32(128, 128, 128, 255);   // fully transparent tile -> neutral
        return new Color32((byte)(r / a), (byte)(g / a), (byte)(b / a), 255);
    }
}
