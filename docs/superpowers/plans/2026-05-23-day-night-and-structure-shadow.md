# Day/Night Cycle + Structure Floor Shadow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a server-synced day/night cycle (global color tint over terrain, water, and the player body) and a fixed darkened floor shadow under structure tiles.

**Architecture:** A `DayNightSystem` (logic, ticked by `Game`) derives `timeOfDay` from network/local time and writes a `DayNightState{timeOfDay,tint}`. `DayNightView` pushes the tint into the terrain material (`_Color`) and the water shader (a new `_DayTint` uniform); `PlayerView` tints the player's body sprites. The structure shadow is a darkened, offset copy of the site's own tile, emitted into the existing terrain mesh by `CellRenderer`. No URP 2D lights, no lit-material conversion, no character-shadow work (the Minifantasy pack already ships one).

**Tech Stack:** Unity 6 / URP 17.3 (2D Renderer), C# (`Assembly-CSharp`), Netcode for GameObjects, IMGUI tuner, custom CG water shader.

**Spec:** `docs/superpowers/specs/2026-05-23-day-night-baked-shadows-design.md`

---

## Testing & verification note

This is a Unity runtime/visual feature. The game assembly (`Assembly-CSharp`) is **not** reachable from the only test assembly (`Minifantasy.Commands.Tests`), and the project deliberately keeps "folders only, no new asmdefs," so classic EditMode unit-test TDD does not apply here. Verification uses the project's established **unity-mcp play-mode loop** plus the debug `time` command this plan adds. Pure clock math lives in an isolated `DayNightMath` static class so it *could* be unit-tested later if a test-reachable assembly is added.

**Verification recipe `V` (referenced by tasks):**
1. `refresh_unity` with `scope=all` (required when new `.cs`/shader files are added, or Unity may compile before importing them).
2. Poll the `editor_state` resource until `isCompiling == false`.
3. `read_console` (errors only) → **expect zero compile errors**.
4. *(play checks only)* `manage_editor` → enter Play; take a camera `screenshot`; observe the stated expectation; exit Play. The MCP screenshot captures the camera, **not** IMGUI tuner panels.

**Commit note:** per the user's commit style, **no Claude attribution** (no `Co-Authored-By`, no "Generated with…"). Stage only the files each task names.

---

# Phase 1 — Day/Night Cycle

## Task 1: Day/night data (settings + state)

**Files:**
- Create: `Assets/_Scripts/Environment/DayNightSettings.cs`
- Create: `Assets/_Scripts/Environment/DayNightState.cs`

- [ ] **Step 1: Create `DayNightSettings.cs`**

```csharp
using UnityEngine;

// Live-tunable day/night knobs (serialized on Game; persisted via JsonPref "daynight.json" like the other
// settings groups). Look-only: a clock length + a 4-stop colour ramp. No sun/shadow fields — nothing is
// sun-driven (the structure shadow is static; the character shadow is the pack's, untouched).
[System.Serializable]
public class DayNightSettings
{
    [Min(1f)] public float cycleSeconds = 480f;                       // full day length (~8 min)
    public Color nightColor = new Color(0.20f, 0.26f, 0.48f, 1f);     // deep dim blue
    public Color dawnColor  = new Color(1.00f, 0.78f, 0.58f, 1f);     // warm
    public Color noonColor  = Color.white;                            // neutral / bright
    public Color duskColor  = new Color(1.00f, 0.62f, 0.40f, 1f);     // orange
    [Range(0.01f, 0.49f)] public float dawnTime = 0.25f;             // colour-stop time for dawn
    [Range(0.51f, 0.99f)] public float duskTime = 0.75f;             // colour-stop time for dusk
}
```

- [ ] **Step 2: Create `DayNightState.cs`**

```csharp
using UnityEngine;

// Data: the computed look for the current instant. DayNightSystem writes it; views read it.
public sealed class DayNightState
{
    public float timeOfDay;        // 0..1 (0 = midnight, 0.5 = noon)
    public Color tint = Color.white;
}
```

- [ ] **Step 3: Verify** — run recipe `V` steps 1–3. Expected: no compile errors. (Nothing references these yet.)

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/Environment/DayNightSettings.cs Assets/_Scripts/Environment/DayNightState.cs
git commit -m "DayNight: settings + state data"
```

---

## Task 2: Day/night clock (system + pure math)

**Files:**
- Create: `Assets/_Scripts/Environment/DayNightSystem.cs`

- [ ] **Step 1: Create `DayNightSystem.cs` (system + `DayNightMath`)**

```csharp
using Unity.Netcode;
using UnityEngine;

