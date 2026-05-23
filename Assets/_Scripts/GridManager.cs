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
    [Header("World Map Tiles (art)")]
    [SerializeField] Texture2D summerSheet;                       // world_map_tiles_SUMMER.png (ground + coastline dual-grid)
    [SerializeField] Texture2D waterSheet;                        // simple_water_spritesheet.png (animated open water)
    [SerializeField] WaterSettings water = new WaterSettings();   // live-tunable open-water animation (see TunerPanels)
    [SerializeField] GroundSettings ground = new GroundSettings(); // Perlin ground-cover variety (grass/forest/rocky/mountain)

    [Header("Biome tiles (ground-cover variation; empty slot = blank/normal ground)")]
    [SerializeField] BiomeTiles grassBiome;       // lowland-dry
    [SerializeField] BiomeTiles forestBiome;      // lowland-wet
    [SerializeField] BiomeTiles rockyBiome;       // foothills
    [SerializeField] BiomeTiles mountainBiome;    // peaks
    [SerializeField] Sprite defaultGroundSprite;  // "normal ground" for no-biome cells; null = built-in grass tile
    [SerializeField] Color defaultGroundColor = new Color(0.6f, 0.54f, 0.42f, 1f);  // minimap color for no-biome (blank) cells
    [SerializeField] Color waterMinimapColor = new Color(0.1137f, 0.1686f, 0.3255f, 1f);  // flat minimap color for water (matches the water tile navy)

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

    // Live-tunable biome knobs; TunerPanels mutates these in play, then calls Regenerate.
    public BiomeSettings Biome => biome;
    public WaterSettings Water => water;
    public GroundSettings Ground => ground;
    public float CellWorld => cellWorld;

    // World <-> cell. Cell (cx,cy) spans [cx,cx+1)*cellWorld on each axis; its center sits at +half a cell.
    public Vector2 CellCenter(int cx, int cy) => new((cx + 0.5f) * cellWorld, (cy + 0.5f) * cellWorld);
    public Vector2Int WorldToCell(Vector2 world) => new(Mathf.FloorToInt(world.x / cellWorld), Mathf.FloorToInt(world.y / cellWorld));

    // Movement/pathfinding walkability: every land cell is walkable; open water is not.
    public bool IsWalkable(int cx, int cy) => GenAt(cx, cy);

    // Minimap overview world bounds, for the HUD to map the camera viewport onto it.
    public Vector2 MinimapWorldCenter => CellCenter(viewCenter.x, viewCenter.y);
    public float MinimapWorldExtent => (minimapRadius + 0.5f) * cellWorld;

    float cellWorld;
    MeshFilter gridMesh;
    Material waterMat;   // submesh-1 (water) material; WaterSettings are pushed into it live
    BiomeGenerator gen;
    GroundGenerator groundGen;                              // visual ground-cover classifier for interior land
    readonly Dictionary<Vector2Int, bool> cache = new();    // generated land(true)/water(false); grows as the player explores
    Vector2Int viewCenter;     // minimap center (tracks the player every cell)
    Vector2Int meshCenter;     // world-mesh center (recentered only every meshRebuildStep cells, since the mesh is heavy)
    bool meshInit;

    bool GenAt(int cx, int cy)
    {
        var key = new Vector2Int(cx, cy);
        if (cache.TryGetValue(key, out var b)) return b;
        b = gen.IsLand(cx, cy);
        cache[key] = b;
        return b;
    }

    // Per-cell interior-land sprite for the renderer's all-land case: classify the cell's ground cover, then
    // pick one weighted variant from that biome's BiomeTiles asset. Returns null when the cell has no usable
    // biome -> the renderer falls back to the built-in "normal ground" tile (the blank/default ground).
    Sprite LandSpriteAt(int cx, int cy)
    {
        var gt = groundGen.At(cx, cy);
        if (Hash01(cx, cy, biome.seed, 1009 + (int)gt) >= CoverageFor(gt))
            return defaultGroundSprite;          // no biome on this tile -> blank/normal ground
        var picked = PickVariant(BiomeFor(gt), cx, cy, (int)gt);
        return picked != null ? picked : defaultGroundSprite;
    }

    BiomeTiles BiomeFor(GroundType gt) => gt switch
    {
        GroundType.Forest   => forestBiome,
        GroundType.Rocky    => rockyBiome,
        GroundType.Mountain => mountainBiome,
        _                   => grassBiome,
    };

    float CoverageFor(GroundType gt) => gt switch
    {
        GroundType.Forest   => ground.forestCoverage,
        GroundType.Rocky    => ground.rockyCoverage,
        GroundType.Mountain => ground.mountainCoverage,
        _                   => ground.grassCoverage,
    };

    // Minimap color for a LAND cell: its ground-cover biome's color, or the blank color when the cell has no
    // biome (mirrors LandSpriteAt's classify + coverage roll so the minimap matches the rendered tiles).
    Color32 LandColorAt(int cx, int cy)
    {
        var gt = groundGen.At(cx, cy);
        if (Hash01(cx, cy, biome.seed, 1009 + (int)gt) >= CoverageFor(gt)) return defaultGroundColor;
        var b = BiomeFor(gt);
        return b != null ? (Color32)b.minimapColor : (Color32)defaultGroundColor;
    }

    // Weighted-random variant pick, made deterministic by hashing the cell + seed, so every Netcode client
    // resolves the same tile without replication. salt decorrelates different biomes at the same cell.
    Sprite PickVariant(BiomeTiles b, int cx, int cy, int salt)
    {
        if (b == null || b.variants == null) return null;
        float total = 0f;
        for (int i = 0; i < b.variants.Length; i++) if (IsUsable(b.variants[i])) total += b.variants[i].weight;
        if (total <= 0f) return null;
        float r = Hash01(cx, cy, biome.seed, salt) * total;
        for (int i = 0; i < b.variants.Length; i++)
        {
            if (!IsUsable(b.variants[i])) continue;
            r -= b.variants[i].weight;
            if (r < 0f) return b.variants[i].sprite;
        }
        for (int i = b.variants.Length - 1; i >= 0; i--) if (IsUsable(b.variants[i])) return b.variants[i].sprite;
        return null;
    }

    // A variant is usable only if it has a positive-weight sprite sliced from the terrain (summer) sheet — a
    // sprite from any other texture would index the wrong atlas on the shared single-material terrain mesh.
    bool IsUsable(BiomeTileVariant v)
    {
        if (v == null || v.sprite == null || v.weight <= 0f) return false;
        if (summerSheet != null && v.sprite.texture != summerSheet) { WarnForeign(v.sprite); return false; }
        return true;
    }

    readonly HashSet<Sprite> warnedForeign = new();
    void WarnForeign(Sprite s)
    {
        if (warnedForeign.Add(s))
            Debug.LogWarning($"[GridManager] Biome sprite '{s.name}' is not from the summer sheet; ignoring it. Slice biome tiles from world_map_tiles_SUMMER.");
    }

    // Deterministic 0..1 hash of (cell, seed, salt). Pure function -> identical variant choice on every client.
    static float Hash01(int x, int y, int seed, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791) ^ (uint)(salt * 2654435761);
            h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }

    void Awake()
    {
        Instance = this;
        QualitySettings.vSyncCount = 0;        // was capping fps at the 60Hz monitor refresh
        Application.targetFrameRate = 120;     // perf target
        Application.runInBackground = true;    // run full speed even when the game view isn't focused
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);  // skip costly stack-trace capture on Debug.Log
        LoadSettings();                        // apply previously-saved biome settings, if any
        LoadWaterSettings();                   // apply previously-saved water settings, if any
        LoadGroundSettings();                  // apply previously-saved ground-cover settings, if any
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
        groundGen = new GroundGenerator(biome.seed, ground);
        gridMesh = CellRenderer.Build(transform, summerSheet, waterSheet);
        waterMat = gridMesh.GetComponent<MeshRenderer>().sharedMaterials[1];
        ApplyWaterSettings();
        viewCenter = meshCenter = Vector2Int.zero;
        meshInit = true;
        RebuildMesh();
        RebuildMinimap();
        gameObject.AddComponent<TunerPanels>();   // one accordion overlay holding the biome/ground/water knob panels
    }

    // Rebuilds the noise generator + clears the cache so a fresh map streams in (called live by TunerPanels).
    public void Regenerate()
    {
        if (gridMesh == null) return;
        gen = new BiomeGenerator(biome);
        groundGen = new GroundGenerator(biome.seed, ground);
        cache.Clear();
        RebuildMesh();
        RebuildMinimap();
    }

    // The heavy world mesh (radius viewRadius) — rebuilt only every meshRebuildStep cells of movement.
    void RebuildMesh()
    {
        var oldMesh = gridMesh.sharedMesh;
        gridMesh.sharedMesh = CellRenderer.BuildWindowMesh(GenAt, LandSpriteAt, meshCenter, viewRadius, cellWorld);
        if (oldMesh != null) Destroy(oldMesh);

        // Clamp the camera to the loaded mesh so it can't pan into the unloaded (black) area.
        float size = (2 * viewRadius + 1) * cellWorld;
        if (rig != null) rig.Bounds = new Rect((meshCenter.x - viewRadius) * cellWorld, (meshCenter.y - viewRadius) * cellWorld, size, size);
    }

    // The minimap overview (same radius) — recentered on the player every cell so it scrolls smoothly.
    void RebuildMinimap()
    {
        var oldTex = MinimapTexture;
        MinimapTexture = CellRenderer.BuildOverviewMinimap(GenAt, LandColorAt, viewCenter, minimapRadius, (Color32)waterMinimapColor, biome.minimapBrightness);
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

    // Push the live water-animation knobs into the runtime water material (submesh 1). Called at boot and by
    // TunerPanels whenever a slider changes. No mesh rebuild — purely material/shader state.
    public void ApplyWaterSettings()
    {
        if (waterMat == null) return;
        waterMat.SetFloat("_AnimSpeed", water.animSpeed);
        waterMat.SetFloat("_NoiseScale", water.waveFreq);
        waterMat.SetFloat("_FlowSpeed", water.waveDrift);
        waterMat.SetFloat("_Calm", water.calm);
        waterMat.SetVector("_WindDir", new Vector4(water.windX, water.windY, 0f, 0f));
        waterMat.SetFloat("_StyleRow", water.styleRow);
        waterMat.SetFloat("_CellSize", cellWorld);
    }

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

    // ---- Save / load tuned WATER settings (separate JSON blob; live-applied) ----
    const string WaterPref = "water.json";

    public void SaveWaterSettings()
    {
        PlayerPrefs.SetString(WaterPref, JsonUtility.ToJson(water));
        PlayerPrefs.Save();
        Debug.Log("[GridManager] Water settings saved.");
    }

    void LoadWaterSettings()
    {
        var json = PlayerPrefs.GetString(WaterPref, "");
        if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, water);
    }

    public void ResetWaterSettings()
    {
        PlayerPrefs.DeleteKey(WaterPref);
        PlayerPrefs.Save();
        water = new WaterSettings();
        ApplyWaterSettings();
        Debug.Log("[GridManager] Saved water settings cleared (defaults applied).");
    }

    // ---- Save / load tuned GROUND-COVER settings (separate JSON blob; rebuilds the map on reset) ----
    const string GroundPref = "ground.json";

    public void SaveGroundSettings()
    {
        PlayerPrefs.SetString(GroundPref, JsonUtility.ToJson(ground));
        PlayerPrefs.Save();
        Debug.Log("[GridManager] Ground settings saved.");
    }

    void LoadGroundSettings()
    {
        var json = PlayerPrefs.GetString(GroundPref, "");
        if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, ground);
    }

    public void ResetGroundSettings()
    {
        PlayerPrefs.DeleteKey(GroundPref);
        PlayerPrefs.Save();
        ground = new GroundSettings();
        Regenerate();
        Debug.Log("[GridManager] Saved ground settings cleared (defaults applied).");
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
