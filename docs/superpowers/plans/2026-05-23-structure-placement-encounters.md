# Structure Placement + ZORK Encounters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Place named structure sites (forest village/home/town/mansion, farm, camp, walled town) across the infinite world with a deterministic noise layer, and let the player trigger a ZORK-style text encounter (walk on → lock → `enter`/`leave` → greet) by walking onto one.

**Architecture:** A third deterministic generator (`StructureGenerator`, jittered region grid) sits beside the existing land/ground generators in the pure `World` query layer; `World.LandSprite`/`LandColor` consult it so sites render on the existing mesh + minimap with **zero renderer changes**. The encounter loop reuses the already-scaffolded command system (`CommandScope.Encounter`, `CommandRouter.Enter/ExitEncounter`, `CommandRegistry.Suggest`, `InputState.Typing` movement gate); a new `EncounterManager` detects walk-on and drives a locked `CommandConsole`. Everything is local + deterministic-from-seed except one `HaltServerRpc`.

**Tech Stack:** Unity 2022+/C#, Unity Netcode for GameObjects, the project's `Minifantasy.Commands` asmdef + EditMode test harness, unity-mcp for compile/play verification.

**Refinement vs spec:** The spec (§3.2) had `StructureSite` carry the chosen sprite. This plan instead lets `World` pick the sprite variant (so the summer-sheet foreign-texture validation in `World.IsUsable` lives in one place). `StructureSite` carries `Def`/`Cell`/`Name` only.

**Conventions for every task:**
- **Compile check** = via unity-mcp: `mcp__unity-mcp__refresh_unity`, then `mcp__unity-mcp__read_console` filtered to errors → expect none. Poll the `editor_state` resource `isCompiling` until false. NOTE: `execute_code` is broken on this machine — do not use it.
- **Commits** use plain messages — **no Claude attribution** (no `Co-Authored-By`, no "Generated with"). Stage new scripts together with their generated `.meta` files (Unity writes the `.meta` on refresh).

---

## Part A — Placement

### Task 1: `StructureSettings` (live-tunable knobs)

**Files:**
- Create: `Assets/_Scripts/World/StructureSettings.cs`

- [ ] **Step 1: Create the settings class**

```csharp
using UnityEngine;

// Live-tunable knobs for structure-site placement (serialized on Game; persisted via JsonPref like the
// biome/ground/water settings). Sites sit on a jittered region grid: the world is divided into
// blockSize x blockSize cell blocks, each of which deterministically rolls AT MOST one site.
[System.Serializable]
public class StructureSettings
{
    [Min(2)] public int blockSize = 14;                          // region block edge in cells (smaller = denser)
    [Range(0f, 1f)] public float siteChance = 0.6f;             // chance a block contains a site
    public Color markerColor = new Color(1f, 0.85f, 0.2f, 1f);  // minimap dot color for a site cell
}
```

- [ ] **Step 2: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/World/StructureSettings.cs" "Assets/_Scripts/World/StructureSettings.cs.meta"
git commit -m "Add StructureSettings (placement knobs)"
```

---

### Task 2: `StructureSet` + `StructureDef` (data asset types)

**Files:**
- Create: `Assets/_Scripts/World/StructureSet.cs`

- [ ] **Step 1: Create the ScriptableObject + def**

```csharp
using UnityEngine;

// Data-only asset: the catalog of structure types that can be placed on the map (forest town, farm, camp,
// ...). One asset for the whole world; you curate the sprite variants per type in the Inspector. Holds NO
// logic. All variant sprites must be slices of the summer sheet (same shared-material constraint as biome
// tiles); World validates this and falls back to plain ground for unassigned/foreign sprites.
[CreateAssetMenu(fileName = "Structures", menuName = "World/Structure Set")]
public class StructureSet : ScriptableObject
{
    public StructureDef[] defs;
    [Tooltip("Shared pool of site names; one is picked deterministically per site.")]
    public string[] namePool;
}

// One placeable structure type. `variants` reuses the biome weighted-variant type: each entry is a weighted
// summer-sheet slice; "(2 variations)" = 2 entries, "(4 variations)" = 4.
[System.Serializable]
public class StructureDef
{
    [Tooltip("Stable id, e.g. forest_town — used later as the town-memory key.")]
    public string id;
    [Tooltip("Shown in encounter text, e.g. 'the town', 'a camp'.")]
    public string label;
    [Tooltip("Ground-cover types this structure may spawn on (land only).")]
    public GroundType[] validOn;
    [Min(0f)] public float spawnWeight = 1f;
    public BiomeTileVariant[] variants;
}
```

- [ ] **Step 2: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/World/StructureSet.cs" "Assets/_Scripts/World/StructureSet.cs.meta"
git commit -m "Add StructureSet/StructureDef data asset types"
```