// Logic: advances the day/night clock and computes the tint into DayNightState. Time is a pure function of
// network/local seconds, so every client agrees with NO replication. Ticked by Game.Update.
public sealed class DayNightSystem
{
    readonly DayNightSettings s;
    public DayNightState State { get; } = new DayNightState();
    public float? TimeOverride { get; set; }   // debug `time` command; client-local, never replicated

    public DayNightSystem(DayNightSettings settings) { s = settings; Tick(); }

    public void Tick()
    {
        float t = TimeOverride ?? DayNightMath.TimeOfDay(NowSeconds(), s.cycleSeconds);
        State.timeOfDay = t;
        State.tint = DayNightMath.Tint(t, s);
    }

    static double NowSeconds()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsServer || nm.IsConnectedClient)) return nm.ServerTime.Time;
        return Time.unscaledTimeAsDouble;
    }
}

// Pure clock/colour math (no scene deps) — isolated so it is unit-testable if a test-reachable assembly is
// ever added (today Assembly-CSharp is not reachable from the Commands test asmdef).
public static class DayNightMath
{
    public static float TimeOfDay(double seconds, float cycleSeconds)
    {
        if (cycleSeconds <= 0f) return 0f;
        double frac = (seconds / cycleSeconds) % 1.0;
        if (frac < 0.0) frac += 1.0;
        return (float)frac;
    }

    // Piecewise lerp over stops: (0,night) (dawnTime,dawn) (0.5,noon) (duskTime,dusk) (1,night).
    public static Color Tint(float t, DayNightSettings s)
    {
        float dawn = Mathf.Clamp(s.dawnTime, 0.0001f, 0.4999f);
        float dusk = Mathf.Clamp(s.duskTime, 0.5001f, 0.9999f);
        if (t < dawn)  return Color.Lerp(s.nightColor, s.dawnColor, t / dawn);
        if (t < 0.5f)  return Color.Lerp(s.dawnColor,  s.noonColor, (t - dawn) / (0.5f - dawn));
        if (t < dusk)  return Color.Lerp(s.noonColor,  s.duskColor, (t - 0.5f) / (dusk - 0.5f));
        return Color.Lerp(s.duskColor, s.nightColor, (t - dusk) / (1f - dusk));
    }
}
```

- [ ] **Step 2: Verify** — recipe `V` steps 1–3. Expected: no compile errors (`Unity.Netcode` is already a dependency).

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/Environment/DayNightSystem.cs
git commit -m "DayNight: clock system + pure DayNightMath"
```

---

## Task 3: Water shader day/night tint hook

**Files:**
- Modify: `Assets/_Shaders/WaterWaves.shader`

- [ ] **Step 1: Add the `_DayTint` property** — in the `Properties` block, after the `_StyleRow` line:

```hlsl
        _StyleRow ("Water Style Row", Float) = 0
        _DayTint ("Day/Night Tint", Color) = (1,1,1,1)
```

- [ ] **Step 2: Add the uniform** — extend the existing `fixed4` declaration line:

Change:
```hlsl
            fixed4 _Color, _CalmColor;
```
to:
```hlsl
            fixed4 _Color, _CalmColor, _DayTint;
```

- [ ] **Step 3: Multiply both output branches by `_DayTint`** — in `frag`:

Change the calm-water return:
```hlsl
                if (saturate(wave) < _Calm) return _CalmColor * i.color;        // flat calm water
```
to:
```hlsl
                if (saturate(wave) < _Calm) return _CalmColor * i.color * _DayTint;   // flat calm water
```

And change the ripple return:
```hlsl
                return tex2D(_MainTex, uv) * i.color;
```
to:
```hlsl
                return tex2D(_MainTex, uv) * i.color * _DayTint;
```

- [ ] **Step 4: Verify** — recipe `V` steps 1–4. Enter Play; screenshot. Expected: water still renders exactly as before (default `_DayTint` is white = no change), no shader errors in console.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Shaders/WaterWaves.shader
git commit -m "WaterWaves: add _DayTint uniform for the day/night cycle"
```

---

## Task 4: Day/night view + terrain material exposure

**Files:**
- Create: `Assets/_Scripts/Environment/DayNightView.cs`
- Modify: `Assets/_Scripts/World/WorldView.cs:22` and `:34`

- [ ] **Step 1: Create `DayNightView.cs`**

```csharp
using UnityEngine;

