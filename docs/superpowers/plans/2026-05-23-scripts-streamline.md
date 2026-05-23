# Scripts Streamline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure the Unity `_Scripts` project into a single-entry, category-foldered, KISS+DRY architecture that reads as a story, while preserving all current verified-in-Play behavior.

**Architecture:** A thin `Game` MonoBehaviour is the single entry/facade; it builds and ticks plain-C# subsystems (`World` terrain pipeline, `WorldView` streamer, `CameraRig`, `WaterMaterial`). Dead code and the unused climate-biome system are deleted; the three settings groups share one generic `JsonPref<T>`. Files are grouped into `Core/ World/ Player/ Camera/ Net/ UI/ Commands/`.

**Tech Stack:** Unity 6000.3.x, C#, Netcode for GameObjects, new Input System, IMGUI HUDs. Verification is the unity-mcp **refresh → Console → Play** chain (no unit-test framework; `execute_code` is broken on this machine).

---

## Pre-flight notes (read once)

- **Verify chain (every phase):** trigger an asset refresh / recompile via unity-mcp, read the Editor Console and confirm **0 compile errors**, enter Play, confirm the phase checklist, exit Play.
- **GUID safety (Phase 2+):** these scripts are referenced by scene objects / prefabs / `.asset` files — when moving or renaming, **always move the `.cs` AND its `.cs.meta` together** (preserves the GUID so references survive): `GridManager`, `PlayerController`, `Minimap`, `ChatPopup`, `CommandConsole`, `TunerPanels`, `RelayConnector`, `RelayTestHUD`, `BiomeTiles`. Plain/static scripts (CameraRig, Pathfinder, CellRenderer, Biome/Ground/Water, GameLog, Commands/*) aren't GUID-referenced, but move their `.meta` too for tidiness.
- **Commits:** plain messages, **no Claude attribution** (no `Co-Authored-By`, no "Generated with"). Stage only the files each task touches — the working tree has unrelated in-progress changes; never `git add -A`.
- **Behavior preservation:** Phases 0–2 must look identical in Play. Phase 3 is a structural move that must also look identical.

---

## Phase 0 — Dead-code purge + climate→bool collapse

**Outcome:** Delete `GridServer.cs`, the `Biomes` color static, the `Biome` enum, the unused tuning knobs, and PASS 2 of the biome generator. `BiomeGenerator.At` becomes `IsLand` returning `bool`. Behavior is identical (every consumer already collapsed climate to water-vs-land); the existing seed keeps its exact coastline.

### Task 0.1: Delete the dead `GridServer.cs`

**Files:**
- Delete: `Assets/_Scripts/GridServer.cs`
- Delete: `Assets/_Scripts/GridServer.cs.meta`

- [ ] **Step 1: Confirm it is unreferenced**

Run a search for the types it defines:
```
grep -rn "IGridServer\|LocalGridServer\|CellSnapshot\|CellData" Assets/_Scripts
```
Expected: matches only inside `GridServer.cs` itself.

- [ ] **Step 2: Delete both files**

```bash
git rm "Assets/_Scripts/GridServer.cs" "Assets/_Scripts/GridServer.cs.meta"
```
(If the `.meta` is untracked, delete it from disk instead.)

### Task 0.2: Collapse `BiomeGenerator` to land/water and trim `BiomeSettings`

**Files:**
- Modify: `Assets/_Scripts/Biome.cs` (full rewrite — the `Biomes` static and `Biome` enum are removed; `BiomeSettings` trimmed; `BiomeGenerator` collapsed)

- [ ] **Step 1: Replace the entire contents of `Assets/_Scripts/Biome.cs` with:**

```csharp
using UnityEngine;

// Live-tunable land/water knobs: serialized on the Game object, mutated by TunerPanels, saved/loaded as one
// JSON blob (PlayerPrefs). Climate sub-classification was removed — terrain is a binary land/water field.
[System.Serializable]
public class BiomeSettings
{
    public int seed = 1337;
    public float biomeScale = 0.08f;                        // domain-warp frequency (smaller = larger warp features)
    public float warpStrength = 8f;                         // domain-warp distortion, in cells
    [Range(0f, 1f)] public float seaLevel = 0.40f;          // continentalness below this -> ocean
    public float waterScale = 0.03f;                        // LOW-freq continent field (smaller = bigger continents/oceans)
    public float continentContrast = 2.5f;                 // pushes the continent field to extremes (solid land, wide ocean)
    [Range(1f, 3f)] public float minimapBrightness = 1.6f;  // minimap colors x this (brighter than the in-game map)
}

// Deterministic land/water generator. One LOW-frequency continentalness field (fBm + a contrast curve)
// carves wide oceans vs. solid continents; domain warping nudges the sample position so coastlines read
// organic, not blobby. Same seed -> identical map; all knobs are live-tunable via TunerPanels.
public sealed class BiomeGenerator
{
    const float Lacunarity = 2f, Persistence = 0.5f;
    const int ContinentOctaves = 2;   // few octaves -> smooth, big water bodies (not speckled)

    readonly float scale, waterScale, warp, sea, contrast;
    readonly float cOx, cOy, wOx, wOy, w2Ox, w2Oy;

    public BiomeGenerator(BiomeSettings s)
    {
        scale = Mathf.Max(s.biomeScale, 0.0001f);
        waterScale = Mathf.Max(s.waterScale, 0.0001f);
        warp = s.warpStrength; sea = s.seaLevel; contrast = Mathf.Max(s.continentContrast, 0.01f);
        var rng = new System.Random(s.seed);
        R(rng); R(rng); R(rng); R(rng);   // (kept) the old temp/moisture offset draws, so existing seeds keep their exact coastlines
        cOx = R(rng); cOy = R(rng); wOx = R(rng); wOy = R(rng); w2Ox = R(rng); w2Oy = R(rng);
    }

    static float R(System.Random r) => (float)r.NextDouble() * 1000f;

    // True = land, false = open water.
    public bool IsLand(int x, int y)
    {
        float wx = x, wy = y;
        if (warp > 0f)
        {
            wx += (Mathf.PerlinNoise(wOx + x * scale, wOy + y * scale) - 0.5f) * 2f * warp;
            wy += (Mathf.PerlinNoise(w2Ox + x * scale, w2Oy + y * scale) - 0.5f) * 2f * warp;
        }
        float cont = Fbm(wx, wy, cOx, cOy, waterScale, ContinentOctaves);
        cont = Mathf.Clamp01((cont - 0.5f) * contrast + 0.5f);
        return cont >= sea;
    }

    // Fractal Brownian motion: sum octaves of Perlin at rising frequency / falling amplitude. Returns ~0..1.
    float Fbm(float x, float y, float ox, float oy, float baseFreq, int oct)
    {
        float freq = baseFreq, amp = 1f, sum = 0f, norm = 0f;
        for (int o = 0; o < oct; o++)
        {
            sum += amp * Mathf.PerlinNoise(ox + x * freq, oy + y * freq);
            norm += amp;
            amp *= Persistence;
            freq *= Lacunarity;
        }
        return sum / norm;
    }
}
```

### Task 0.3: Update `CellRenderer` to the bool land field

**Files:**
- Modify: `Assets/_Scripts/CellRenderer.cs` (signatures + corner checks; remove the `Biome` dependency)

- [ ] **Step 1: Change `BuildWindowMesh`'s signature** (line ~69)

Old:
```csharp
    public static Mesh BuildWindowMesh(Func<int, int, Biome> at, Func<int, int, Sprite> landSpriteAt, Vector2Int center, int radius, float cellWorld)
```
New:
```csharp
    public static Mesh BuildWindowMesh(Func<int, int, bool> isLand, Func<int, int, Sprite> landSpriteAt, Vector2Int center, int radius, float cellWorld)
```

- [ ] **Step 2: Change the four corner reads** (lines ~88-91)

Old:
```csharp
            bool tl = at(i - 1, j)     != Biome.Water;  // top-left  cell (low x, high y)
            bool tr = at(i,     j)     != Biome.Water;  // top-right
            bool bl = at(i - 1, j - 1) != Biome.Water;  // bottom-left
            bool br = at(i,     j - 1) != Biome.Water;  // bottom-right
```
New:
```csharp
            bool tl = isLand(i - 1, j);      // top-left  cell (low x, high y)
            bool tr = isLand(i,     j);      // top-right
            bool bl = isLand(i - 1, j - 1);  // bottom-left
            bool br = isLand(i,     j - 1);  // bottom-right
```

- [ ] **Step 3: Change `BuildOverviewMinimap`'s signature** (line ~139)

Old:
```csharp
    public static Texture2D BuildOverviewMinimap(Func<int, int, Biome> at, Func<int, int, Color32> landColorAt,
                                                 Vector2Int center, int radius, Color32 waterColor, float brightness)
```
New:
```csharp
    public static Texture2D BuildOverviewMinimap(Func<int, int, bool> isLand, Func<int, int, Color32> landColorAt,
                                                 Vector2Int center, int radius, Color32 waterColor, float brightness)
```

- [ ] **Step 4: Change the minimap sampling loop** (lines ~145-155)

Old:
```csharp
        var biomes = new Biome[size * size];
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
            biomes[lx * size + ly] = at(minX + lx, minY + ly);

        var px = new Color32[size * size];
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
        {
            Biome b = biomes[lx * size + ly];
            Color32 c = b == Biome.Water ? waterColor : landColorAt(minX + lx, minY + ly);
```
New:
```csharp
        var land = new bool[size * size];
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
            land[lx * size + ly] = isLand(minX + lx, minY + ly);

        var px = new Color32[size * size];
        for (int lx = 0; lx < size; lx++)
        for (int ly = 0; ly < size; ly++)
        {
            Color32 c = land[lx * size + ly] ? landColorAt(minX + lx, minY + ly) : waterColor;
```

### Task 0.4: Update `GridManager` to the bool field + drop dead palette

**Files:**
- Modify: `Assets/_Scripts/GridManager.cs`

- [ ] **Step 1: Change the cache type** (line ~72)

Old:
```csharp
    readonly Dictionary<Vector2Int, Biome> cache = new();   // generated biomes; grows as the player explores
```
New:
```csharp
    readonly Dictionary<Vector2Int, bool> cache = new();    // generated land(true)/water(false); grows as the player explores
```

- [ ] **Step 2: Replace `GenAt` and `IsWalkable`** (lines ~61 and ~77-84)

Replace the `IsWalkable` property (line ~61):
```csharp
    public bool IsWalkable(int cx, int cy) => GenAt(cx, cy);
```
Replace the `GenAt` method (lines ~77-84):
```csharp
    bool GenAt(int cx, int cy)
    {
        var key = new Vector2Int(cx, cy);
        if (cache.TryGetValue(key, out var b)) return b;
        b = gen.IsLand(cx, cy);
        cache[key] = b;
        return b;
    }
```

- [ ] **Step 3: Delete the `ApplyPalette`/`Tinted` methods** (lines ~262-267 and ~283) and **remove their two call sites** (lines ~201 and ~219, both `ApplyPalette();`).

- [ ] **Step 4: Confirm `RebuildMesh`/`RebuildMinimap` still pass `GenAt`** — they pass `GenAt` as the first arg to `CellRenderer.BuildWindowMesh`/`BuildOverviewMinimap`; `GenAt` is now `Func<int,int,bool>`, matching the new signatures. No change needed beyond Steps 1-2.

### Task 0.5: Remove the dead sliders from `TunerPanels`

**Files:**
- Modify: `Assets/_Scripts/TunerPanels.cs` (`BiomeBody`, lines ~70-94)

- [ ] **Step 1: Replace the `BiomeBody` method body** so only surviving knobs remain:

Old (lines ~70-94):
```csharp
    void BiomeBody()
    {
        var b = gm.Biome;
        F("Scale", ref b.biomeScale, 0.01f, 0.40f, "0.000");
        I("Octaves", ref b.octaves, 1, 8);
        F("Warp", ref b.warpStrength, 0f, 20f, "0.0");
        F("Sea level", ref b.seaLevel, 0f, 1f, "0.00");
        F("Continent scale", ref b.waterScale, 0.005f, 0.10f, "0.000");
        F("Continent contrast", ref b.continentContrast, 1f, 6f, "0.0");
        F("Water shore", ref b.waterShoreLevel, 0f, 1f, "0.00");
        F("Water deep", ref b.waterDeepLevel, 0f, 1f, "0.00");
        I("Ocean depth", ref b.waterDepthCells, 1, 40);
        F("Ground bright", ref b.landBackgroundLevel, 0f, 1f, "0.00");
        F("Minimap bright", ref b.minimapBrightness, 1f, 3f, "0.0");
        F("Cold <", ref b.coldThreshold, 0f, 1f, "0.00");
        F("Hot >", ref b.hotThreshold, 0f, 1f, "0.00");
        F("Wet ≥", ref b.wetThreshold, 0f, 1f, "0.00");
        I("Seed", ref b.seed, 0, 9999);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Randomize")) { b.seed = Random.Range(0, 100000); dirty = true; }
        if (GUILayout.Button("Save")) gm.SaveSettings();
        if (GUILayout.Button("Reset")) gm.ResetSavedSettings();
        GUILayout.EndHorizontal();
    }
```
New:
```csharp
    void BiomeBody()
    {
        var b = gm.Biome;
        F("Scale", ref b.biomeScale, 0.01f, 0.40f, "0.000");
        F("Warp", ref b.warpStrength, 0f, 20f, "0.0");
        F("Sea level", ref b.seaLevel, 0f, 1f, "0.00");
        F("Continent scale", ref b.waterScale, 0.005f, 0.10f, "0.000");
        F("Continent contrast", ref b.continentContrast, 1f, 6f, "0.0");
        F("Minimap bright", ref b.minimapBrightness, 1f, 3f, "0.0");
        I("Seed", ref b.seed, 0, 9999);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Randomize")) { b.seed = Random.Range(0, 100000); dirty = true; }
        if (GUILayout.Button("Save")) gm.SaveSettings();
        if (GUILayout.Button("Reset")) gm.ResetSavedSettings();
        GUILayout.EndHorizontal();
    }
```

### Task 0.6: Verify + commit Phase 0

- [ ] **Step 1: Verify**

Refresh assets via unity-mcp → read Console → expect **0 compile errors**. Enter Play and confirm:
- The map renders **identically** to before (same coastlines — the kept RNG draws preserve the seed).
- Coastline still dual-grid autotiles; interior land variety + blank coverage unchanged.
- Open water still animates; minimap colors match; player can walk/path; commands work.
Exit Play.

- [ ] **Step 2: Commit**

```bash
git add "Assets/_Scripts/Biome.cs" "Assets/_Scripts/CellRenderer.cs" "Assets/_Scripts/GridManager.cs" "Assets/_Scripts/TunerPanels.cs"
git rm --cached "Assets/_Scripts/GridServer.cs" "Assets/_Scripts/GridServer.cs.meta" 2>/dev/null; true
git commit -m "Delete dead code; collapse biome generator to land/water"
```

---

## Phase 1 — DRY the settings persistence with `JsonPref<T>`

**Outcome:** One generic helper replaces the nine copy-pasted Save/Load/Reset methods in `GridManager`. Public method names that `TunerPanels` calls are unchanged.

### Task 1.1: Add the generic `JsonPref<T>` helper

**Files:**
- Create: `Assets/_Scripts/JsonPref.cs`

- [ ] **Step 1: Create `Assets/_Scripts/JsonPref.cs`:**

```csharp
using UnityEngine;

// Generic PlayerPrefs-backed JSON persistence for one [Serializable] settings object. Replaces the
// per-settings Save/Load/Reset copies. Load() overwrites the live instance in place so references held by
// subsystems stay valid; Reset() clears the saved blob and copies fresh defaults into the live instance.
public sealed class JsonPref<T> where T : class
{
    readonly string key;
    public JsonPref(string key) { this.key = key; }

    public void Save(T value)
    {
        PlayerPrefs.SetString(key, JsonUtility.ToJson(value));
        PlayerPrefs.Save();
    }

    public void Load(T into)
    {
        var json = PlayerPrefs.GetString(key, "");
        if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, into);
    }

    public void Clear()
    {
        PlayerPrefs.DeleteKey(key);
        PlayerPrefs.Save();
    }

    public void Reset(T into, T defaults)   // clear saved blob + copy defaults into the live object
    {
        Clear();
        JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(defaults), into);
    }
}
```

### Task 1.2: Route `GridManager`'s settings through `JsonPref`

**Files:**
- Modify: `Assets/_Scripts/GridManager.cs` (the three const keys + nine methods, lines ~285-356; and `LoadSettings`/`LoadWaterSettings`/`LoadGroundSettings` calls in `Awake`, lines ~178-179)

- [ ] **Step 1: Add three `JsonPref` fields** near the other private fields (after line ~75):

```csharp
    readonly JsonPref<BiomeSettings> biomePref = new("biome.json");
    readonly JsonPref<WaterSettings> waterPref = new("water.json");
    readonly JsonPref<GroundSettings> groundPref = new("ground.json");
```

- [ ] **Step 2: Replace the entire settings region** (everything from `// ---- Save / load tuned settings ...` at line ~285 through the end of `ResetGroundSettings` at line ~356) with:

```csharp
    // ---- Tuned settings: one JSON blob per group via JsonPref (survives play-stop + editor restarts) ----

    public void SaveSettings()       { biomePref.Save(biome);  Debug.Log("[GridManager] Biome settings saved."); }
    public void ResetSavedSettings() { biomePref.Clear();      Debug.Log("[GridManager] Saved biome settings cleared (inspector defaults apply next play)."); }

    public void SaveWaterSettings()  { waterPref.Save(water);  Debug.Log("[GridManager] Water settings saved."); }
    public void ResetWaterSettings() { waterPref.Reset(water, new WaterSettings()); ApplyWaterSettings(); Debug.Log("[GridManager] Saved water settings cleared (defaults applied)."); }

    public void SaveGroundSettings() { groundPref.Save(ground); Debug.Log("[GridManager] Ground settings saved."); }
    public void ResetGroundSettings(){ groundPref.Reset(ground, new GroundSettings()); Regenerate(); Debug.Log("[GridManager] Saved ground settings cleared (defaults applied)."); }
```

- [ ] **Step 3: Replace the three load calls in `Awake`** (lines ~177-179)

Old:
```csharp
        LoadSettings();                        // apply previously-saved biome settings, if any
        LoadWaterSettings();                   // apply previously-saved water settings, if any
        LoadGroundSettings();                  // apply previously-saved ground-cover settings, if any
```
New:
```csharp
        biomePref.Load(biome);                 // apply previously-saved settings, if any
        waterPref.Load(water);
        groundPref.Load(ground);
```

(The old `const string Pref/WaterPref/GroundPref` and `LoadSettings/LoadWaterSettings/LoadGroundSettings` private methods were inside the replaced region / are now unused — confirm none remain after Step 2. `SaveSettings`, `ResetSavedSettings`, etc. keep their names so `TunerPanels` is untouched.)

### Task 1.3: Verify + commit Phase 1

- [ ] **Step 1: Verify**

Refresh → Console → 0 errors → Play. In a tuner panel, move a slider, click **Save**; stop and re-enter Play → the saved value persists. Click **Reset** → defaults return (water/ground reset live; biome resets on next play). Exit Play.

- [ ] **Step 2: Commit**

```bash
git add "Assets/_Scripts/JsonPref.cs" "Assets/_Scripts/GridManager.cs"
git commit -m "DRY settings persistence behind generic JsonPref<T>"
```

---

## Phase 2 — Category folders + split data/logic files

**Outcome:** Files live in `Core/ World/ Player/ Camera/ Net/ UI/ Commands/`. `Biome.cs`/`Ground.cs`/`Water.cs` split into data + logic files. No code logic changes — only file locations, file names, and (for the splits) which file a class sits in.

### Task 2.1: Create the category folders

**Files:**
- Create dirs: `Assets/_Scripts/Core`, `World`, `Player`, `Camera`, `Net`, `UI` (`Commands` already exists)

- [ ] **Step 1: Make the folders**

```bash
mkdir -p "Assets/_Scripts/Core" "Assets/_Scripts/World" "Assets/_Scripts/Player" "Assets/_Scripts/Camera" "Assets/_Scripts/Net" "Assets/_Scripts/UI"
```
(Unity will generate folder `.meta` files on the next refresh.)

### Task 2.2: Move the GUID-referenced scripts (`.cs` + `.cs.meta` together)

**Files:** move each pair to its category folder.

- [ ] **Step 1: Move with `git mv` (moves file + meta when both are tracked; otherwise move on disk):**

```bash
git mv "Assets/_Scripts/PlayerController.cs" "Assets/_Scripts/Player/PlayerController.cs"
git mv "Assets/_Scripts/PlayerController.cs.meta" "Assets/_Scripts/Player/PlayerController.cs.meta"
git mv "Assets/_Scripts/Minimap.cs" "Assets/_Scripts/UI/Minimap.cs"
git mv "Assets/_Scripts/Minimap.cs.meta" "Assets/_Scripts/UI/Minimap.cs.meta"
git mv "Assets/_Scripts/ChatPopup.cs" "Assets/_Scripts/UI/ChatPopup.cs"
git mv "Assets/_Scripts/ChatPopup.cs.meta" "Assets/_Scripts/UI/ChatPopup.cs.meta"
git mv "Assets/_Scripts/CommandConsole.cs" "Assets/_Scripts/UI/CommandConsole.cs"
git mv "Assets/_Scripts/CommandConsole.cs.meta" "Assets/_Scripts/UI/CommandConsole.cs.meta"
git mv "Assets/_Scripts/RelayConnector.cs" "Assets/_Scripts/Net/RelayConnector.cs"
git mv "Assets/_Scripts/RelayConnector.cs.meta" "Assets/_Scripts/Net/RelayConnector.cs.meta"
git mv "Assets/_Scripts/RelayTestHUD.cs" "Assets/_Scripts/Net/RelayTestHUD.cs"
git mv "Assets/_Scripts/RelayTestHUD.cs.meta" "Assets/_Scripts/Net/RelayTestHUD.cs.meta"
```

For the **untracked** new files (`TunerPanels.cs`, `BiomeTiles.cs` and their metas show as `??` in git status) move them on disk so the `.meta` GUID is kept:
```bash
mv "Assets/_Scripts/TunerPanels.cs" "Assets/_Scripts/UI/TunerPanels.cs"
mv "Assets/_Scripts/TunerPanels.cs.meta" "Assets/_Scripts/UI/TunerPanels.cs.meta"
mv "Assets/_Scripts/BiomeTiles.cs" "Assets/_Scripts/World/BiomeTiles.cs"
mv "Assets/_Scripts/BiomeTiles.cs.meta" "Assets/_Scripts/World/BiomeTiles.cs.meta"
```

### Task 2.3: Move the plain/static scripts

- [ ] **Step 1: Move (file + meta) — these aren't GUID-referenced but keep metas tidy:**

```bash
git mv "Assets/_Scripts/CameraRig.cs" "Assets/_Scripts/Camera/CameraRig.cs"
git mv "Assets/_Scripts/CameraRig.cs.meta" "Assets/_Scripts/Camera/CameraRig.cs.meta"
git mv "Assets/_Scripts/Pathfinder.cs" "Assets/_Scripts/Player/Pathfinder.cs"
git mv "Assets/_Scripts/Pathfinder.cs.meta" "Assets/_Scripts/Player/Pathfinder.cs.meta"
git mv "Assets/_Scripts/CellRenderer.cs" "Assets/_Scripts/World/CellRenderer.cs"
git mv "Assets/_Scripts/CellRenderer.cs.meta" "Assets/_Scripts/World/CellRenderer.cs.meta"
git mv "Assets/_Scripts/GameLog.cs" "Assets/_Scripts/Core/GameLog.cs"
git mv "Assets/_Scripts/GameLog.cs.meta" "Assets/_Scripts/Core/GameLog.cs.meta"
git mv "Assets/_Scripts/Commands/OutputType.cs" "Assets/_Scripts/Core/OutputType.cs"
git mv "Assets/_Scripts/Commands/OutputType.cs.meta" "Assets/_Scripts/Core/OutputType.cs.meta"
git mv "Assets/_Scripts/CommandBootstrap.cs" "Assets/_Scripts/Commands/CommandBootstrap.cs"
git mv "Assets/_Scripts/CommandBootstrap.cs.meta" "Assets/_Scripts/Commands/CommandBootstrap.cs.meta"
git mv "Assets/_Scripts/JsonPref.cs" "Assets/_Scripts/Core/JsonPref.cs"
git mv "Assets/_Scripts/JsonPref.cs.meta" "Assets/_Scripts/Core/JsonPref.cs.meta"
```
(`GridManager.cs` stays put for now; it is renamed/moved in Phase 3.)

### Task 2.4: Split `Water.cs` → `World/WaterSettings.cs`

**Files:**
- Create: `Assets/_Scripts/World/WaterSettings.cs`
- Delete: `Assets/_Scripts/Water.cs` (+ `.meta`)

- [ ] **Step 1: Create `Assets/_Scripts/World/WaterSettings.cs`** with the exact current contents of `Water.cs` (the `WaterSettings` class is unchanged):

```csharp
using UnityEngine;

// All live-tunable open-water animation knobs in one place: serialized on Game, mutated by TunerPanels,
// saved/loaded as a single JSON blob (PlayerPrefs). Pushed into the runtime water material by WaterMaterial.
// Defaults match the WaterWaves.shader property defaults.
[System.Serializable]
public class WaterSettings
{
    public float animSpeed = 1.5f;    // ripple spritesheet frames/sec (independent of wave drift)
    public float waveFreq = 0.35f;    // shader _NoiseScale: higher = smaller, more frequent waves
    public float waveDrift = 0.06f;   // shader _FlowSpeed: how fast wave/calm regions travel
    [Range(0f, 1f)] public float calm = 0.5f;   // fraction of water that stays flat blue (no ripple)
    public float windX = 1f;          // wave drift direction
    public float windY = 0.35f;
    [Range(0, 2)] public int styleRow = 0;      // which of the 3 water styles in the sheet
}
```

- [ ] **Step 2: Delete the old file**

```bash
rm "Assets/_Scripts/Water.cs" "Assets/_Scripts/Water.cs.meta"
```

### Task 2.5: Split `Ground.cs` → `World/GroundSettings.cs` + `World/GroundGenerator.cs`

**Files:**
- Create: `Assets/_Scripts/World/GroundSettings.cs`, `Assets/_Scripts/World/GroundGenerator.cs`
- Delete: `Assets/_Scripts/Ground.cs` (+ `.meta`)

- [ ] **Step 1: Create `Assets/_Scripts/World/GroundSettings.cs`** (the `GroundType` enum + `GroundSettings` class, unchanged):

```csharp
using UnityEngine;

// Ground-cover variety for LAND tiles (purely visual; gameplay only cares about water vs not-water).
public enum GroundType { Grass, Forest, Rocky, Mountain }

// Tunable knobs for the ground-cover noise (serialized on Game).
[System.Serializable]
public class GroundSettings
{
    public float scale = 0.05f;                          // region size (smaller = larger patches)
    [Range(1, 6)] public int octaves = 3;
    [Range(0f, 1f)] public float mountainLevel = 0.66f;  // elevation above -> Mountain (peaks; rare)
    [Range(0f, 1f)] public float rockyLevel = 0.56f;     // elevation above -> Rocky (foothills)
    [Range(0f, 1f)] public float forestLevel = 0.50f;    // lowland moisture at/above -> Forest, else Grass

    // Per-biome coverage: fraction of a biome's cells that actually GET the biome. The rest render as the
    // blank/normal ground tile. 1 = solid (no blank); lower = more scattered blank.
    [Range(0f, 1f)] public float grassCoverage = 0.3f;
    [Range(0f, 1f)] public float forestCoverage = 1f;
    [Range(0f, 1f)] public float rockyCoverage = 1f;
    [Range(0f, 1f)] public float mountainCoverage = 1f;
}
```

- [ ] **Step 2: Create `Assets/_Scripts/World/GroundGenerator.cs`** (the `GroundGenerator` class, unchanged):

```csharp
using UnityEngine;

// Two fBm fields: an "elevation" field carves mountain peaks > rocky foothills > lowland; within the lowland
// a separate "moisture" field splits forest vs grass. Deterministic from the seed, so Netcode clients agree.
public sealed class GroundGenerator
{
    const float Lacunarity = 2f, Persistence = 0.5f;
    readonly float scale, mountainLevel, rockyLevel, forestLevel;
    readonly int octaves;
    readonly float eOx, eOy, mOx, mOy;

    public GroundGenerator(int seed, GroundSettings s)
    {
        scale = Mathf.Max(s.scale, 0.0001f);
        octaves = Mathf.Clamp(s.octaves, 1, 6);
        mountainLevel = s.mountainLevel; rockyLevel = s.rockyLevel; forestLevel = s.forestLevel;
        var rng = new System.Random(seed * 131071 + 99);   // distinct from the biome generator's offsets
        eOx = R(rng); eOy = R(rng); mOx = R(rng); mOy = R(rng);
    }

    static float R(System.Random r) => (float)r.NextDouble() * 1000f;

    public GroundType At(int x, int y)
    {
        float elev = Fbm(x, y, eOx, eOy);
        if (elev > mountainLevel) return GroundType.Mountain;
        if (elev > rockyLevel) return GroundType.Rocky;
        float moist = Fbm(x, y, mOx, mOy);
        return moist >= forestLevel ? GroundType.Forest : GroundType.Grass;
    }

    float Fbm(float x, float y, float ox, float oy)
    {
        float freq = scale, amp = 1f, sum = 0f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Mathf.PerlinNoise(ox + x * freq, oy + y * freq);
            norm += amp;
            amp *= Persistence;
            freq *= Lacunarity;
        }
        return sum / norm;
    }
}
```

- [ ] **Step 3: Delete the old file**

```bash
rm "Assets/_Scripts/Ground.cs" "Assets/_Scripts/Ground.cs.meta"
```

### Task 2.6: Split `Biome.cs` → `World/BiomeSettings.cs` + `World/BiomeGenerator.cs`

**Files:**
- Create: `Assets/_Scripts/World/BiomeSettings.cs`, `Assets/_Scripts/World/BiomeGenerator.cs`
- Delete: `Assets/_Scripts/Biome.cs` (+ `.meta`)

- [ ] **Step 1: Create `Assets/_Scripts/World/BiomeSettings.cs`** with the `BiomeSettings` class from the Phase 0 rewrite (the `using UnityEngine;` + the `[System.Serializable] public class BiomeSettings { ... }` block only).

- [ ] **Step 2: Create `Assets/_Scripts/World/BiomeGenerator.cs`** with the `BiomeGenerator` class from the Phase 0 rewrite (the `using UnityEngine;` + the `public sealed class BiomeGenerator { ... }` block only).

- [ ] **Step 3: Delete the old file**

```bash
rm "Assets/_Scripts/Biome.cs" "Assets/_Scripts/Biome.cs.meta"
```

### Task 2.7: Verify + commit Phase 2

- [ ] **Step 1: Verify**

Refresh → Console → **0 compile errors** and **no "missing script" warnings** on scene objects (confirms GUIDs survived the moves). Enter Play → everything behaves exactly as Phase 1. Exit Play.

- [ ] **Step 2: Commit**

```bash
git add -A "Assets/_Scripts"
git commit -m "Sort scripts into category folders; split settings/generator files"
```
(Here `-A` under the `Assets/_Scripts` path is intentional — it captures the renames/deletes/creates of the move. Do not widen the path.)

---

## Phase 3 — Split `GridManager` into `Game` + `World` + `WorldView` + `WaterMaterial`

**Outcome:** `GridManager` becomes a thin `Core/Game.cs` (single entry + facade). The terrain query layer moves to `World/World.cs`; streaming to `World/WorldView.cs`; the water-material push to `World/WaterMaterial.cs`. A `Core/InputState.cs` flag replaces `CommandConsole.IsTyping` so non-UI code stops depending on UI.

### Task 3.1: Add the `InputState` flag (decouple input gating from UI)

**Files:**
- Create: `Assets/_Scripts/Core/InputState.cs`

- [ ] **Step 1: Create `Assets/_Scripts/Core/InputState.cs`:**

```csharp
// Cross-cutting input gate: true while the text console is capturing keystrokes, so gameplay/camera input
// stays suppressed. Lives in Core (not UI) so Camera/Player can read it without depending on the console.
public static class InputState
{
    public static bool Typing;
}
```

- [ ] **Step 2: In `Assets/_Scripts/UI/CommandConsole.cs`**, make the console drive the flag.

Replace the property (line ~15):
```csharp
    public static bool IsTyping { get; private set; }
```
with:
```csharp
    public static bool IsTyping => InputState.Typing;   // back-compat alias; the source of truth is InputState
```
Then set `InputState.Typing` everywhere `IsTyping` was assigned: in `Awake` (`IsTyping = false;` → `InputState.Typing = false;`), `OnDisable` (`IsTyping = false;` → `InputState.Typing = false;`), `Open` (`open = true; IsTyping = true;` → `open = true; InputState.Typing = true;`), and `Close` (`open = false; IsTyping = false;` → `open = false; InputState.Typing = false;`).

- [ ] **Step 3: Point the readers at `InputState`.**

In `Assets/_Scripts/Camera/CameraRig.cs` replace both `CommandConsole.IsTyping` reads (in `KeyboardPan` line ~178 and `SpacePressed` line ~190) with `InputState.Typing`.
In `Assets/_Scripts/Player/PlayerController.cs` replace the `CommandConsole.IsTyping` read (in `ReadOwnerInput` line ~91) with `InputState.Typing`.
In `Assets/_Scripts/UI/ChatPopup.cs` replace the `CommandConsole.IsTyping` read (in `Update` line ~61) with `InputState.Typing`.

### Task 3.2: Create `World/World.cs` (terrain pipeline)

**Files:**
- Create: `Assets/_Scripts/World/World.cs`

- [ ] **Step 1: Create `Assets/_Scripts/World/World.cs`:**

```csharp
using System.Collections.Generic;
using UnityEngine;

// The terrain pipeline: answers "what is at cell (x,y)?" A pure query layer over two deterministic noise
// generators + the per-biome tile assets. Same seed -> identical answers on every Netcode client (no
// replication). Game builds it; WorldView samples it for the mesh/minimap; Player/Pathfinder use IsWalkable.
public sealed class World
{
    public sealed class Config
    {
        public BiomeSettings biome;
        public GroundSettings ground;
        public Texture2D summerSheet;
        public BiomeTiles grass, forest, rocky, mountain;
        public Sprite defaultGroundSprite;
        public Color32 defaultGroundColor;
    }

    readonly Config cfg;
    BiomeGenerator gen;
    GroundGenerator groundGen;
    readonly Dictionary<Vector2Int, bool> landCache = new();   // generated land(true)/water(false); grows as you explore
    readonly HashSet<Sprite> warnedForeign = new();

    public World(Config cfg) { this.cfg = cfg; Rebuild(); }

    // Rebuild the generators + clear the cache (after a live settings change).
    public void Rebuild()
    {
        gen = new BiomeGenerator(cfg.biome);
        groundGen = new GroundGenerator(cfg.biome.seed, cfg.ground);
        landCache.Clear();
    }

    public bool IsLand(int cx, int cy)
    {
        var key = new Vector2Int(cx, cy);
        if (landCache.TryGetValue(key, out var v)) return v;
        v = gen.IsLand(cx, cy);
        landCache[key] = v;
        return v;
    }

    public bool IsWalkable(int cx, int cy) => IsLand(cx, cy);   // everything but open water is walkable

    // Per-cell interior-land sprite (renderer's all-land case): classify cover, roll coverage, pick a weighted
    // variant. Null -> the renderer's built-in blank ground tile.
    public Sprite LandSprite(int cx, int cy)
    {
        var gt = groundGen.At(cx, cy);
        if (Hash01(cx, cy, cfg.biome.seed, 1009 + (int)gt) >= CoverageFor(gt)) return cfg.defaultGroundSprite;
        var picked = PickVariant(BiomeFor(gt), cx, cy, (int)gt);
        return picked != null ? picked : cfg.defaultGroundSprite;
    }

    // Minimap color for a land cell: its cover-biome color, or the blank color when coverage rolls it out.
    public Color32 LandColor(int cx, int cy)
    {
        var gt = groundGen.At(cx, cy);
        if (Hash01(cx, cy, cfg.biome.seed, 1009 + (int)gt) >= CoverageFor(gt)) return cfg.defaultGroundColor;
        var b = BiomeFor(gt);
        return b != null ? (Color32)b.minimapColor : cfg.defaultGroundColor;
    }

    BiomeTiles BiomeFor(GroundType gt) => gt switch
    {
        GroundType.Forest   => cfg.forest,
        GroundType.Rocky    => cfg.rocky,
        GroundType.Mountain => cfg.mountain,
        _                   => cfg.grass,
    };

    float CoverageFor(GroundType gt) => gt switch
    {
        GroundType.Forest   => cfg.ground.forestCoverage,
        GroundType.Rocky    => cfg.ground.rockyCoverage,
        GroundType.Mountain => cfg.ground.mountainCoverage,
        _                   => cfg.ground.grassCoverage,
    };

    // Weighted-random variant pick made deterministic by hashing (cell, seed, salt) -> every client agrees.
    Sprite PickVariant(BiomeTiles b, int cx, int cy, int salt)
    {
        if (b == null || b.variants == null) return null;
        float total = 0f;
        for (int i = 0; i < b.variants.Length; i++) if (IsUsable(b.variants[i])) total += b.variants[i].weight;
        if (total <= 0f) return null;
        float r = Hash01(cx, cy, cfg.biome.seed, salt) * total;
        for (int i = 0; i < b.variants.Length; i++)
        {
            if (!IsUsable(b.variants[i])) continue;
            r -= b.variants[i].weight;
            if (r < 0f) return b.variants[i].sprite;
        }
        for (int i = b.variants.Length - 1; i >= 0; i--) if (IsUsable(b.variants[i])) return b.variants[i].sprite;
        return null;
    }

    // Usable only if a positive-weight sprite sliced from the summer sheet (a foreign texture would index the
    // wrong atlas on the shared single-material terrain mesh).
    bool IsUsable(BiomeTileVariant v)
    {
        if (v == null || v.sprite == null || v.weight <= 0f) return false;
        if (cfg.summerSheet != null && v.sprite.texture != cfg.summerSheet) { WarnForeign(v.sprite); return false; }
        return true;
    }

    void WarnForeign(Sprite s)
    {
        if (warnedForeign.Add(s))
            Debug.LogWarning($"[World] Biome sprite '{s.name}' is not from the summer sheet; ignoring it. Slice biome tiles from world_map_tiles_SUMMER.");
    }

    // Deterministic 0..1 hash of (cell, seed, salt). Pure -> identical variant choice on every client.
    static float Hash01(int x, int y, int seed, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791) ^ (uint)(salt * 2654435761);
            h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
```

### Task 3.3: Create `World/WorldView.cs` (streaming)

**Files:**
- Create: `Assets/_Scripts/World/WorldView.cs`

- [ ] **Step 1: Create `Assets/_Scripts/World/WorldView.cs`:**

```csharp
using UnityEngine;

// Streams the visible window: rebuilds the world mesh (radius viewRadius) only every meshRebuildStep cells of
// player movement, and the wider minimap every cell. Sets the camera's pan bounds to the loaded mesh so no
// unloaded (black) area can show. Game ticks Follow(); WorldView calls CellRenderer to build the geometry.
public sealed class WorldView
{
    public sealed class Config
    {
        public int viewRadius, minimapRadius, meshRebuildStep;
        public Color32 waterMinimapColor;
        public float minimapBrightness;
    }

    readonly World world;
    readonly Config cfg;
    readonly float cellWorld;
    readonly MeshFilter gridMesh;
    readonly CameraRig rig;

    public Material WaterMat { get; }                       // submesh-1 material; WaterMaterial pushes settings into it
    public Texture2D MinimapTexture { get; private set; }
    public Vector2Int ViewCenter { get; private set; }      // minimap center (tracks the player every cell)

    Vector2Int meshCenter;
    bool meshInit;

    public WorldView(World world, Config cfg, float cellWorld, Transform parent,
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
```

### Task 3.4: Create `World/WaterMaterial.cs`

**Files:**
- Create: `Assets/_Scripts/World/WaterMaterial.cs`

- [ ] **Step 1: Create `Assets/_Scripts/World/WaterMaterial.cs`:**

```csharp
using UnityEngine;

// Pushes the live WaterSettings into the runtime water material (mesh submesh 1). No mesh rebuild — pure
// shader state. Game calls Apply() at boot and whenever a water slider changes.
public sealed class WaterMaterial
{
    readonly Material mat;
    readonly WaterSettings s;
    readonly float cellWorld;

    public WaterMaterial(Material mat, WaterSettings s, float cellWorld)
    {
        this.mat = mat; this.s = s; this.cellWorld = cellWorld;
        Apply();
    }

    public void Apply()
    {
        if (mat == null) return;
        mat.SetFloat("_AnimSpeed", s.animSpeed);
        mat.SetFloat("_NoiseScale", s.waveFreq);
        mat.SetFloat("_FlowSpeed", s.waveDrift);
        mat.SetFloat("_Calm", s.calm);
        mat.SetVector("_WindDir", new Vector4(s.windX, s.windY, 0f, 0f));
        mat.SetFloat("_StyleRow", s.styleRow);
        mat.SetFloat("_CellSize", cellWorld);
    }
}
```

### Task 3.5: Replace `GridManager.cs` with `Core/Game.cs`

**Files:**
- Rename: `Assets/_Scripts/GridManager.cs` → `Assets/_Scripts/Core/Game.cs` (and `.meta` → `.meta`, GUID kept)
- Rewrite the renamed file's contents to the slim `Game` class below

- [ ] **Step 1: Rename the file + meta (keep the GUID so the scene component stays linked):**

```bash
git mv "Assets/_Scripts/GridManager.cs" "Assets/_Scripts/Core/Game.cs"
git mv "Assets/_Scripts/GridManager.cs.meta" "Assets/_Scripts/Core/Game.cs.meta"
```

- [ ] **Step 2: Replace the entire contents of `Assets/_Scripts/Core/Game.cs` with:**

```csharp
using UnityEngine;

// SINGLE ENTRY POINT. Boots the game as one readable story: configure app -> load settings -> build the
// terrain pipeline (World) -> camera -> streaming view (WorldView) -> water material -> commands. Then
// Update() ticks the camera and the view's player-follow. Also the facade UI/Player read (Cam, cell<->world
// geometry, World, minimap). Lives on the scene "Game" object (formerly GridManager).
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

    // Minimap facade (Minimap HUD reads these).
    public Texture2D MinimapTexture => view != null ? view.MinimapTexture : null;
    public Vector2 MinimapWorldCenter => view != null ? view.MinimapWorldCenter : Vector2.zero;
    public float MinimapWorldExtent => view != null ? view.MinimapWorldExtent : 0f;

    // World <-> cell geometry (Player/Camera read these).
    float cellWorld;
    public float CellWorld => cellWorld;
    public Vector2 CellCenter(int cx, int cy) => new((cx + 0.5f) * cellWorld, (cy + 0.5f) * cellWorld);
    public Vector2Int WorldToCell(Vector2 w) => new(Mathf.FloorToInt(w.x / cellWorld), Mathf.FloorToInt(w.y / cellWorld));
    public bool IsWalkable(int cx, int cy) => World != null && World.IsWalkable(cx, cy);

    CameraRig rig;
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

        rig = new CameraRig(Cam, new CameraRig.Config
        {
            minCellsVisible = minCellsVisible,
            startCellsVisible = startCellsVisible,
            keyboardPanSpeed = keyboardPanSpeed,
            recenterDuration = recenterDuration,
        }, cellWorld);

        World = new World(new World.Config
        {
            biome = biome, ground = ground, summerSheet = summerSheet,
            grass = grassBiome, forest = forestBiome, rocky = rockyBiome, mountain = mountainBiome,
            defaultGroundSprite = defaultGroundSprite, defaultGroundColor = (Color32)defaultGroundColor,
        });

        view = new WorldView(World, new WorldView.Config
        {
            viewRadius = viewRadius, minimapRadius = minimapRadius, meshRebuildStep = meshRebuildStep,
            waterMinimapColor = (Color32)waterMinimapColor, minimapBrightness = biome.minimapBrightness,
        }, cellWorld, transform, summerSheet, waterSheet, rig);

        waterMat = new WaterMaterial(view.WaterMat, water, cellWorld);

        CommandBootstrap.EnsureInstalled();          // single entry: Game installs commands (was CommandConsole.Awake)
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
        if (rig == null) return;   // e.g. after an edit-during-play domain reload; a fresh Play fixes it
        var lp = PlayerController.LocalInstance;
        rig.FollowTarget = lp != null ? (Vector2?)(Vector2)lp.transform.position : null;
        rig.Tick(Time.unscaledDeltaTime);
        view.Follow(lp != null ? lp.CurrentCell() : Vector2Int.zero);
    }
}
```

### Task 3.6: Update consumers to reference `Game`

**Files:**
- Modify: `Assets/_Scripts/Player/PlayerController.cs`, `Assets/_Scripts/UI/Minimap.cs`, `Assets/_Scripts/UI/TunerPanels.cs`, `Assets/_Scripts/UI/CommandConsole.cs`

- [ ] **Step 1: `PlayerController.cs`** — replace every `GridManager.Instance` with `Game.Instance` and every local typed as `GridManager` with `Game`. (Calls used: `.CellCenter`, `.WorldToCell`, `.IsWalkable` — all exist on the `Game` facade, so only the type name changes.)

- [ ] **Step 2: `Minimap.cs`** — replace every `GridManager.Instance` with `Game.Instance` and `var gm = GridManager.Instance;` with `var gm = Game.Instance;`. (Uses `.Cam`, `.MinimapWorldExtent`, `.MinimapWorldCenter`, `.MinimapTexture` — all on the facade.)

- [ ] **Step 3: `TunerPanels.cs`** — replace the field `GridManager gm;` with `Game gm;` and `gm = GridManager.Instance;` with `gm = Game.Instance;`. (All `gm.*` calls exist on `Game`.)

- [ ] **Step 4: `CommandConsole.cs`** — remove the now-duplicated bootstrap from `Awake` (Game owns it). Delete these two lines from `Awake` (~lines 62-63):
```csharp
        CommandBootstrap.EnsureInstalled();
        CommandRouter.Instance.ResetScopes();
```
(`Game` has `[DefaultExecutionOrder(-100)]`, so it installs commands before any console use.)

### Task 3.7: Rename the scene GameObject (optional cosmetic) + verify + commit

- [ ] **Step 1: Verify the script link**

Refresh → Console. Expect **0 compile errors** and **no "missing script"** on the former `GridManager` object (the GUID was preserved by the file+meta rename, and all `[SerializeField]` field names are unchanged, so its Inspector values — sheets, BiomeTiles SOs, radii, colors — survive).

- [ ] **Step 2: (Optional) rename the GameObject** in the scene from `GridManager` to `Game` for tidiness. This is a scene edit, not required for function.

- [ ] **Step 3: Play-verify the full checklist**

Enter Play and confirm, end to end:
- Terrain renders identically; coastline dual-grid autotiles; interior land variety + blank coverage; open water animates (calm/ripple split).
- Minimap colors match the land; water is flat navy; viewport box + center dot track the camera.
- Camera: right-drag pan, arrow-key pan, wheel-zoom-to-cursor, spacebar recenter, bounds clamp (no black).
- Player: WASD step, double-click auto-move, pathfinding around water; animation faces movement.
- Networking: `create lobby`/`host` prints a code; `join <code>` connects a second client.
- Console: Enter opens; valid command runs; invalid flashes/shakes; chat popup colors by type; typing suppresses player/camera input (the `InputState` flag).
- Tuner: biome/ground sliders regenerate live; water sliders apply live; Save persists across a Play stop; Reset restores defaults.
Exit Play.

- [ ] **Step 4: Commit**

```bash
git add -A "Assets/_Scripts"
git commit -m "Split GridManager into Game entry + World/WorldView/WaterMaterial subsystems"
```

---

## Self-review (completed by plan author)

**Spec coverage:**
- Category folders → Phase 2 (Tasks 2.1-2.6). ✓
- Single entry `Game` (boot story + Update + facade) → Task 3.5. ✓
- `World` pipeline (in/out) → Task 3.2. ✓
- `WorldView` streamer → Task 3.3. ✓
- `JsonPref<T>` DRY → Phase 1 (reused in Task 3.5). ✓
- `WaterMaterial` → Task 3.4. ✓
- Deletions: `GridServer.cs` (0.1), `Biomes` static + `Biome` enum (0.2), dead knobs + sliders (0.2/0.5), climate PASS 2 collapse (0.2-0.4). ✓
- Surviving tuner knobs match the trimmed `BiomeSettings` (Task 0.2 / 0.5). ✓
- Phasing with verify gates → Phases 0-3 each end in a verify+commit task. ✓
- GUID-safe moves → Pre-flight note + Tasks 2.2/3.5. ✓
- `Game` rename, field names preserved → Task 3.5 Step 1-2, Task 3.7 Step 1. ✓
- Layering ("nothing depends on UI") → `InputState` decouples Camera/Player from `CommandConsole` (Task 3.1). ✓ (an addition beyond the spec text, required to make the stated layering rule true)

**Placeholder scan:** No TBD/TODO; every code step shows complete code; every edit shows old→new. ✓

**Type consistency:** `BiomeGenerator.IsLand` (0.2) is consumed as `Func<int,int,bool>` by `CellRenderer` (0.3), `GridManager.GenAt` (0.4), and `World.IsLand` (3.2) → `WorldView` (3.3). `World.LandSprite`/`LandColor` signatures match `CellRenderer.BuildWindowMesh`/`BuildOverviewMinimap`. `JsonPref<T>` methods (`Save`/`Load`/`Clear`/`Reset`) match all call sites in Phase 1 and Task 3.5. `Game` facade exposes every member the consumers in Task 3.6 call. ✓

## Out of scope (future)

- **Extract `LineEditor`** from the 454-line `CommandConsole` (caret/selection/clipboard/word-nav). Worthwhile, riskiest UI surgery — defer.
- **asmdefs** (compiler-enforced boundaries). Folders map 1:1 to asmdefs; layer on later if compile times or boundary slips warrant it.