---

### Task 3: `StructureGenerator` + `StructureSite` (deterministic placement logic)

**Files:**
- Create: `Assets/_Scripts/World/StructureGenerator.cs`

- [ ] **Step 1: Create the generator and the site value object**

```csharp
using System;
using UnityEngine;

// Deterministic structure-site placement on a jittered region grid: the world is divided into
// blockSize x blockSize cell blocks; each block hashes to AT MOST one site at a jittered cell inside it.
// SiteAt(cx,cy) computes only the containing block's single candidate, so it is O(1) per cell. Pure and
// seed-driven -> every Netcode client agrees with no replication. Picks the structure TYPE + NAME here;
// World picks the sprite variant (so summer-sheet validation lives in one place, in World.IsUsable).
public sealed class StructureGenerator
{
    const int SaltPresence = 50001, SaltX = 50002, SaltY = 50003, SaltType = 50004, SaltName = 50005;

    readonly int seed, blockSize;
    readonly float siteChance;
    readonly StructureSet set;
    readonly Func<int, int, bool> isLand;
    readonly Func<int, int, GroundType> groundAt;

    public StructureGenerator(int seed, StructureSettings s, StructureSet set,
                              Func<int, int, bool> isLand, Func<int, int, GroundType> groundAt)
    {
        this.seed = seed;
        this.blockSize = Mathf.Max(2, s.blockSize);
        this.siteChance = Mathf.Clamp01(s.siteChance);
        this.set = set;
        this.isLand = isLand;
        this.groundAt = groundAt;
    }

    // The site occupying cell (cx,cy), or null. Only the containing block's single candidate can match.
    public StructureSite SiteAt(int cx, int cy)
    {
        if (set == null || set.defs == null || set.defs.Length == 0) return null;

        int bx = FloorDiv(cx, blockSize), by = FloorDiv(cy, blockSize);
        if (Hash01(bx, by, SaltPresence) >= siteChance) return null;

        int sx = bx * blockSize + (int)(Hash01(bx, by, SaltX) * blockSize);
        int sy = by * blockSize + (int)(Hash01(bx, by, SaltY) * blockSize);
        if (sx != cx || sy != cy) return null;
        if (!isLand(sx, sy)) return null;

        var def = PickDef(groundAt(sx, sy), sx, sy);
        if (def == null) return null;

        return new StructureSite(def, new Vector2Int(sx, sy), PickName(sx, sy));
    }

    StructureDef PickDef(GroundType gt, int x, int y)
    {
        float total = 0f;
        for (int i = 0; i < set.defs.Length; i++)
            if (Accepts(set.defs[i], gt)) total += Mathf.Max(0f, set.defs[i].spawnWeight);
        if (total <= 0f) return null;

        float r = Hash01(x, y, SaltType) * total;
        for (int i = 0; i < set.defs.Length; i++)
        {
            if (!Accepts(set.defs[i], gt)) continue;
            r -= Mathf.Max(0f, set.defs[i].spawnWeight);
            if (r < 0f) return set.defs[i];
        }
        return null;
    }

    static bool Accepts(StructureDef d, GroundType gt)
    {
        if (d == null || d.validOn == null) return false;
        for (int i = 0; i < d.validOn.Length; i++) if (d.validOn[i] == gt) return true;
        return false;
    }

    string PickName(int x, int y)
    {
        if (set.namePool == null || set.namePool.Length == 0) return "an unnamed place";
        int idx = (int)(Hash01(x, y, SaltName) * set.namePool.Length);
        if (idx >= set.namePool.Length) idx = set.namePool.Length - 1;
        return set.namePool[idx];
    }

    // Integer floor division so blocks tile correctly across the origin (C# '/' truncates toward zero).
    static int FloorDiv(int a, int b) => (a >= 0 ? a : a - b + 1) / b;

    // Same hash family as World.Hash01 (kept local so the generator is self-contained, like BiomeGenerator).
    float Hash01(int x, int y, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791) ^ (uint)(salt * 2654435761);
            h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}

// A placed structure site: which type, where, and its deterministic name. World picks the sprite variant.
public sealed class StructureSite
{
    public readonly StructureDef Def;
    public readonly Vector2Int Cell;
    public readonly string Name;
    public StructureSite(StructureDef def, Vector2Int cell, string name) { Def = def; Cell = cell; Name = name; }
}
```