// Visual: pushes the day/night tint into the world materials. Terrain uses Sprites/Default's _Color (nothing
// else writes it); water uses a dedicated _DayTint uniform so we don't fight WaterMaterial's wave params.
public sealed class DayNightView
{
    readonly Material terrain, water;
    static readonly int DayTintId = Shader.PropertyToID("_DayTint");

    public DayNightView(Material terrain, Material water) { this.terrain = terrain; this.water = water; }

    public void Apply(DayNightState st)
    {
        if (terrain != null) terrain.color = st.tint;
        if (water != null) water.SetColor(DayTintId, st.tint);
    }
}
```

- [ ] **Step 2: Expose `TerrainMat` on `WorldView`** — add the property next to `WaterMat`:

Change ([WorldView.cs:22](Assets/_Scripts/World/WorldView.cs)):
```csharp
    public Material WaterMat { get; }                       // submesh-1 material; WaterMaterial pushes settings into it
```
to:
```csharp
    public Material WaterMat { get; }                       // submesh-1 material; WaterMaterial pushes settings into it
    public Material TerrainMat { get; }                     // submesh-0 material; DayNightView tints its _Color
```

- [ ] **Step 3: Set `TerrainMat` in the constructor** — change ([WorldView.cs:34](Assets/_Scripts/World/WorldView.cs)):
```csharp
        WaterMat = gridMesh.GetComponent<MeshRenderer>().sharedMaterials[1];
```
to:
```csharp
        var gridMr = gridMesh.GetComponent<MeshRenderer>();
        WaterMat = gridMr.sharedMaterials[1];
        TerrainMat = gridMr.sharedMaterials[0];
```

- [ ] **Step 4: Verify** — recipe `V` steps 1–3. Expected: no compile errors. (Not wired into Game yet.)

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Environment/DayNightView.cs Assets/_Scripts/World/WorldView.cs
git commit -m "DayNight: view that tints terrain + water; expose WorldView.TerrainMat"
```

---

## Task 5: Wire the day/night cycle into Game

**Files:**
- Modify: `Assets/_Scripts/Game.cs` (several anchored edits)

- [ ] **Step 1: Add the serialized settings field** — after the Structures header block ([Game.cs:34](Assets/_Scripts/Game.cs)), before `[Header("Camera")]`:

```csharp
    [Header("Day/Night")]
    [SerializeField] DayNightSettings dayNight = new DayNightSettings();
```

- [ ] **Step 2: Add facade members** — after `public StructureSettings Structure => structureSettings;` ([Game.cs:55](Assets/_Scripts/Game.cs)):

```csharp
    public DayNightState DayNight => dayNightSystem != null ? dayNightSystem.State : null;
    public DayNightSettings DayNightCfg => dayNight;
    public float? TimeOverride => dayNightSystem != null ? dayNightSystem.TimeOverride : null;
    public void SetTimeOverride(float? t) { if (dayNightSystem != null) dayNightSystem.TimeOverride = t; }
```

- [ ] **Step 3: Add the runtime fields** — after `WaterMaterial waterMat;` ([Game.cs:74](Assets/_Scripts/Game.cs)):

```csharp
    DayNightSystem dayNightSystem;
    DayNightView dayNightView;
```

- [ ] **Step 4: Add the JsonPref** — after the `structurePref` line ([Game.cs:79](Assets/_Scripts/Game.cs)):

```csharp
    readonly JsonPref<DayNightSettings> dayNightPref = new("daynight.json");
```

- [ ] **Step 5: Load the saved settings** — change ([Game.cs:86](Assets/_Scripts/Game.cs)):
```csharp
        biomePref.Load(biome); groundPref.Load(ground); waterPref.Load(water); structurePref.Load(structureSettings);
```
to:
```csharp
        biomePref.Load(biome); groundPref.Load(ground); waterPref.Load(water); structurePref.Load(structureSettings);
        dayNightPref.Load(dayNight);
```

- [ ] **Step 6: Build the system + view** — after `waterMat = new WaterMaterial(view.WaterMat, water, cellWorld);` ([Game.cs:114](Assets/_Scripts/Game.cs)):

```csharp
        dayNightSystem = new DayNightSystem(dayNight);
        dayNightView = new DayNightView(view.TerrainMat, view.WaterMat);
```

