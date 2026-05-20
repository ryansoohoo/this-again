using UnityEngine;

// Orchestrator: wires server (data) + rig (logic) + renderer (visual). Lives in the scene.
// Ticks the camera each frame; the biome map rebuilds on demand via Regenerate() (driven by BiomeTuner).
[DefaultExecutionOrder(-100)]
public class GridManager : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField] int pixelsPerUnit = 16;
    [SerializeField] int cellSizePixels = 16;
    [SerializeField] int gridSize = 100;

    [Header("Biomes")]
    [SerializeField] BiomeSettings biome = new BiomeSettings();   // all live-tunable noise/water knobs (see BiomeSettings)
    [SerializeField] Texture2D[] biomeTextures;                   // one floor tile per biome, in Biome enum order

    [Header("Camera")]
    [SerializeField] int minCellsVisible = 10;
    [SerializeField] int startCellsVisible = 16;
    [SerializeField] float zoomStep = 1.2f;
    [SerializeField] float keyboardPanSpeed = 20f;
    [SerializeField] float recenterDuration = 0.25f;

    CameraRig rig;

    public static GridManager Instance { get; private set; }
    public float HalfExtent { get; private set; }
    public Camera Cam { get; private set; }
    public Texture2D MinimapTexture { get; private set; }   // one pixel per cell, biome/water-colored; drawn by Minimap

    // Live-tunable biome knobs; BiomeTuner mutates these in play, then calls Regenerate.
    public BiomeSettings Biome => biome;

    float cellWorld;
    MeshFilter gridMesh;
    Color32[] biomeAvg;   // each biome tile's average art color (computed once from biomeTextures)

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
        HalfExtent = gridSize * cellWorld * 0.5f;

        rig = new CameraRig(cam, new CameraRig.Config
        {
            minCellsVisible = minCellsVisible,
            startCellsVisible = startCellsVisible,
            zoomStep = zoomStep,
            keyboardPanSpeed = keyboardPanSpeed,
            recenterDuration = recenterDuration,
        }, HalfExtent, cellWorld);

        ComputeBiomeAverages();
        ApplyPalette();
        var server = BuildServer();
        gridMesh = CellRenderer.Build(transform, server.Cells, cellWorld, HalfExtent, biomeTextures, biome.waterDepthCells);
        MinimapTexture = CellRenderer.BuildMinimapTexture(server.Cells, biome.waterDepthCells);
        gameObject.AddComponent<BiomeTuner>();
    }

    IGridServer BuildServer() => new LocalGridServer(gridSize, new BiomeGenerator(biome));

    // Rebuilds the biome map + mesh from the current noise params (called live by BiomeTuner).
    public void Regenerate()
    {
        if (gridMesh == null) return;
        ApplyPalette();
        var server = BuildServer();
        var old = gridMesh.sharedMesh;
        gridMesh.sharedMesh = CellRenderer.BuildMesh(server.Cells, cellWorld, HalfExtent, biomeTextures, biome.waterDepthCells);
        if (old != null) Destroy(old);
        var oldTex = MinimapTexture;
        MinimapTexture = CellRenderer.BuildMinimapTexture(server.Cells, biome.waterDepthCells);
        if (oldTex != null) Destroy(oldTex);
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

    void Update() => rig.Tick(Time.unscaledDeltaTime);
}