- [ ] **Step 2: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/World/StructureGenerator.cs" "Assets/_Scripts/World/StructureGenerator.cs.meta"
git commit -m "Add StructureGenerator (jittered-grid site placement) + StructureSite"
```

---

### Task 4: Wire the generator into `World` (render override + minimap + public query)

**Files:**
- Modify: `Assets/_Scripts/World/World.cs`

- [ ] **Step 1: Add the two config fields to `WorldConfig`**

In `WorldConfig` (top of the file), add after `public BiomeTiles grass, forest, rocky, mountain;`:

```csharp
    public StructureSet structures;
    public StructureSettings structureSettings;
```

- [ ] **Step 2: Add the generator field**

In class `World`, after `GroundGenerator groundGen;`, add:

```csharp
    StructureGenerator structureGen;
```

- [ ] **Step 3: Build the generator in `Rebuild()`**

Replace the body of `Rebuild()` with:

```csharp
    public void Rebuild()
    {
        gen = new BiomeGenerator(cfg.biome);
        groundGen = new GroundGenerator(cfg.biome.seed, cfg.ground);
        structureGen = new StructureGenerator(cfg.biome.seed, cfg.structureSettings ?? new StructureSettings(),
                                              cfg.structures, IsLand, (x, y) => groundGen.At(x, y));
        landCache.Clear();
    }
```

- [ ] **Step 4: Add the public `SiteAt` query**

After `IsWalkable`, add:

```csharp
    // The structure site occupying a cell, or null. Public so the encounter layer can ask "am I on a site?".
    public StructureSite SiteAt(int cx, int cy) => structureGen != null ? structureGen.SiteAt(cx, cy) : null;
```

- [ ] **Step 5: Make `LandSprite` consult the site first**

Replace `LandSprite` with:

```csharp
    public Sprite LandSprite(int cx, int cy)
    {
        var site = SiteAt(cx, cy);
        if (site != null)
        {
            var s = PickVariant(site.Def.variants, cx, cy, 7777);
            if (s != null) return s;
            // structure art unassigned/foreign -> render as normal ground (the site still triggers encounters)
        }
        var gt = groundGen.At(cx, cy);
        if (Hash01(cx, cy, cfg.biome.seed, 1009 + (int)gt) >= CoverageFor(gt)) return cfg.defaultGroundSprite;
        var picked = PickVariant(BiomeFor(gt)?.variants, cx, cy, (int)gt);
        return picked != null ? picked : cfg.defaultGroundSprite;
    }
```

- [ ] **Step 6: Make `LandColor` mark sites**

Replace `LandColor` with:

```csharp
    public Color32 LandColor(int cx, int cy)
    {
        if (cfg.structureSettings != null && SiteAt(cx, cy) != null) return (Color32)cfg.structureSettings.markerColor;
        var gt = groundGen.At(cx, cy);
        if (Hash01(cx, cy, cfg.biome.seed, 1009 + (int)gt) >= CoverageFor(gt)) return cfg.defaultGroundColor;
        var b = BiomeFor(gt);
        return b != null ? (Color32)b.minimapColor : cfg.defaultGroundColor;
    }
```

- [ ] **Step 7: Generalize `PickVariant` to take a variant array**

Replace the existing `PickVariant(BiomeTiles b, ...)` method with this array-based version (both call sites above already pass `...variants`):

```csharp
    // Weighted-random variant pick made deterministic by hashing (cell, seed, salt) -> every client agrees.
    Sprite PickVariant(BiomeTileVariant[] variants, int cx, int cy, int salt)
    {
        if (variants == null) return null;
        float total = 0f;
        for (int i = 0; i < variants.Length; i++) if (IsUsable(variants[i])) total += variants[i].weight;
        if (total <= 0f) return null;
        float r = Hash01(cx, cy, cfg.biome.seed, salt) * total;
        for (int i = 0; i < variants.Length; i++)
        {
            if (!IsUsable(variants[i])) continue;
            r -= variants[i].weight;
            if (r < 0f) return variants[i].sprite;
        }
        for (int i = variants.Length - 1; i >= 0; i--) if (IsUsable(variants[i])) return variants[i].sprite;
        return null;
    }