- [ ] **Step 7: Tick + apply each frame** — at the end of `Update()`, after the `view.Follow(...)` line ([Game.cs:167](Assets/_Scripts/Game.cs)), before the closing brace:

```csharp
        if (dayNightSystem != null) { dayNightSystem.Tick(); dayNightView.Apply(dayNightSystem.State); }
```

- [ ] **Step 8: Add Save/Reset methods** — after `ResetStructureSettings()` ([Game.cs:158](Assets/_Scripts/Game.cs)):

```csharp
    public void SaveDayNightSettings()  { dayNightPref.Save(dayNight); Debug.Log("[Game] Day/Night settings saved."); }
    public void ResetDayNightSettings() { dayNightPref.Reset(dayNight, new DayNightSettings()); Debug.Log("[Game] Day/Night settings reset (defaults applied)."); }
```

- [ ] **Step 9: Verify** — recipe `V` steps 1–4. Enter Play and let it run; screenshot. Expected: no errors; over time the terrain + water tint drifts (full cycle is 8 min, so the shift is slow — Task 6 adds the `time` command to scrub it instantly). The default `noonColor` is white, so if you start mid-cycle it may look near-normal; confirm no errors and that `Game.Instance.DayNight` is non-null.

- [ ] **Step 10: Commit**

```bash
git add Assets/_Scripts/Game.cs
git commit -m "DayNight: build/tick the clock + apply tint from Game; persist daynight.json"
```

---

## Task 6: Debug `time` command (fast verification)

**Files:**
- Modify: `Assets/_Scripts/CommandBootstrap.cs` (add to the Debug section)

- [ ] **Step 1: Register the `time` command** — in `EnsureInstalled()`, after the `sites` command ([CommandBootstrap.cs:70](Assets/_Scripts/CommandBootstrap.cs)):

```csharp
        r.Register(new Command
        {
            Keyword = "time", Scope = CommandScope.World, Arg = ArgMode.Optional,
            Description = "(debug) Show, set (0..1), or resume ('off') the day/night clock.", Usage = "time [0..1|off]",
            Run = arg =>
            {
                var gm = Game.Instance;
                if (gm == null) return CommandResult.Ok("No game.", keepOpen: true, output: OutputType.System);
                if (arg.Length == 0)
                {
                    float now = gm.DayNight != null ? gm.DayNight.timeOfDay : 0f;
                    return CommandResult.Ok($"Time of day: {now:0.00}" + (gm.TimeOverride.HasValue ? " (frozen)" : ""),
                                            keepOpen: true, output: OutputType.System);
                }
                if (arg.Equals("off", System.StringComparison.OrdinalIgnoreCase))
                {
                    gm.SetTimeOverride(null);
                    return CommandResult.Ok("Time resumed (live clock).", keepOpen: true, output: OutputType.System);
                }
                if (float.TryParse(arg, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float t))
                {
                    float f = Mathf.Repeat(t, 1f);
                    gm.SetTimeOverride(f);
                    return CommandResult.Ok($"Time frozen at {f:0.00}.", keepOpen: true, output: OutputType.System);
                }
                return CommandResult.Bad("Usage: time [0..1|off]");
            },
        });
```

- [ ] **Step 2: Verify** — recipe `V` steps 1–4. Enter Play, open the command console, and run `time 0` (night), `time 0.25` (dawn), `time 0.5` (noon), `time 0.75` (dusk), screenshotting each. Expected: terrain **and** water tint clearly shift blue→warm→white→orange; the player's existing shadow still renders; `time off` resumes the live clock; `time` prints the current value.

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/CommandBootstrap.cs
git commit -m "DayNight: debug 'time' command to scrub/freeze the clock"
```

---

## Task 7: Tint the player body with the day/night cycle

**Files:**
- Modify: `Assets/_Scripts/Player/PlayerView.cs`

- [ ] **Step 1: Add the cached-renderers field** — after the `Animator animator;` field ([PlayerView.cs:7](Assets/_Scripts/Player/PlayerView.cs)):

```csharp
    SpriteRenderer[] bodyRenderers;   // all child sprites EXCEPT the "Shadow" child; day/night tints these
```

- [ ] **Step 2: Cache the body renderers in `Awake`** — change:
```csharp
    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        lastPos = transform.position;
    }
