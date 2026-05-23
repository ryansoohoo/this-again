using UnityEngine;

// Construction inputs for WorldView (top-level for symmetry with WorldConfig).
public sealed class WorldViewConfig
{
    public int viewRadius, minimapRadius, meshRebuildStep;
    public Color32 waterMinimapColor;
    public float minimapBrightness;
}

// Streams the visible window: rebuilds the world mesh (radius viewRadius) only every meshRebuildStep cells of
// player movement, and the wider minimap every cell. Sets the camera's pan bounds to the loaded mesh so no
// unloaded (black) area can show. Game ticks Follow(); WorldView calls CellRenderer to build the geometry.
public sealed class WorldView
{
    readonly World world;
    readonly WorldViewConfig cfg;
    readonly float cellWorld;
    readonly MeshFilter gridMesh;
    readonly CameraRig rig;

    public Material WaterMat { get; }                       // submesh-1 material; WaterMaterial pushes settings into it
    public Texture2D MinimapTexture { get; private set; }
    public Vector2Int ViewCenter { get; private set; }      // minimap center (tracks the player every cell)

    Vector2Int meshCenter;
    bool meshInit;

    public WorldView(World world, WorldViewConfig cfg, float cellWorld, Transform parent,
                     Texture2D summerSheet, Texture2D waterSheet, CameraRig rig)
    {
        this.world = world; this.cfg = cfg; this.cellWorld = cellWorld; this.rig = rig;
        gridMesh = CellRenderer.Build(parent, summerSheet, waterSheet);
        WaterMat = gridMesh.GetComponent<MeshRenderer>().sharedMaterials[1];
        ViewCenter = meshCenter = Vector2Int.zero;
        meshInit = true;
        RebuildMesh();
        RebuildMinimap();
    }

    // Clears + rebuilds everything after a live settings change (Game.Regenerate).
    public void Refresh() { RebuildMesh(); RebuildMinimap(); }

    // Follow the player: minimap recenters every cell; the heavier mesh recenters every meshRebuildStep cells.
    public void Follow(Vector2Int c)
    {
        if (c != ViewCenter) { ViewCenter = c; RebuildMinimap(); }
        if (!meshInit || Mathf.Max(Mathf.Abs(c.x - meshCenter.x), Mathf.Abs(c.y - meshCenter.y)) >= cfg.meshRebuildStep)
        {
            meshCenter = c;
            meshInit = true;
            RebuildMesh();
        }
    }

    void RebuildMesh()
    {
        var oldMesh = gridMesh.sharedMesh;
        gridMesh.sharedMesh = CellRenderer.BuildWindowMesh(world.IsLand, world.LandSprite, meshCenter, cfg.viewRadius, cellWorld);
        if (oldMesh != null) Object.Destroy(oldMesh);

        float size = (2 * cfg.viewRadius + 1) * cellWorld;
        if (rig != null) rig.Bounds = new Rect((meshCenter.x - cfg.viewRadius) * cellWorld,
                                               (meshCenter.y - cfg.viewRadius) * cellWorld, size, size);
    }

    void RebuildMinimap()
    {
        var oldTex = MinimapTexture;
        MinimapTexture = CellRenderer.BuildOverviewMinimap(world.IsLand, world.LandColor, ViewCenter,
                                                           cfg.minimapRadius, cfg.waterMinimapColor, cfg.minimapBrightness);
        if (oldTex != null) Object.Destroy(oldTex);
    }

    public Vector2 MinimapWorldCenter => new((ViewCenter.x + 0.5f) * cellWorld, (ViewCenter.y + 0.5f) * cellWorld);
    public float MinimapWorldExtent => (cfg.minimapRadius + 0.5f) * cellWorld;
}
