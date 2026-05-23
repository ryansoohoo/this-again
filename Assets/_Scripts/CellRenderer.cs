using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Renders a square WINDOW of the infinite world as ONE mesh under a single MeshRenderer. Cells are
// generated on demand from a biome function; only the window around the player is built, so everything
// else stays "unloaded" (camera background). Cells are bucketed into one submesh per biome (its floor
// tile) plus a ground submesh behind the sparse tiles. Data-oriented: flat arrays uploaded per rebuild.
public static class CellRenderer
{
    // Creates the "Grid" GameObject (mesh + one material per biome + a ground material). The mesh is
    // filled later by BuildWindowMesh. Returns the MeshFilter so the caller can swap meshes cheaply.
    public static MeshFilter Build(Transform parent, Texture2D[] biomeTextures)
    {
        int biomeCount = Enum.GetValues(typeof(Biome)).Length;
        var go = new GameObject("Grid", typeof(MeshFilter), typeof(MeshRenderer));
        go.transform.SetParent(parent, false);

        var shader = Shader.Find("Sprites/Default");
        var mats = new Material[biomeCount + 1];
        mats[0] = new Material(shader);                                  // submesh 0: solid ground behind the tiles (vertex color)
        for (int b = 0; b < biomeCount; b++)
            mats[b + 1] = new Material(shader) { mainTexture = TexFor(biomeTextures, b) };
        go.GetComponent<MeshRenderer>().sharedMaterials = mats;
        return go.GetComponent<MeshFilter>();
    }