```
to:
```csharp
    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        lastPos = transform.position;

        var all = GetComponentsInChildren<SpriteRenderer>(true);
        var body = new System.Collections.Generic.List<SpriteRenderer>(all.Length);
        foreach (var sr in all) if (sr.gameObject.name != "Shadow") body.Add(sr);   // leave the pack shadow untouched
        bodyRenderers = body.ToArray();
    }
```

- [ ] **Step 3: Apply the tint at the end of `LateUpdate`** — after the facing/animator block, before the closing brace of `LateUpdate()` ([PlayerView.cs:46](Assets/_Scripts/Player/PlayerView.cs)):

```csharp
        var dn = Game.Instance != null ? Game.Instance.DayNight : null;
        if (dn != null && bodyRenderers != null)
        {
            var c = dn.tint;
            for (int i = 0; i < bodyRenderers.Length; i++)
                if (bodyRenderers[i] != null) bodyRenderers[i].color = c;
        }
```

- [ ] **Step 4: Verify** — recipe `V` steps 1–4. Enter Play; `time 0` then `time 0.5`. Expected: the player's body darkens to blue at night and returns to full brightness at noon, in step with the world; the player's **Shadow** child does not get re-tinted (still the pack shadow). Move the player to confirm the tint persists across animation frames.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Player/PlayerView.cs
git commit -m "DayNight: tint the player body (not the shadow) with the cycle"
```

---

## Task 8: Day/Night tuner panel + time scrubber

**Files:**
- Modify: `Assets/_Scripts/UI/TunerPanels.cs`

- [ ] **Step 1: Register the panel** — in `Build()` ([TunerPanels.cs:31](Assets/_Scripts/UI/TunerPanels.cs)), add an entry to the list (apply is a no-op — the system reads the settings live each Tick):

Change:
```csharp
        new Panel("Structures",    StructureBody, () => gm.Regenerate()),
    };
```
to:
```csharp
        new Panel("Structures",    StructureBody, () => gm.Regenerate()),
        new Panel("Day/Night",     DayNightBody,  () => {}),
    };
```

- [ ] **Step 2: Add the `DayNightBody` + `ColRGB` methods** — after `StructureBody()` ([TunerPanels.cs:137](Assets/_Scripts/UI/TunerPanels.cs)):

```csharp
    void DayNightBody()
    {
        var d = gm.DayNightCfg;
        F("Cycle seconds", ref d.cycleSeconds, 5f, 1200f, "0");
        F("Dawn time", ref d.dawnTime, 0.01f, 0.49f, "0.00");
        F("Dusk time", ref d.duskTime, 0.51f, 0.99f, "0.00");
        d.nightColor = ColRGB("Night", d.nightColor);
        d.dawnColor  = ColRGB("Dawn",  d.dawnColor);
        d.noonColor  = ColRGB("Noon",  d.noonColor);
        d.duskColor  = ColRGB("Dusk",  d.duskColor);

        float cur = gm.TimeOverride ?? (gm.DayNight != null ? gm.DayNight.timeOfDay : 0f);
        GUILayout.Label($"Time of day  {cur:0.00}" + (gm.TimeOverride.HasValue ? "  (frozen)" : ""));
        float nt = GUILayout.HorizontalSlider(cur, 0f, 1f);
        if (!Mathf.Approximately(nt, cur)) gm.SetTimeOverride(nt);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Live")) gm.SetTimeOverride(null);
        if (GUILayout.Button("Save")) gm.SaveDayNightSettings();
        if (GUILayout.Button("Reset")) gm.ResetDayNightSettings();
        GUILayout.EndHorizontal();
    }

    Color ColRGB(string name, Color c)
    {
        GUILayout.Label(name);
        float r = c.r, g = c.g, b = c.b;
        F("  R", ref r, 0f, 1f, "0.00");
        F("  G", ref g, 0f, 1f, "0.00");
        F("  B", ref b, 0f, 1f, "0.00");
        return new Color(r, g, b, 1f);
    }
```

