using System;
using UnityEngine;

// Construction inputs for WorldView (top-level for symmetry with WorldConfig).
public sealed class WorldViewConfig
{
    public int viewRadius, meshRebuildStep;
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
    readonly ViewSettings vs;
    readonly float cellWorld;
    readonly MeshFilter gridMesh;
    readonly CameraState cameraState;
    readonly Func<int, int, bool> isLandAt;        // cached method-group delegates: re-passing world.IsLand etc.
    readonly Func<int, int, Sprite> landSpriteAt;  // each rebuild allocates a fresh delegate, and a rebuild fires
    readonly Func<int, int, Color32> landColorAt;  // every cell of movement (minimap) — cache once instead

    public Material WaterMat { get; }                       // submesh-1 material; WaterMaterial pushes settings into it
    public Material TerrainMat { get; }                     // submesh-0 material; DayNightView tints its _Color
    public Texture2D MinimapTexture { get; private set; }
    public Vector2Int ViewCenter { get; private set; }      // minimap center (tracks the player every cell)

    Vector2Int meshCenter;
    bool meshInit;
    Mesh windowMesh;        // persistent: refilled in place each rebuild (no per-rebuild Mesh alloc/Destroy)

    public WorldView(World world, WorldViewConfig cfg, ViewSettings vs, float cellWorld, Transform parent,
                     Texture2D summerSheet, Texture2D waterSheet, CameraState cameraState)
    {
        this.world = world; this.cfg = cfg; this.vs = vs; this.cellWorld = cellWorld; this.cameraState = cameraState;
        isLandAt = world.IsLand; landSpriteAt = world.LandSprite; landColorAt = world.LandColor;
        gridMesh = CellRenderer.Build(parent, summerSheet, waterSheet);
        var gridMr = gridMesh.GetComponent<MeshRenderer>();
        WaterMat = gridMr.sharedMaterials[1];
        TerrainMat = gridMr.sharedMaterials[0];
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
        windowMesh = CellRenderer.FillWindowMesh(windowMesh, isLandAt, landSpriteAt, meshCenter, cfg.viewRadius, cellWorld);
        if (gridMesh.sharedMesh != windowMesh) gridMesh.sharedMesh = windowMesh;

        float size = (2 * cfg.viewRadius + 1) * cellWorld;
        if (cameraState != null) cameraState.Bounds = new Rect((meshCenter.x - cfg.viewRadius) * cellWorld,
                                                              (meshCenter.y - cfg.viewRadius) * cellWorld, size, size);
    }

    void RebuildMinimap()
    {
        MinimapTexture = CellRenderer.FillOverviewMinimap(MinimapTexture, isLandAt, landColorAt, ViewCenter,
                                                          vs.minimapRadius, cfg.waterMinimapColor, cfg.minimapBrightness);
    }

    public Vector2 MinimapWorldCenter => new((ViewCenter.x + 0.5f) * cellWorld, (ViewCenter.y + 0.5f) * cellWorld);
    public Vector2 MinimapWorldExtent => new((vs.minimapRadius.x + 0.5f) * cellWorld, (vs.minimapRadius.y + 0.5f) * cellWorld);
}