```

- [ ] **Step 8: Compile check** — `refresh_unity` → `read_console` (errors) → expect none. (Watch for any other caller of the old `PickVariant(BiomeTiles, …)` signature — there should be none beyond the two updated above.)

- [ ] **Step 9: Commit**

```bash
git add "Assets/_Scripts/World/World.cs"
git commit -m "World: render structure sites over ground + minimap markers + SiteAt query"
```

---

### Task 5: Wire into `Game` (serialized fields, persistence, Save/Reset)

**Files:**
- Modify: `Assets/_Scripts/Game.cs`

- [ ] **Step 1: Add serialized fields**

After the `[Header("Biome tiles ...")]` block (just before `[Header("Camera")]`), add:

```csharp
    [Header("Structures")]
    [SerializeField] StructureSet structures;
    [SerializeField] StructureSettings structureSettings = new StructureSettings();
```

- [ ] **Step 2: Add the live accessor**

Next to `public WaterSettings Water => water;`, add:

```csharp
    public StructureSettings Structure => structureSettings;
```

- [ ] **Step 3: Add the JsonPref field**

Next to `readonly JsonPref<WaterSettings> waterPref = new("water.json");`, add:

```csharp
    readonly JsonPref<StructureSettings> structurePref = new("structures.json");
```

- [ ] **Step 4: Load saved settings in `Awake`**

Change the load line `biomePref.Load(biome); groundPref.Load(ground); waterPref.Load(water);` to:

```csharp
        biomePref.Load(biome); groundPref.Load(ground); waterPref.Load(water); structurePref.Load(structureSettings);
```

- [ ] **Step 5: Pass into `WorldConfig`**

In the `new World(new WorldConfig { ... })` initializer, add to the object initializer:

```csharp
            structures = structures, structureSettings = structureSettings,
```

- [ ] **Step 6: Add Save/Reset methods**

After `ResetWaterSettings()`, add:

```csharp
    public void SaveStructureSettings()  { structurePref.Save(structureSettings); Debug.Log("[Game] Structure settings saved."); }
    public void ResetStructureSettings() { structurePref.Reset(structureSettings, new StructureSettings()); Regenerate(); Debug.Log("[Game] Saved structure settings cleared (defaults applied)."); }
```

- [ ] **Step 7: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 8: Commit**

```bash
git add "Assets/_Scripts/Game.cs"
git commit -m "Game: serialize + persist structure settings; pass into World"
```

---

### Task 6: Density panel in `TunerPanels`

**Files:**
- Modify: `Assets/_Scripts/UI/TunerPanels.cs`

- [ ] **Step 1: Register the panel**

In `Build()`, add to the list after the Water panel entry:

```csharp
        new Panel("Structures",    StructureBody, () => gm.Regenerate()),
```

- [ ] **Step 2: Add the body method**

After `WaterBody()`, add:

```csharp
    void StructureBody()
    {
        var s = gm.Structure;
        I("Block size (smaller=denser)", ref s.blockSize, 2, 40);
        F("Site chance", ref s.siteChance, 0f, 1f, "0.00");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save")) gm.SaveStructureSettings();
        if (GUILayout.Button("Reset")) gm.ResetStructureSettings();
        GUILayout.EndHorizontal();
    }
```

- [ ] **Step 3: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/UI/TunerPanels.cs"
git commit -m "TunerPanels: add Structures density panel"
```

---

### Task 7: Create + scaffold the `StructureSet` asset; assign it; curate sprites (manual)

**Files:**
- Create: `Assets/_Structures/Structures.asset`
- Configure: the `Game` component's `Structures` field in the scene.

This is a Unity-editor data task, not code. The encounter logic works even before sprites are assigned (sites render as plain ground), so sprite curation can lag.

- [ ] **Step 1: Create the asset** — In the Project window: `Assets/_Structures/` → Create → `World/Structure Set`, name it `Structures`. (Or via `mcp__unity-mcp__manage_scriptable_object` create at `Assets/_Structures/Structures.asset`, type `StructureSet`.)

- [ ] **Step 2: Fill `defs` (7 entries)** with these exact values (leave `variants` for Ryan):

| id | label | validOn | spawnWeight | variants to assign |
|----|-------|---------|-------------|--------------------|
| forest_village | the village | Forest | 1 | 1 |
| forest_home | a home | Forest | 1 | 2 |
| forest_town | the town | Forest | 1 | 2 |
| forest_mansion | the mansion | Forest | 1 | 1 |
| farm | a farm | Grass | 1 | 2 |
| camp | a camp | Grass, Forest | 1 | 2 |
| walled_town | the walled town | Grass, Forest | 1 | 4 |

- [ ] **Step 3: Fill `namePool`** with a starter list (edit freely):

