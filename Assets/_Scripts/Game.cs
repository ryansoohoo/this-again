using UnityEngine;

// SINGLE ENTRY POINT. Boots the game as one readable story: configure app -> load settings -> camera ->
// terrain pipeline (World) -> streaming view (WorldView) -> water material -> commands. Then Update() ticks
// the camera and the view's player-follow. Also the facade UI/Player read (Cam, cell<->world geometry,
// World, minimap). Lives on the scene "Game" object (formerly GridManager).
[DefaultExecutionOrder(-100)]
public sealed class Game : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField] int pixelsPerUnit = 16;
    [SerializeField] int cellSizePixels = 16;

    [Header("Biomes")]
    [SerializeField] BiomeSettings biome = new BiomeSettings();
    [SerializeField] GroundSettings ground = new GroundSettings();
    [SerializeField] WaterSettings water = new WaterSettings();

    [Header("World Map Tiles (art)")]
    [SerializeField] Texture2D summerSheet;
    [SerializeField] Texture2D waterSheet;

    [Header("Biome tiles (empty slot = blank ground)")]
    [SerializeField] BiomeTiles grassBiome;
    [SerializeField] BiomeTiles forestBiome;
    [SerializeField] BiomeTiles rockyBiome;
    [SerializeField] BiomeTiles mountainBiome;
    [SerializeField] Sprite defaultGroundSprite;
    [SerializeField] Color defaultGroundColor = new Color(0.6f, 0.54f, 0.42f, 1f);
    [SerializeField] Color waterMinimapColor = new Color(0.1137f, 0.1686f, 0.3255f, 1f);

    [Header("Camera")]
    [SerializeField] int minCellsVisible = 10;
    [SerializeField] int startCellsVisible = 16;
    [SerializeField] float keyboardPanSpeed = 20f;
    [SerializeField] float recenterDuration = 0.25f;

    [Header("Vision")]
    [SerializeField] int viewRadius = 80;
    [SerializeField] int minimapRadius = 40;
    [SerializeField] int meshRebuildStep = 32;

    public static Game Instance { get; private set; }
    public Camera Cam { get; private set; }
    public World World { get; private set; }

    // Live-tunable knob groups; TunerPanels mutates these then calls Regenerate / ApplyWaterSettings.
    public BiomeSettings Biome => biome;
    public GroundSettings Ground => ground;
    public WaterSettings Water => water;

    // Minimap facade (the Minimap HUD reads these).
    public Texture2D MinimapTexture => view != null ? view.MinimapTexture : null;
    public Vector2 MinimapWorldCenter => view != null ? view.MinimapWorldCenter : Vector2.zero;
    public float MinimapWorldExtent => view != null ? view.MinimapWorldExtent : 0f;

    // World <-> cell geometry (Player/Camera read these).
    float cellWorld;
    public float CellWorld => cellWorld;
    public Vector2 CellCenter(int cx, int cy) => new((cx + 0.5f) * cellWorld, (cy + 0.5f) * cellWorld);
    public Vector2Int WorldToCell(Vector2 w) => new(Mathf.FloorToInt(w.x / cellWorld), Mathf.FloorToInt(w.y / cellWorld));
    public bool IsWalkable(int cx, int cy) => World != null && World.IsWalkable(cx, cy);

    CameraState cameraState;
    CameraSystem cameraSystem;
    CameraView cameraView;
    WorldView view;
    WaterMaterial waterMat;

    readonly JsonPref<BiomeSettings> biomePref = new("biome.json");
    readonly JsonPref<GroundSettings> groundPref = new("ground.json");
    readonly JsonPref<WaterSettings> waterPref = new("water.json");

    void Awake()
    {
        Instance = this;
        if (!ConfigureApp()) return;

        biomePref.Load(biome); groundPref.Load(ground); waterPref.Load(water);
        cellWorld = (float)cellSizePixels / pixelsPerUnit;

        cameraState = new CameraState();
        cameraSystem = new CameraSystem(Cam, new CameraSystem.Config
        {
            minCellsVisible = minCellsVisible,
            startCellsVisible = startCellsVisible,
            keyboardPanSpeed = keyboardPanSpeed,
            recenterDuration = recenterDuration,
        }, cellWorld, cameraState);
        cameraView = new CameraView();
        cameraView.Apply(Cam, cameraState);

        World = new World(new WorldConfig
        {
            biome = biome, ground = ground, summerSheet = summerSheet,
            grass = grassBiome, forest = forestBiome, rocky = rockyBiome, mountain = mountainBiome,
            defaultGroundSprite = defaultGroundSprite, defaultGroundColor = (Color32)defaultGroundColor,
        });

        view = new WorldView(World, new WorldViewConfig
        {
            viewRadius = viewRadius, minimapRadius = minimapRadius, meshRebuildStep = meshRebuildStep,
            waterMinimapColor = (Color32)waterMinimapColor, minimapBrightness = biome.minimapBrightness,
        }, cellWorld, transform, summerSheet, waterSheet, cameraState);

        waterMat = new WaterMaterial(view.WaterMat, water, cellWorld);

        CommandBootstrap.EnsureInstalled();          // single entry: Game installs commands
        CommandRouter.Instance.ResetScopes();

        gameObject.AddComponent<TunerPanels>();       // one accordion overlay for the biome/ground/water knobs
    }

    bool ConfigureApp()
    {
        QualitySettings.vSyncCount = 0;               // was capping fps at the monitor refresh
        Application.targetFrameRate = 120;
        Application.runInBackground = true;           // run full speed even when the game view isn't focused
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);   // skip costly stack-trace capture
        var cam = Camera.main;
        if (cam == null) { Debug.LogError("[Game] No Main Camera."); enabled = false; return false; }
        Cam = cam;
        cam.orthographic = true;
        cam.backgroundColor = Color.black;
        cam.clearFlags = CameraClearFlags.SolidColor;
        var p = cam.transform.position;
        cam.transform.position = new Vector3(0f, 0f, p.z >= 0f ? -10f : p.z);
        return true;
    }

    // Rebuild the map after a biome/ground slider change (TunerPanels).
    public void Regenerate()
    {
        if (view == null) return;
        World.Rebuild();
        view.Refresh();
    }

    public void ApplyWaterSettings() { if (waterMat != null) waterMat.Apply(); }

    // ---- Tuned settings: one JSON blob per group via JsonPref ----
    public void SaveSettings()        { biomePref.Save(biome);  Debug.Log("[Game] Biome settings saved."); }
    public void ResetSavedSettings()  { biomePref.Clear();      Debug.Log("[Game] Saved biome settings cleared (inspector defaults apply next play)."); }
    public void SaveGroundSettings()  { groundPref.Save(ground); Debug.Log("[Game] Ground settings saved."); }
    public void ResetGroundSettings() { groundPref.Reset(ground, new GroundSettings()); Regenerate(); Debug.Log("[Game] Saved ground settings cleared (defaults applied)."); }
    public void SaveWaterSettings()   { waterPref.Save(water);  Debug.Log("[Game] Water settings saved."); }
    public void ResetWaterSettings()  { waterPref.Reset(water, new WaterSettings()); ApplyWaterSettings(); Debug.Log("[Game] Saved water settings cleared (defaults applied)."); }

    void Update()
    {
        if (cameraSystem == null) return;   // e.g. after an edit-during-play domain reload; a fresh Play fixes it
        var lp = PlayerMovement.LocalInstance;
        cameraState.FollowTarget = lp != null ? (Vector2?)(Vector2)lp.transform.position : null;
        cameraSystem.Tick(Time.unscaledDeltaTime);
        cameraView.Apply(Cam, cameraState);
        view.Follow(lp != null ? lp.CurrentCell() : Vector2Int.zero);
    }
}
