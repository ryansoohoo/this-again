using System.Collections.Generic;
using UnityEngine;

// Orchestrator for an INFINITE procedural world. Biome cells are generated on demand from noise and
// cached. The main view renders only a small square VISION window around the local player as one mesh
// (everything else stays "unloaded" = camera background); the minimap shows a wider, always-revealed
// overview centered on the player. The camera is unbounded. Lives in the scene.
[DefaultExecutionOrder(-100)]
public class GridManager : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField] int pixelsPerUnit = 16;
    [SerializeField] int cellSizePixels = 16;

    [Header("Biomes")]
    [SerializeField] BiomeSettings biome = new BiomeSettings();   // all live-tunable noise/water knobs (see BiomeSettings)
    [SerializeField] Texture2D[] biomeTextures;                   // one floor tile per biome, in Biome enum order

    [Header("Camera")]
    [SerializeField] int minCellsVisible = 10;
    [SerializeField] int startCellsVisible = 16;
    [SerializeField] float keyboardPanSpeed = 20f;
    [SerializeField] float recenterDuration = 0.25f;

    [Header("Vision")]
    [SerializeField] int viewRadius = 80;         // cells loaded/visible around the player (main-view mesh + camera bounds)
    [SerializeField] int minimapRadius = 40;      // cells of world the minimap overview shows around the player
    [SerializeField] int meshRebuildStep = 32;    // rebuild the (heavy) world mesh only after the player moves this many cells

    CameraRig rig;

    public static GridManager Instance { get; private set; }
    public Camera Cam { get; private set; }
    public Texture2D MinimapTexture { get; private set; }   // wide overview around the player; drawn by Minimap

    // Live-tunable biome knobs; BiomeTuner mutates these in play, then calls Regenerate.
    public BiomeSettings Biome => biome;
    public float CellWorld => cellWorld;

    // World <-> cell. Cell (cx,cy) spans [cx,cx+1)*cellWorld on each axis; its center sits at +half a cell.
    public Vector2 CellCenter(int cx, int cy) => new((cx + 0.5f) * cellWorld, (cy + 0.5f) * cellWorld);
    public Vector2Int WorldToCell(Vector2 world) => new(Mathf.FloorToInt(world.x / cellWorld), Mathf.FloorToInt(world.y / cellWorld));

    // Movement/pathfinding walkability: every biome can be stepped on except open water.
    // global:: disambiguates the Biome enum from this class's BiomeSettings 'Biome' property.
    public bool IsWalkable(int cx, int cy) => GenAt(cx, cy) != global::Biome.Water;

    // Minimap overview world bounds, for the HUD to map the camera viewport onto it.
    public Vector2 MinimapWorldCenter => CellCenter(viewCenter.x, viewCenter.y);
    public float MinimapWorldExtent => (minimapRadius + 0.5f) * cellWorld;

    float cellWorld;
    MeshFilter gridMesh;
    Color32[] biomeAvg;   // each biome tile's average art color (computed once from biomeTextures)
    BiomeGenerator gen;
    readonly Dictionary<Vector2Int, Biome> cache = new();   // generated biomes; grows as the player explores
    Vector2Int viewCenter;     // minimap center (tracks the player every cell)
    Vector2Int meshCenter;     // world-mesh center (recentered only every meshRebuildStep cells, since the mesh is heavy)
    bool meshInit;

    Biome GenAt(int cx, int cy)
    {
        var key = new Vector2Int(cx, cy);
        if (cache.TryGetValue(key, out var b)) return b;
        b = gen.At(cx, cy);
        cache[key] = b;
        return b;
    }

    void Awake()
    {
        Instance = this;
        QualitySettings.vSyncCount = 0;        // was capping fps at the 60Hz monitor refresh
        Application.targetFrameRate = 120;     // perf target
        Application.runInBackground = true;    // run full speed even when the game view isn't focused
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);  // skip costly stack-trace capture on Debug.Log
        LoadSettings();                        // apply previously-saved biome settings, if any
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[GridManager] No Main Camera."); enabled = false; return; }
        Cam = cam;
        cam.orthographic = true;
        cam.backgroundColor = Color.black;
        cam.clearFlags = CameraClearFlags.SolidColor;
        var p = cam.transform.position;
        cam.transform.position = new Vector3(0f, 0f, p.z >= 0f ? -10f : p.z);

        cellWorld = (float)cellSizePixels / pixelsPerUnit;

        rig = new CameraRig(cam, new CameraRig.Config
        {
            minCellsVisible = minCellsVisible,
            startCellsVisible = startCellsVisible,
            keyboardPanSpeed = keyboardPanSpeed,
            recenterDuration = recenterDuration,
        }, cellWorld);

        gen = new BiomeGenerator(biome);
        ComputeBiomeAverages();
        ApplyPalette();
        gridMesh = CellRenderer.Build(transform, biomeTextures);
        viewCenter = meshCenter = Vector2Int.zero;
        meshInit = true;
        RebuildMesh();
        RebuildMinimap();
        gameObject.AddComponent<BiomeTuner>();
    }

    // Rebuilds the noise generator + clears the cache so a fresh map streams in (called live by BiomeTuner).
    public void Regenerate()
    {
        if (gridMesh == null) return;
        gen = new BiomeGenerator(biome);
        cache.Clear();
        ApplyPalette();
        RebuildMesh();
        RebuildMinimap();
    }

    // The heavy world mesh (radius viewRadius) — rebuilt only every meshRebuildStep cells of movement.
    void RebuildMesh()
    {
        var oldMesh = gridMesh.sharedMesh;
        gridMesh.sharedMesh = CellRenderer.BuildWindowMesh(GenAt, meshCenter, viewRadius, cellWorld, biomeTextures, biome.waterDepthCells);
        if (oldMesh != null) Destroy(oldMesh);

        // Clamp the camera to the loaded mesh so it can't pan into the unloaded (black) area.
        float size = (2 * viewRadius + 1) * cellWorld;
        if (rig != null) rig.Bounds = new Rect((meshCenter.x - viewRadius) * cellWorld, (meshCenter.y - viewRadius) * cellWorld, size, size);
    }

    // The minimap overview (same radius) — recentered on the player every cell so it scrolls smoothly.
    void RebuildMinimap()
    {
        var oldTex = MinimapTexture;
        MinimapTexture = CellRenderer.BuildOverviewMinimap(GenAt, viewCenter, minimapRadius, biome.waterDepthCells, biome.minimapBrightness);
        if (oldTex != null) Destroy(oldTex);
    }

    // Follows the local player: the minimap recenters every cell; the (much heavier) world mesh only
    // recenters once the player has moved meshRebuildStep cells from its center. Centers on the origin
    // before a local player exists (e.g. in the editor without hosting).
    void UpdateView()
    {
        if (gridMesh == null) return;
        var lp = PlayerController.LocalInstance;
        Vector2Int c = lp != null ? lp.CurrentCell() : Vector2Int.zero;
        if (c != viewCenter) { viewCenter = c; RebuildMinimap(); }
        if (!meshInit || Mathf.Max(Mathf.Abs(c.x - meshCenter.x), Mathf.Abs(c.y - meshCenter.y)) >= meshRebuildStep)
        {
            meshCenter = c;
            meshInit = true;
            RebuildMesh();
        }
    }

    // Average art color of each biome's floor tile (computed once); ApplyPalette darkens these into ground.
    void ComputeBiomeAverages()
    {
        int biomeCount = System.Enum.GetValues(typeof(Biome)).Length;
        biomeAvg = new Color32[biomeCount];
        for (int b = 0; b < biomeCount; b++)
        {
            var tex = (biomeTextures != null && b < biomeTextures.Length) ? biomeTextures[b] : null;
            biomeAvg[b] = CellRenderer.AverageColor(tex);
        }
    }

    // Push the live palette (water gradient + per-biome ground = avg tile color x brightness) into the renderer.
    void ApplyPalette()
    {
        Biomes.WaterShallow = Tinted(biome.waterShoreLevel);
        Biomes.WaterDeep = Tinted(biome.waterDeepLevel);
        if (biomeAvg != null)
        {
            if (Biomes.GroundColors == null || Biomes.GroundColors.Length != biomeAvg.Length)
                Biomes.GroundColors = new Color32[biomeAvg.Length];
            float lvl = biome.landBackgroundLevel;
            for (int b = 0; b < biomeAvg.Length; b++)
                Biomes.GroundColors[b] = new Color32((byte)(biomeAvg[b].r * lvl),
                                                     (byte)(biomeAvg[b].g * lvl),
                                                     (byte)(biomeAvg[b].b * lvl), 255);
        }
    }

    Color32 Tinted(float level) => new Color(biome.waterTint.r * level, biome.waterTint.g * level, biome.waterTint.b * level, 1f);

    // ---- Save / load tuned settings (one PlayerPrefs JSON blob; survives play-stop and editor restarts) ----
    const string Pref = "biome.json";

    public void SaveSettings()
    {
        PlayerPrefs.SetString(Pref, JsonUtility.ToJson(biome));
        PlayerPrefs.Save();
        Debug.Log("[GridManager] Biome settings saved.");
    }

    void LoadSettings()
    {
        var json = PlayerPrefs.GetString(Pref, "");
        if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, biome);
    }

    public void ResetSavedSettings()
    {
        PlayerPrefs.DeleteKey(Pref);
        PlayerPrefs.Save();
        Debug.Log("[GridManager] Saved biome settings cleared (inspector defaults apply next play).");
    }

    void Update()
    {
        if (rig == null) return;   // e.g. after an edit-during-play domain reload (Awake didn't re-run); a fresh Play fixes it
        var lp = PlayerController.LocalInstance;
        rig.FollowTarget = lp != null ? (Vector2?)(Vector2)lp.transform.position : null;
        rig.Tick(Time.unscaledDeltaTime);
        UpdateView();
    }
}