```
Oakhollow, Mossfen, Thornwood, Elderbrook, Ashvale, Brackenford, Willowmere, Duskwood, Fernhollow, Greystead
```

- [ ] **Step 4: Assign the asset** — select the `Game` object in the scene, drag `Structures.asset` into its **Structures** field. (Or `mcp__unity-mcp__manage_components` to set `Game.structures`.)

- [ ] **Step 5: (Ryan) Curate sprites** — for each def, add the listed number of `variants`, each a slice of `world_map_tiles_SUMMER` (full tiles, not transparent icons), weight 1. Foreign/unassigned slices are safely ignored (render as ground).

- [ ] **Step 6: Commit**

```bash
git add "Assets/_Structures.meta" "Assets/_Structures/Structures.asset" "Assets/_Structures/Structures.asset.meta" "Assets/Scenes/SampleScene.unity"
git commit -m "Add Structures asset (7 defs + name pool); assign to Game"
```

---

### Task 8: Part A verification — debug `sites` command + play-mode

**Files:**
- Modify: `Assets/_Scripts/CommandBootstrap.cs`

- [ ] **Step 1: Add a debug `sites` command**

In `EnsureInstalled()`, after the `inventory` registration, add:

```csharp
        // ---- Debug ----
        r.Register(new Command
        {
            Keyword = "sites", Scope = CommandScope.World, Arg = ArgMode.None,
            Description = "(debug) Report the nearest structure site to you.",
            Run = _ => CommandResult.Ok(NearestSite(), keepOpen: true, output: OutputType.System),
        });
```

- [ ] **Step 2: Add the helper**

After the `Net()` helper method, add:

```csharp
    static string NearestSite()
    {
        var gm = Game.Instance; var lp = PlayerMovement.LocalInstance;
        if (gm == null || gm.World == null || lp == null) return "No world/player.";
        var c = lp.CurrentCell();
        const int R = 80;
        StructureSite best = null; int bestD = int.MaxValue;
        for (int dx = -R; dx <= R; dx++)
        for (int dy = -R; dy <= R; dy++)
        {
            var s = gm.World.SiteAt(c.x + dx, c.y + dy);
            if (s == null) continue;
            int d = Mathf.Abs(dx) + Mathf.Abs(dy);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best == null ? $"No sites within {R} cells."
                            : $"Nearest: {best.Name} ({best.Def.label}) at ({best.Cell.x},{best.Cell.y}), {bestD} cells away.";
    }
```

(Add `using UnityEngine;` is already present at the top of the file.)

- [ ] **Step 3: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 4: Play-mode verify** — `mcp__unity-mcp__manage_editor` enter Play. Observe: the minimap shows yellow site dots scattered (~one per ~14-cell block on suitable terrain). Open the console (Enter), type `sites`, submit → a "Nearest: …" line appears in the popup with a name + label + coords. If sprites are assigned, sites also appear as distinct tiles on the map. `read_console` → no errors. Exit Play.

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/CommandBootstrap.cs"
git commit -m "Add debug 'sites' command for placement verification"
```

---

## Part B — Encounter loop

### Task 9: `CommandRegistry.Suggest` autocomplete-rule tests (EditMode)

These characterize the unique-prefix rule the console UI will depend on. `Suggest` already implements it, so they pass immediately — they lock the behavior before we surface it.

**Files:**
- Modify: `Assets/Tests/EditMode/CommandRegistryTests.cs`

- [ ] **Step 1: Add the test cases**

Before the closing `}` of the class, add:

```csharp
    [Test]
    public void Suggest_AmbiguousPrefix_ReturnsEmpty()
    {
        var r = MakeRegistry();
        r.Register(new Command { Keyword = "attune", Scope = CommandScope.Encounter, Arg = ArgMode.None, Run = _ => CommandResult.Ok() });
        Assert.AreEqual("", r.Suggest("a", Combat));   // "attack" and "attune" both match -> ambiguous
    }

    [Test]
    public void Suggest_ExactFullWord_ReturnsEmpty()
    {
        Assert.AreEqual("", MakeRegistry().Suggest("attack", Combat));   // already complete
    }

    [Test]
    public void Suggest_EmptyInput_ReturnsEmpty()
    {
        Assert.AreEqual("", MakeRegistry().Suggest("", Combat));
    }