    // Mesh for the square window of cells [center +- radius] (generated via `at`). World position of
    // cell (cx,cy) is cx*cellWorld (origin-relative; the world is unbounded). Submesh 0 = ground quad
    // per non-water cell; submeshes 1..N = one tile quad per cell on top.
    public static Mesh BuildWindowMesh(Func<int, int, Biome> at, Vector2Int center, int radius,
                                       float cellWorld, Texture2D[] biomeTextures, int waterDepthCells)
    {
        int biomeCount = Enum.GetValues(typeof(Biome)).Length;
        int size = 2 * radius + 1;
        int minX = center.x - radius, minY = center.y - radius;

        var biomes = new Biome[size * size];
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
            biomes[lx * size + ly] = at(minX + lx, minY + ly);

        var dist = LocalWaterDistance(biomes, size);
        int n = size * size, landCount = 0;
        for (int i = 0; i < n; i++) if (biomes[i] != Biome.Water) landCount++;

        int totalV = n * 4 + landCount * 4;
        var verts = new Vector3[totalV];
        var uvs = new Vector2[totalV];
        var colors = new Color32[totalV];
        var tris = new List<int>[biomeCount + 1];
        for (int b = 0; b < biomeCount + 1; b++) tris[b] = new List<int>();
        var white = new Color32(255, 255, 255, 255);

        int v = 0, gv = n * 4;
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
        {
            Biome b = biomes[lx * size + ly];
            int bi = (int)b;
            float x0 = (minX + lx) * cellWorld, y0 = (minY + ly) * cellWorld;
            float x1 = x0 + cellWorld, y1 = y0 + cellWorld;
            verts[v] = new Vector3(x0, y0, 0f);
            verts[v + 1] = new Vector3(x1, y0, 0f);
            verts[v + 2] = new Vector3(x0, y1, 0f);
            verts[v + 3] = new Vector3(x1, y1, 0f);
            uvs[v] = new Vector2(0f, 0f);
            uvs[v + 1] = new Vector2(1f, 0f);
            uvs[v + 2] = new Vector2(0f, 1f);
            uvs[v + 3] = new Vector2(1f, 1f);
            Color32 col = b == Biome.Water
                ? WaterColor(dist[lx * size + ly], waterDepthCells)
                : (TexFor(biomeTextures, bi) != null ? white : Biomes.ColorOf(b));
            colors[v] = colors[v + 1] = colors[v + 2] = colors[v + 3] = col;

            var t = tris[bi + 1];                            // +1: submesh 0 is the ground
            t.Add(v); t.Add(v + 2); t.Add(v + 1);
            t.Add(v + 1); t.Add(v + 2); t.Add(v + 3);

            if (b != Biome.Water)
            {
                verts[gv] = verts[v]; verts[gv + 1] = verts[v + 1];
                verts[gv + 2] = verts[v + 2]; verts[gv + 3] = verts[v + 3];
                uvs[gv] = uvs[gv + 1] = uvs[gv + 2] = uvs[gv + 3] = Vector2.zero;
                Color32 gcol = (Biomes.GroundColors != null && bi < Biomes.GroundColors.Length)
                    ? Biomes.GroundColors[bi] : Biomes.ColorOf(b);
                colors[gv] = colors[gv + 1] = colors[gv + 2] = colors[gv + 3] = gcol;
                var gt = tris[0];
                gt.Add(gv); gt.Add(gv + 2); gt.Add(gv + 1);
                gt.Add(gv + 1); gt.Add(gv + 2); gt.Add(gv + 3);
                gv += 4;
            }
            v += 4;
        }

        var mesh = new Mesh { name = "GridWindowMesh", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.subMeshCount = biomeCount + 1;
        for (int b = 0; b < biomeCount + 1; b++) mesh.SetTriangles(tris[b], b);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Wide overview minimap: a (2*radius+1)^2 texture centered on the player, fully revealed (no fog).
    public static Texture2D BuildOverviewMinimap(Func<int, int, Biome> at, Vector2Int center, int radius,
                                                 int waterDepthCells, float brightness)
    {
        int size = 2 * radius + 1;
        int minX = center.x - radius, minY = center.y - radius;

        var biomes = new Biome[size * size];
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
            biomes[lx * size + ly] = at(minX + lx, minY + ly);

        var dist = LocalWaterDistance(biomes, size);
        var px = new Color32[size * size];
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
        {
            Biome b = biomes[lx * size + ly];
            int bi = (int)b;
            Color32 c = b == Biome.Water
                ? WaterColor(dist[lx * size + ly], waterDepthCells)
                : ((Biomes.GroundColors != null && bi < Biomes.GroundColors.Length)
                    ? Biomes.GroundColors[bi] : Biomes.ColorOf(b));
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

    // Multi-source BFS: distance from each window cell to the nearest land cell (land = 0). Drives the
    // shore->deep water gradient. Approximate at the window edge (out-of-window land isn't counted).
    static int[] LocalWaterDistance(Biome[] biomes, int size)
    {
        var dist = new int[size * size];
        for (int i = 0; i < dist.Length; i++) dist[i] = -1;
        var q = new Queue<int>();
        for (int i = 0; i < dist.Length; i++)
            if (biomes[i] != Biome.Water) { dist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int cur = q.Dequeue(), cx = cur / size, cy = cur % size, nd = dist[cur] + 1;
            if (cx > 0 && dist[cur - size] < 0) { dist[cur - size] = nd; q.Enqueue(cur - size); }
            if (cx < size - 1 && dist[cur + size] < 0) { dist[cur + size] = nd; q.Enqueue(cur + size); }
            if (cy > 0 && dist[cur - 1] < 0) { dist[cur - 1] = nd; q.Enqueue(cur - 1); }
            if (cy < size - 1 && dist[cur + 1] < 0) { dist[cur + 1] = nd; q.Enqueue(cur + 1); }
        }
        return dist;
    }

    // Water is always a flat gradient color (no sprite) -> never use a tile texture for it.
    static Texture2D TexFor(Texture2D[] arr, int i) =>
        (i == (int)Biome.Water || arr == null || i >= arr.Length) ? null : arr[i];

    // distToLand: cells from shore; maxDepth: cells until water is fully "deep".
    static Color32 WaterColor(int distToLand, int maxDepth)
    {
        float t = distToLand < 0 ? 1f : Mathf.Clamp01((distToLand - 1) / (float)Mathf.Max(maxDepth, 1));
        return Color32.Lerp(Biomes.WaterShallow, Biomes.WaterDeep, t);
    }

    // Alpha-weighted average color of a texture's pixels (ignoring transparent areas). GPU blit +
    // readback so the source texture needn't be Read/Write enabled. Derives each biome's ground color.
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
        UnityEngine.Object.Destroy(tmp);

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