- [ ] **Step 3: Verify** — recipe `V` steps 1–4. Enter Play; open the "Day/Night" accordion in the on-screen tuner (visible only in the live Game view, not MCP screenshots — observe directly or via the live window). Drag the **Time of day** scrubber → the world tint follows in real time; tweak a color → it applies live; **Save**, stop, replay → the tuned colors persist (loaded from `daynight.json`); **Reset** restores defaults; **Live** resumes the moving clock.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/UI/TunerPanels.cs
git commit -m "DayNight: tuner accordion (colours, cycle, time scrubber) + persistence"
```

---

# Phase 2 — Structure Floor Shadow

## Task 9: Structure shadow settings

**Files:**
- Modify: `Assets/_Scripts/World/StructureSettings.cs`

- [ ] **Step 1: Add the shadow fields** — after the `markerColor` field ([StructureSettings.cs:11](Assets/_Scripts/World/StructureSettings.cs)), inside the class:

```csharp
    [Header("Floor shadow")]
    public bool castShadow = true;                              // draw a fixed darkened shadow under each site tile
    [Range(-1f, 1f)] public float shadowOffsetX = 0f;          // shadow offset in cells (x)
    [Range(-1f, 1f)] public float shadowOffsetY = -0.15f;      // shadow offset in cells (y; negative = toward camera)
    public Color shadowColor = new Color(0f, 0f, 0f, 0.45f);   // darken + translucency of the shadow copy
```

- [ ] **Step 2: Verify** — recipe `V` steps 1–3. Expected: no compile errors. (These are JSON-safe like the existing `markerColor`, so `structures.json` persistence keeps working.)

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/World/StructureSettings.cs
git commit -m "Structures: add floor-shadow settings (cast/offset/colour)"
```

---

## Task 10: World queries for the structure shadow

**Files:**
- Modify: `Assets/_Scripts/World/World.cs` (add three members)

- [ ] **Step 1: Add the shadow query members** — after the `LandColor(...)` method ([World.cs:83](Assets/_Scripts/World/World.cs)), before `MoveCost`:

```csharp
    // ---- Structure drop-shadow: a darkened, offset copy of the site's own tile (CellRenderer draws it under
    // the tile). Reuses LandSprite so it stays a summer-sheet slice => the single-material terrain mesh is
    // unchanged. Returns null for non-site cells or when shadows are disabled. ----
    public Sprite StructureShadowSpriteAt(int cx, int cy)
        => (cfg.structureSettings != null && cfg.structureSettings.castShadow && SiteAt(cx, cy) != null)
           ? LandSprite(cx, cy) : null;

    public Vector2 StructureShadowOffset => cfg.structureSettings != null
        ? new Vector2(cfg.structureSettings.shadowOffsetX, cfg.structureSettings.shadowOffsetY)
        : new Vector2(0f, -0.15f);

    public Color32 StructureShadowColor => cfg.structureSettings != null
        ? (Color32)cfg.structureSettings.shadowColor : new Color32(0, 0, 0, 115);
```

- [ ] **Step 2: Verify** — recipe `V` steps 1–3. Expected: no compile errors. (Not yet consumed by the renderer.)

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/World/World.cs
git commit -m "Structures: World queries for the floor-shadow sprite/offset/colour"
```

---

## Task 11: Render the structure shadow in the mesh

**Files:**
- Modify: `Assets/_Scripts/World/CellRenderer.cs` (Quad signature + BuildWindowMesh)
- Modify: `Assets/_Scripts/World/WorldView.cs:59`

- [ ] **Step 1: Give `Quad` a vertex-colour parameter** — change the local function ([CellRenderer.cs:87](Assets/_Scripts/World/CellRenderer.cs)):

```csharp
        void Quad(float wx, float wy, float uMin, float uMax, float vMin, float vMax, List<int> tris)
        {
            int v = verts.Count;
            verts.Add(new Vector3(wx - half, wy - half, 0f));
            verts.Add(new Vector3(wx + half, wy - half, 0f));
            verts.Add(new Vector3(wx - half, wy + half, 0f));
            verts.Add(new Vector3(wx + half, wy + half, 0f));
            uvs.Add(new Vector2(uMin, vMin)); uvs.Add(new Vector2(uMax, vMin));
            uvs.Add(new Vector2(uMin, vMax)); uvs.Add(new Vector2(uMax, vMax));
            colors.Add(white); colors.Add(white); colors.Add(white); colors.Add(white);
            tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);
            tris.Add(v + 1); tris.Add(v + 2); tris.Add(v + 3);
        }