```

- [ ] **Step 2: Run the EditMode tests** — `mcp__unity-mcp__run_tests` (mode: EditMode, filter: `CommandRegistryTests`). Expected: all pass (existing + 3 new).

- [ ] **Step 3: Commit**

```bash
git add "Assets/Tests/EditMode/CommandRegistryTests.cs"
git commit -m "Test: Suggest hides completion on ambiguous/exact/empty input"
```

---

### Task 10: Inline ghost autocomplete + Tab in `CommandConsole`

**Files:**
- Modify: `Assets/_Scripts/UI/CommandConsole.cs`

- [ ] **Step 1: Render the ghost completion**

In `Render()`, replace the no-selection `else` branch:

```csharp
        else
        {
            bool caretOn = ((int)(Time.unscaledTime / 0.5f) & 1) == 0;
            string caretGlyph = caretOn ? "<color=#FFFFFFFF>|</color>" : "<color=#FFFFFF00>|</color>";
            label.text = prompt + text.Substring(0, caret) + caretGlyph + text.Substring(caret);
            if (highlightRect != null) highlightRect.gameObject.SetActive(false);
        }
```

with:

```csharp
        else
        {
            bool caretOn = ((int)(Time.unscaledTime / 0.5f) & 1) == 0;
            string caretGlyph = caretOn ? "<color=#FFFFFFFF>|</color>" : "<color=#FFFFFF00>|</color>";
            string ghost = caret == text.Length ? CommandRouter.Instance.Suggest(text) : "";   // only complete at line end
            string ghostGlyph = string.IsNullOrEmpty(ghost) ? "" : "<color=#FFFFFF55>" + ghost + "</color>";
            label.text = prompt + text.Substring(0, caret) + caretGlyph + text.Substring(caret) + ghostGlyph;
            if (highlightRect != null) highlightRect.gameObject.SetActive(false);
        }
```

- [ ] **Step 2: Accept the ghost on Tab**

In `Update()`, inside the `open` branch, add a Tab handler right after the `if (EnterPressed(kb)) { Confirm(); return; }` line:

```csharp
        if (kb.tabKey.wasPressedThisFrame) { AcceptGhost(); return; }
```

- [ ] **Step 3: Add the `AcceptGhost` helper**

After the `Suggest` seam method near the bottom, add:

```csharp
    void AcceptGhost()
    {
        string ghost = CommandRouter.Instance.Suggest(text);
        if (!string.IsNullOrEmpty(ghost)) { Insert(ghost); Render(); }
    }
```

- [ ] **Step 4: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 5: Play-mode verify** — enter Play, open the console (Enter), type `he` → a gray `lp` ghost appears; press Tab → becomes `help`. Type a single ambiguous letter that prefixes two active commands → no ghost. Exit Play.

- [ ] **Step 6: Commit**

```bash
git add "Assets/_Scripts/UI/CommandConsole.cs"
git commit -m "CommandConsole: inline ghost autocomplete + Tab to accept"
```

---

### Task 11: Locked-open mode in `CommandConsole`

**Files:**
- Modify: `Assets/_Scripts/UI/CommandConsole.cs`

- [ ] **Step 1: Add the lock flag + public open/unlock**

After the field declarations (e.g. after `bool open;`), add:

```csharp
    public bool LockOpen { get; set; }   // while true: Esc / click-out / empty-Enter won't close (only a command can)
```

After the private `Open()` method, add:

```csharp
    // Open the console and trap it open (used by encounters). Only a command that clears LockOpen can close it.
    public void OpenLocked() { if (!open) Open(); LockOpen = true; }
    public void Unlock() => LockOpen = false;
```

- [ ] **Step 2: Guard Escape** — in `Update()`, change `if (kb.escapeKey.wasPressedThisFrame) { Close(); return; }` to:

```csharp
        if (kb.escapeKey.wasPressedThisFrame) { if (!LockOpen) Close(); return; }
```

- [ ] **Step 3: Guard empty-Enter** — in `Confirm()`, change `if (submitted.Length == 0) { Close(); return; }` to:

```csharp
        if (submitted.Length == 0) { if (!LockOpen) Close(); return; }   // empty Enter cancels (unless locked)
```

- [ ] **Step 4: Guard click-outside** — in `HandleMouse()`, change the outside-click block:

```csharp
            if (panelRect == null || !RectTransformUtility.RectangleContainsScreenPoint(panelRect, mp, null))
            {
                Close();
                return true;
            }
```

to:

```csharp
            if (panelRect == null || !RectTransformUtility.RectangleContainsScreenPoint(panelRect, mp, null))
            {
                if (!LockOpen) { Close(); return true; }
                return false;   // locked: ignore clicks outside the panel
            }
