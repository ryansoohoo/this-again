using UnityEngine;

// SINGLE ENTRY POINT. Boots the game as one readable story: configure app -> load settings -> camera ->
// terrain pipeline (World) -> streaming view (WorldView) -> water material -> commands. Then LateUpdate() ticks
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

    [Header("Structures")]
    [SerializeField] StructureSet structures;
    [SerializeField] StructureSettings structureSettings = new StructureSettings();

    [Header("Day/Night")]
    [SerializeField] DayNightSettings dayNight = new DayNightSettings();

    [Header("Replication")]
    [SerializeField] ReplicationSettings replication = new ReplicationSettings();

    [Header("Movement (free / prediction)")]
    [SerializeField] MovementSettings movement = new MovementSettings();
    [SerializeField] CombatFxSettings combatFx = new CombatFxSettings();   // tunable weapon attack FX (same for all weapons)

    [Header("Combat")]
    [SerializeField] WeaponCatalog weaponCatalog;   // byte id <-> AttackDefinition; shared by server sim + remote render
    [SerializeField] StatusCatalog statusCatalog;   // byte id <-> status effect; shared by server sim + owner predict + remote render

    [Header("View (tunable — minimap / viewport)")]
    [SerializeField] ViewSettings viewSettings = new ViewSettings();

    [Header("Vision")]
    [SerializeField] int viewRadius = 80;
    [SerializeField] int meshRebuildStep = 32;

    public static Game Instance { get; private set; }
    public Camera Cam { get; private set; }
    public World World { get; private set; }

    // Live-tunable knob groups; TunerPanels mutates these then calls Regenerate / ApplyWaterSettings.
    public BiomeSettings Biome => biome;
    public GroundSettings Ground => ground;
    public WaterSettings Water => water;
    public StructureSettings Structure => structureSettings;
    public DayNightState DayNight => dayNightSystem != null ? dayNightSystem.State : null;
    public DayNightSettings DayNightCfg => dayNight;
    public float? TimeOverride => dayNightSystem != null ? dayNightSystem.TimeOverride : null;
    public void SetTimeOverride(float? t) { if (dayNightSystem != null) dayNightSystem.TimeOverride = t; }
    public ReplicationSettings ReplicationCfg => replication;
    public MovementSettings MovementCfg => movement;
    public CombatFxSettings CombatFx => combatFx;
    public WeaponCatalog WeaponCatalog => weaponCatalog;
    public StatusCatalog StatusCatalog => statusCatalog;
    public ViewSettings ViewCfg => viewSettings;

    // Minimap facade (the Minimap HUD reads these).
    public Texture2D MinimapTexture => view != null ? view.MinimapTexture : null;
    public Vector2 MinimapWorldCenter => view != null ? view.MinimapWorldCenter : Vector2.zero;
    public Vector2 MinimapWorldExtent => view != null ? view.MinimapWorldExtent : Vector2.zero;

    // Read-only X×Y view extents in cells — the three view sizes, for inspection/tuning.
    public Vector2Int MinimapCells => new(2 * viewSettings.minimapRadius.x + 1, 2 * viewSettings.minimapRadius.y + 1);
    public Vector2 ViewportCells => cameraSystem != null ? cameraSystem.ViewportCells : Vector2.zero;

    // World <-> cell geometry (Player/Camera read these).
    float cellWorld;
    public float CellWorld => cellWorld;
    public Vector2 CellCenter(int cx, int cy) => new((cx + 0.5f) * cellWorld, (cy + 0.5f) * cellWorld);
    public Vector2Int WorldToCell(Vector2 w) => new(Mathf.FloorToInt(w.x / cellWorld), Mathf.FloorToInt(w.y / cellWorld));
    public bool IsWalkable(int cx, int cy) => World != null && World.IsWalkable(cx, cy);
    public int MoveCost(int cx, int cy) => World != null ? World.MoveCost(cx, cy) : 0;

    CameraState cameraState;
    CameraSystem cameraSystem;
    CameraView cameraView;
    bool lastInInstance;   // last-applied region for the viewport-height switch (overworld vs underworld)
    WorldView view;
    WaterMaterial waterMat;
    DayNightSystem dayNightSystem;
    DayNightView dayNightView;

    readonly JsonPref<BiomeSettings> biomePref = new("biome.json");
    readonly JsonPref<GroundSettings> groundPref = new("ground.json");
    readonly JsonPref<WaterSettings> waterPref = new("water.json");
    readonly JsonPref<StructureSettings> structurePref = new("structures.json");
    readonly JsonPref<DayNightSettings> dayNightPref = new("daynight.json");
    readonly JsonPref<ReplicationSettings> replicationPref = new("replication.json");
    readonly JsonPref<MovementSettings> movementPref = new("movement.json");
    readonly JsonPref<CombatFxSettings> combatFxPref = new("combatfx.json");
    readonly JsonPref<ViewSettings> viewPref = new("view.json");

    void Awake()
    {
        Instance = this;
        if (!ConfigureApp()) return;

        biomePref.Load(biome); groundPref.Load(ground); waterPref.Load(water); structurePref.Load(structureSettings);
        dayNightPref.Load(dayNight);
        replicationPref.Load(replication);
        movementPref.Load(movement);
        viewPref.Load(viewSettings);
        combatFxPref.Load(combatFx);
        cellWorld = (float)cellSizePixels / pixelsPerUnit;

        if (weaponCatalog == null)
            Debug.LogError("[Game] WeaponCatalog is not assigned — attacks won't replicate (server/clients can't resolve weapons, so the lunge and remote swing animation are dropped). Assign Assets/_Combat/WeaponCatalog.asset to the Game component's Weapon Catalog field.");
        if (statusCatalog == null)
            Debug.LogError("[Game] StatusCatalog is unassigned — status effects (hitstun/cooldown/poison/freeze/slow) will not work. Assign Assets/_Combat/StatusCatalog.asset to the Game component's Status Catalog field (GridManager prefab instance).");

        cameraState = new CameraState();
        cameraSystem = new CameraSystem(Cam, viewSettings, cellWorld, cameraState);
        cameraView = new CameraView();
        cameraView.Apply(Cam, cameraState);

        World = new World(new WorldConfig
        {
            biome = biome, ground = ground, summerSheet = summerSheet,
            grass = grassBiome, forest = forestBiome, rocky = rockyBiome, mountain = mountainBiome,
            structures = structures, structureSettings = structureSettings,
            defaultGroundSprite = defaultGroundSprite, defaultGroundColor = (Color32)defaultGroundColor,
        });

        view = new WorldView(World, new WorldViewConfig
        {
            viewRadius = viewRadius, meshRebuildStep = meshRebuildStep,
            waterMinimapColor = (Color32)waterMinimapColor, minimapBrightness = biome.minimapBrightness,
        }, viewSettings, cellWorld, transform, summerSheet, waterSheet, cameraState);

        waterMat = new WaterMaterial(view.WaterMat, water, cellWorld);
        dayNightSystem = new DayNightSystem(dayNight);
        dayNightView = new DayNightView(view.TerrainMat, view.WaterMat);

        CommandBootstrap.EnsureInstalled();          // single entry: Game installs commands
        CommandRouter.Instance.ResetScopes();

        gameObject.AddComponent<TunerPanels>();       // one accordion overlay for the biome/ground/water knobs
        gameObject.AddComponent<EncounterManager>();   // walk-on encounter driver
    }

    bool ConfigureApp()
    {
        QualitySettings.vSyncCount = 0;               // was capping fps at the monitor refresh
        Application.targetFrameRate = 120;
        Time.fixedDeltaTime = 1f / 60f;               // 60 Hz sim/prediction tick (was 0.02 = 50 Hz)
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

    // ---- Tuned settings: one JSON blob per group via JsonPref. Save persists the live values; Reset clears the
    // saved blob, copies fresh defaults into the live object, runs the group's apply step, then logs. (Biome is the
    // odd one out: it only clears the saved blob so the inspector defaults reappear next play.) ----
    void SaveTo<T>(JsonPref<T> pref, T value, string label) where T : class { pref.Save(value); Debug.Log($"[Game] {label} settings saved."); }
    void ResetTo<T>(JsonPref<T> pref, T live, T defaults, System.Action apply, string label) where T : class { pref.Reset(live, defaults); apply?.Invoke(); Debug.Log($"[Game] {label} settings reset (defaults applied)."); }

    public void SaveSettings()        => SaveTo(biomePref, biome, "Biome");
    public void ResetSavedSettings()  { biomePref.Clear(); Debug.Log("[Game] Saved biome settings cleared (inspector defaults apply next play)."); }
    public void SaveGroundSettings()  => SaveTo(groundPref, ground, "Ground");
    public void ResetGroundSettings() => ResetTo(groundPref, ground, new GroundSettings(), Regenerate, "Ground");
    public void SaveWaterSettings()   => SaveTo(waterPref, water, "Water");
    public void ResetWaterSettings()  => ResetTo(waterPref, water, new WaterSettings(), ApplyWaterSettings, "Water");
    public void SaveStructureSettings()  => SaveTo(structurePref, structureSettings, "Structure");
    public void ResetStructureSettings() => ResetTo(structurePref, structureSettings, new StructureSettings(), Regenerate, "Structure");
    public void SaveDayNightSettings()  => SaveTo(dayNightPref, dayNight, "Day/Night");
    public void ResetDayNightSettings() => ResetTo(dayNightPref, dayNight, new DayNightSettings(), null, "Day/Night");
    public void SaveReplicationSettings()  => SaveTo(replicationPref, replication, "Replication");
    public void ResetReplicationSettings() => ResetTo(replicationPref, replication, new ReplicationSettings(), null, "Replication");
    public void SaveMovementSettings()  => SaveTo(movementPref, movement, "Movement");
    public void ResetMovementSettings() => ResetTo(movementPref, movement, new MovementSettings(), null, "Movement");
    public void SaveViewSettings()  => SaveTo(viewPref, viewSettings, "View");
    public void ResetViewSettings() => ResetTo(viewPref, viewSettings, new ViewSettings(), ApplyViewSettings, "View");
    public void SaveCombatFxSettings()  => SaveTo(combatFxPref, combatFx, "Combat FX");
    public void ResetCombatFxSettings() => ResetTo(combatFxPref, combatFx, new CombatFxSettings(), null, "Combat FX");
    public void ApplyViewSettings()
    {
        if (cameraSystem != null)
        {
            var lp = LocalPlayer.Instance;
            cameraSystem.ApplyZoom(lp != null && lp.InInstance ? viewSettings.underworldCellsTall : viewSettings.overworldCellsTall);
        }
        if (view != null) view.Refresh();
    }

    // LateUpdate (not Update): the camera locks onto the player ghost, which GhostManager repositions in Update.
    // A follow camera must run AFTER its target has moved this frame, else it tracks last frame's pos (jitter).
    void LateUpdate()
    {
        if (cameraSystem == null) return;   // e.g. after an edit-during-play domain reload; a fresh Play fixes it
        var lp = LocalPlayer.Instance;
        bool inst = lp != null && lp.InInstance;
        if (inst != lastInInstance)   // entered/left a dungeon: switch viewport height
        {
            lastInInstance = inst;
            cameraSystem.ApplyZoom(inst ? viewSettings.underworldCellsTall : viewSettings.overworldCellsTall);
        }
        cameraState.FollowTarget = lp != null ? lp.SelfWorldPos : null;
        cameraSystem.Tick(Time.unscaledDeltaTime);
        cameraView.Apply(Cam, cameraState);
        view.Follow(lp != null ? lp.CurrentCell() : Vector2Int.zero);

        if (dayNightSystem != null) { dayNightSystem.Tick(); dayNightView.Apply(dayNightSystem.State); }
    }
}