```
to:
```csharp
        void Quad(float wx, float wy, float uMin, float uMax, float vMin, float vMax, Color32 col, List<int> tris)
        {
            int v = verts.Count;
            verts.Add(new Vector3(wx - half, wy - half, 0f));
            verts.Add(new Vector3(wx + half, wy - half, 0f));
            verts.Add(new Vector3(wx - half, wy + half, 0f));
            verts.Add(new Vector3(wx + half, wy + half, 0f));
            uvs.Add(new Vector2(uMin, vMin)); uvs.Add(new Vector2(uMax, vMin));
            uvs.Add(new Vector2(uMin, vMax)); uvs.Add(new Vector2(uMax, vMax));
            colors.Add(col); colors.Add(col); colors.Add(col); colors.Add(col);
            tris.Add(v); tris.Add(v + 2); tris.Add(v + 1);
            tris.Add(v + 1); tris.Add(v + 2); tris.Add(v + 3);
        }
```

- [ ] **Step 2: Extend `BuildWindowMesh`'s signature** — change ([CellRenderer.cs:73](Assets/_Scripts/World/CellRenderer.cs)):

```csharp
    public static Mesh BuildWindowMesh(Func<int, int, bool> isLand, Func<int, int, Sprite> landSpriteAt, Vector2Int center, int radius, float cellWorld)
```
to:
```csharp
    public static Mesh BuildWindowMesh(Func<int, int, bool> isLand, Func<int, int, Sprite> landSpriteAt,
        Func<int, int, Sprite> shadowAt, Vector2 shadowOffset, Color32 shadowColor,
        Vector2Int center, int radius, float cellWorld)
```

- [ ] **Step 3: Emit the shadow in pass 1, then update the three `Quad` calls** — change the pass-1 body ([CellRenderer.cs:105-112](Assets/_Scripts/World/CellRenderer.cs)):

```csharp
            if (!isLand(cx, cy)) continue;
            float uMin, uMax, vMin, vMax;
            Sprite s = landSpriteAt(cx, cy);                                                      // per-cell biome variant (or null)
            if (s != null)
                RectUV(s.textureRect, s.texture.width, s.texture.height, out uMin, out uMax, out vMin, out vMax);
            else
                TileUV(FallbackGroundCol, FallbackGroundRow, SummerW, SummerH, out uMin, out uMax, out vMin, out vMax);  // blank ground
            Quad((cx + 0.5f) * cw, (cy + 0.5f) * cw, uMin, uMax, vMin, vMax, landTris);
```
to:
```csharp
            if (!isLand(cx, cy)) continue;

            // structure drop-shadow: a darkened, offset copy of the site tile, drawn BEFORE the tile so it sits
            // under it. With a downward offset (shadowOffsetY < 0) the cell below was emitted earlier this pass,
            // so the translucent shadow paints over it correctly; the opaque site tile (next) covers its own cell.
            Sprite shadow = shadowAt(cx, cy);
            if (shadow != null && shadow.texture != null)
            {
                RectUV(shadow.textureRect, shadow.texture.width, shadow.texture.height,
                       out float shu0, out float shu1, out float shv0, out float shv1);
                Quad((cx + 0.5f + shadowOffset.x) * cw, (cy + 0.5f + shadowOffset.y) * cw,
                     shu0, shu1, shv0, shv1, shadowColor, landTris);
            }

            float uMin, uMax, vMin, vMax;
            Sprite s = landSpriteAt(cx, cy);                                                      // per-cell biome variant (or null)
            if (s != null)
                RectUV(s.textureRect, s.texture.width, s.texture.height, out uMin, out uMax, out vMin, out vMax);
            else
                TileUV(FallbackGroundCol, FallbackGroundRow, SummerW, SummerH, out uMin, out uMax, out vMin, out vMax);  // blank ground
            Quad((cx + 0.5f) * cw, (cy + 0.5f) * cw, uMin, uMax, vMin, vMax, white, landTris);
```

- [ ] **Step 4: Update the pass-2 `Quad` calls to pass `white`** — change the open-water call ([CellRenderer.cs:132](Assets/_Scripts/World/CellRenderer.cs)):
```csharp
                Quad(i * cw, j * cw, uMin, uMax, vMin, vMax, waterTris);
```
to:
```csharp
                Quad(i * cw, j * cw, uMin, uMax, vMin, vMax, white, waterTris);
```
and the coastline call ([CellRenderer.cs:137](Assets/_Scripts/World/CellRenderer.cs)):
```csharp
                Quad(i * cw, j * cw, uMin, uMax, vMin, vMax, landTris);
```
to:
```csharp
                Quad(i * cw, j * cw, uMin, uMax, vMin, vMax, white, landTris);