```

- [ ] **Step 5: Clear the lock in `Close()`** — at the top of `Close()`, add `LockOpen = false;` so a normal close can never leave a stale lock:

```csharp
    void Close()
    {
        LockOpen = false;
        open = false; InputState.Typing = false;
        text = ""; caret = anchor = 0;
        dragging = false;
        ResetVisuals();
    }
```

- [ ] **Step 6: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 7: Commit**

```bash
git add "Assets/_Scripts/UI/CommandConsole.cs"
git commit -m "CommandConsole: locked-open mode (Esc/click-out/empty-Enter ignored)"
```

---

### Task 12: `PlayerMovement.Halt()` + `HaltServerRpc`

**Files:**
- Modify: `Assets/_Scripts/Player/PlayerMovement.cs`

- [ ] **Step 1: Add the halt API**

After `CurrentCell()`, add:

```csharp
    // Stop all movement now (used when an encounter begins): cancel WASD intent and ask the server to drop
    // any click-to-move path, so the player can't drift off the encounter tile. An in-flight step finishes.
    public void Halt()
    {
        if (!IsSpawned) return;
        if (IsOwner) moveInput.Value = Vector2.zero;
        HaltServerRpc();
    }

    [ServerRpc]
    void HaltServerRpc()
    {
        motion.hasTarget = false;
        motion.path.Clear();
        motion.pathIndex = 0;
    }
```

- [ ] **Step 2: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/Player/PlayerMovement.cs"
git commit -m "PlayerMovement: Halt() clears intent + server path (encounter lock)"
```

---

### Task 13: `EncounterManager` + boot wiring

**Files:**
- Create: `Assets/_Scripts/EncounterManager.cs`
- Modify: `Assets/_Scripts/Game.cs`

- [ ] **Step 1: Create the manager**

```csharp
using UnityEngine;

// The encounter driver (local client only): watches the owning player's cell and, when they step onto a
// structure site, halts movement, swaps the command set to the Encounter scope, and opens the command
// console LOCKED with an intro line. The `enter`/`leave` commands (CommandBootstrap) read Current and call
// End() to finish. v1 is fully local + deterministic; only PlayerMovement.Halt touches the network. Added
// to the Game object at boot (like TunerPanels).
public sealed class EncounterManager : MonoBehaviour
{
    public static EncounterManager Instance { get; private set; }

    public StructureSite Current { get; private set; }
    public bool InEncounter => Current != null;

    CommandConsole console;
    Vector2Int lastCell;
    bool haveLast;

    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

    void Update()
    {
        var lp = PlayerMovement.LocalInstance;
        var world = Game.Instance != null ? Game.Instance.World : null;
        if (lp == null || world == null) return;

        var cell = lp.CurrentCell();
        if (!haveLast) { lastCell = cell; haveLast = true; return; }   // don't trigger on the spawn cell

        if (!InEncounter && cell != lastCell)
        {
            var site = world.SiteAt(cell.x, cell.y);
            if (site != null) Begin(lp, site);
        }
        lastCell = cell;
    }

    void Begin(PlayerMovement lp, StructureSite site)
    {
        Current = site;
        lp.Halt();
        CommandRouter.Instance.EnterEncounter();
        if (console == null) console = FindFirstObjectByType<CommandConsole>();
        if (console != null) console.OpenLocked();
        GameLog.Post(OutputType.Encounter, $"You approach {site.Name}, {site.Def.label}. (enter / leave)");
    }

    // Called by the `leave` command. Clears the lock BEFORE the command result closes the console.
    public void End()
    {
        if (!InEncounter) return;
        Current = null;
        CommandRouter.Instance.ExitEncounter();
        if (console != null) console.Unlock();
        // lastCell is still the site cell, so we won't re-trigger until the player steps off and back on.
    }
}
```

- [ ] **Step 2: Add it at boot**

In `Game.Awake()`, after `gameObject.AddComponent<TunerPanels>();`, add:

```csharp
        gameObject.AddComponent<EncounterManager>();   // walk-on encounter driver
```

- [ ] **Step 3: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/EncounterManager.cs" "Assets/_Scripts/EncounterManager.cs.meta" "Assets/_Scripts/Game.cs"
git commit -m "Add EncounterManager (walk-on detection, lock, intro) + boot wiring"
```

---

### Task 14: `enter`/`leave` commands; remove demo `fight`/`attack`/`flee`

**Files:**
- Modify: `Assets/_Scripts/CommandBootstrap.cs`

- [ ] **Step 1: Remove the demo combat commands**

Delete the `fight` registration (in the World block) and the entire `// ---- Encounter (combat; ...)` block containing `attack` and `flee`. (The real encounter loop replaces them; `CommandRouter.Enter/ExitEncounter` stay — `EncounterManager` uses them.)

- [ ] **Step 2: Add the encounter commands**

Where the removed Encounter block was, add:

```csharp
        // ---- Encounter (entering a structure site; only available while standing in one) ----
        r.Register(new Command
        {
            Keyword = "enter", Scope = CommandScope.Encounter, Arg = ArgMode.None,
            Description = "Enter the place you're standing at.",
            Run = _ =>
            {
                var e = EncounterManager.Instance;
                string name = e != null && e.Current != null ? e.Current.Name : "the place";
                return CommandResult.Ok($"A villager greets you to {name}. \"Welcome, traveler.\"", keepOpen: true, output: OutputType.Encounter);
            },
        });
        r.Register(new Command
        {
            Keyword = "leave", Scope = CommandScope.Encounter, Arg = ArgMode.None,
            Description = "Leave and return to the world.",
            Run = _ => { EncounterManager.Instance?.End(); return CommandResult.Ok("You leave.", keepOpen: false, output: OutputType.Encounter); },
        });
```

- [ ] **Step 3: Compile check** — `refresh_unity` → `read_console` (errors) → expect none.

- [ ] **Step 4: Re-run EditMode tests** — `mcp__unity-mcp__run_tests` (EditMode, `CommandRegistryTests`) → still all pass (the tests use their own registry, unaffected).

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/CommandBootstrap.cs"
git commit -m "Commands: replace demo combat verbs with enter/leave encounter commands"
```

---

### Task 15: Full encounter-loop play-mode verification

No code — end-to-end check of the vertical slice.

- [ ] **Step 1:** `refresh_unity`; confirm no console errors. Enter Play (`manage_editor`). Host a session (type `create lobby` / `host`, or however a local player is spawned in this scene).

- [ ] **Step 2: Find a site** — type `sites` to get the nearest site's coords (and watch the minimap dots). Walk there with WASD.

- [ ] **Step 3: Trigger** — on stepping onto the site cell, expect: the player stops, the console opens (and stays open), and the popup shows `You approach {name}, {label}. (enter / leave)`.

- [ ] **Step 4: Lock holds** — press Esc and click outside the console: it must NOT close. Try WASD: the player must NOT move.

- [ ] **Step 5: Autocomplete + enter** — type `ent` → gray `er` ghost; Tab → `enter`; Enter → `A villager greets you to {name}. "Welcome, traveler."`.

- [ ] **Step 6: Leave** — type `le` → ghost `ave`; Enter `leave` → `You leave.`; the console closes and WASD moves the player again.

- [ ] **Step 7: Re-arm** — step off the site cell and back on → the encounter triggers again. `read_console` → no errors. Exit Play.

- [ ] **Step 8 (optional): tune** — open the Structures tuner panel; adjust Block size / Site chance; confirm site density changes live; Save.

---

## Self-Review

**Spec coverage:**
- §3.1 data (StructureSet/Def/Settings) → Tasks 1, 2. ✓
- §3.2 StructureGenerator/SiteAt → Task 3. ✓
- §3.3 World render override + minimap + SiteAt → Task 4. ✓
- §3.4 Game wiring + persistence + tuner → Tasks 5, 6. ✓
- §3.5 default defs/affinities + asset → Task 7. ✓
- §4.1 EncounterManager → Task 13. ✓
- §4.2 enter/leave commands → Task 14. ✓
- §4.3 Halt RPC → Task 12. ✓
- §4.4 ghost autocomplete + locked mode → Tasks 10, 11. ✓
- §4.5 naming → Task 3 (`PickName`) + Task 7 (namePool). ✓
- §7 tests/verification → Task 9 (Suggest tests), Tasks 8 & 15 (play-mode), Task 8 (`sites` debug). ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code; the only manual task (7) is a Unity data step with an exact value table.

**Type consistency:** `StructureSite.Def/Cell/Name`; `StructureDef.id/label/validOn/spawnWeight/variants`; `StructureSettings.blockSize/siteChance/markerColor`; `World.SiteAt`; `Game.Structure/SaveStructureSettings/ResetStructureSettings`; `CommandConsole.OpenLocked/Unlock/LockOpen`; `PlayerMovement.Halt`; `EncounterManager.Instance/Current/End` — all referenced consistently across tasks.