```

- [ ] **Step 5: Pass the new args from `WorldView.RebuildMesh`** — change ([WorldView.cs:59](Assets/_Scripts/World/WorldView.cs)):
```csharp
        gridMesh.sharedMesh = CellRenderer.BuildWindowMesh(world.IsLand, world.LandSprite, meshCenter, cfg.viewRadius, cellWorld);
```
to:
```csharp
        gridMesh.sharedMesh = CellRenderer.BuildWindowMesh(world.IsLand, world.LandSprite,
            world.StructureShadowSpriteAt, world.StructureShadowOffset, world.StructureShadowColor,
            meshCenter, cfg.viewRadius, cellWorld);
```

- [ ] **Step 6: Verify** — recipe `V` steps 1–4. Enter Play; run `sites` to find the nearest structure, walk to it (or note its coords), and screenshot. Expected: each structure tile shows a darkened, slightly-downward shadow beneath it; no shadow on plain ground/coast/water; no seams or flicker on neighboring tiles as the mesh streams.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Scripts/World/CellRenderer.cs Assets/_Scripts/World/WorldView.cs
git commit -m "Structures: render a fixed darkened floor shadow under site tiles"
```

---

## Task 12: Structure shadow tuner controls + final pass

**Files:**
- Modify: `Assets/_Scripts/UI/TunerPanels.cs` (`StructureBody`)

- [ ] **Step 1: Add the shadow controls to the Structures panel** — change `StructureBody()` ([TunerPanels.cs:127-137](Assets/_Scripts/UI/TunerPanels.cs)):

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
to:
```csharp
    void StructureBody()
    {
        var s = gm.Structure;
        I("Block size (smaller=denser)", ref s.blockSize, 2, 40);
        F("Site chance", ref s.siteChance, 0f, 1f, "0.00");

        GUILayout.Space(4f);
        bool cs = GUILayout.Toggle(s.castShadow, " Cast floor shadow");
        if (cs != s.castShadow) { s.castShadow = cs; dirty = true; }
        F("Shadow offset X", ref s.shadowOffsetX, -1f, 1f, "0.00");
        F("Shadow offset Y", ref s.shadowOffsetY, -1f, 1f, "0.00");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save")) gm.SaveStructureSettings();
        if (GUILayout.Button("Reset")) gm.ResetStructureSettings();
        GUILayout.EndHorizontal();
    }
```

(The Structures panel's apply is already `() => gm.Regenerate()`, which rebuilds the mesh so toggling/offsetting the shadow takes effect live.)

- [ ] **Step 2: Verify** — recipe `V` steps 1–4. Enter Play; open the Structures accordion; toggle **Cast floor shadow** off/on (shadows vanish/return after the mesh rebuild) and drag **Shadow offset Y** (the shadow shifts under each structure). Save, replay → persists.

- [ ] **Step 3: Final regression pass** — with everything in place: `time 0/0.25/0.5/0.75` shows the full day cycle on terrain, water, and player; the player's pack shadow is intact; structures show their floor shadow at all times of day; minimap is unaffected; console is error-free.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/UI/TunerPanels.cs
git commit -m "Structures: tuner controls for the floor shadow"
```

---

## Self-review (completed by plan author)

- **Spec coverage:** §4 day/night clock+tint → Tasks 1–8; §4.5 `_DayTint` → Task 3; §4.6 wiring → Tasks 4–5; §4.7 tuner+`time` → Tasks 6, 8; player body tint → Task 7; §5 structure shadow → Tasks 9–12. All spec sections map to tasks.
- **Type consistency:** `DayNightState{timeOfDay,tint}`, `DayNightSettings{cycleSeconds,night/dawn/noon/duskColor,dawn/duskTime}`, `DayNightSystem.State/Tick/TimeOverride`, `DayNightMath.TimeOfDay/Tint`, `DayNightView.Apply`, `Game.DayNight/DayNightCfg/TimeOverride/SetTimeOverride/Save+ResetDayNightSettings`, `WorldView.TerrainMat`, `World.StructureShadowSpriteAt/StructureShadowOffset/StructureShadowColor`, `CellRenderer.Quad(...,Color32,...)` + `BuildWindowMesh(...,shadowAt,shadowOffset,shadowColor,...)` are used identically across tasks.
- **No placeholders:** every code step shows complete code; every verify step states the exact `time` value / action and expected visual.
- **Known gotchas honored:** `refresh scope=all` for new files (recipe `V`); no new asmdef; the "Shadow" child is excluded from player tint; downward shadow offset documented for painter's order; commits carry no Claude attribution.
