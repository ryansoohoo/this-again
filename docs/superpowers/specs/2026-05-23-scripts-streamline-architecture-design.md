# Scripts Streamline — Architecture Design

**Date:** 2026-05-23
**Status:** Approved (design); implementation plan to follow.

## Goal

Streamline the Unity script project so the code reads as a **story with clear steps**,
has a **single point of entry**, follows **KISS + DRY**, and is sorted into **category
folders** so a human can instantly find where something lives. Preserve all current,
verified-in-Play behavior — this is a restructure, not a feature change.

## Principles (Ryan's)

- A readable story with steps; each unit has clear inputs and outputs.
- Single point of entry — one manager runs the workflow.
- KISS + DRY; minimal lines; robust and scalable.
- Category folders so humans find things fast.
- Prefer plain C# / static helpers over MonoBehaviour; extract helpers when we can.
- Each file keeps a header summary + one-line comments (the existing style).

## Decisions

- **Approach A:** plain-C# subsystems + category folders, **no asmdefs**. (asmdefs map 1:1
  to folders and stay a cheap future upgrade if compile times or boundary slips bite.)
- **Delete the climate system:** `BiomeGenerator.At` collapses to a `bool IsLand`.
  Behavior-preserving today — every consumer already reduces the 7 climate biomes to
  water-vs-land, and land visual variety comes from `GroundGenerator`.
- **Phased**, with a Unity compile + Play **verify gate between phases**.
- **Rename** `GridManager` → `Game` (GUID-preserving: rename file + `.meta`, keep
  `[SerializeField]` field names so the scene component and its serialized values survive).

## Target structure

```
_Scripts/
├─ Core/
│  ├─ Game.cs            single entry: boot story + Update + facade   ← from GridManager (slimmed)
│  ├─ JsonPref.cs        generic settings save/load/reset             ← NEW (replaces 9 methods)
│  ├─ GameLog.cs         output bus + OutputEntry                     ← moved
│  └─ OutputType.cs      output vocabulary                            ← moved from Commands
├─ World/
│  ├─ World.cs           pipeline: cell → land / sprite / color       ← NEW (from GridManager)
│  ├─ WorldView.cs       streaming: mesh + minimap + cam bounds       ← NEW (from GridManager)
│  ├─ CellRenderer.cs    mesh/texture builder                         ← moved (Biome→bool)
│  ├─ BiomeGenerator.cs  land/water noise                             ← from Biome.cs (collapsed)
│  ├─ BiomeSettings.cs   land/water knobs (7)                         ← from Biome.cs (trimmed)
│  ├─ GroundGenerator.cs cover-type noise                             ← from Ground.cs
│  ├─ GroundSettings.cs  cover + coverage knobs                       ← from Ground.cs
│  ├─ BiomeTiles.cs      SO: sprite variants + minimap color          ← moved
│  ├─ WaterSettings.cs   water-anim knobs                             ← was Water.cs
│  └─ WaterMaterial.cs   push WaterSettings → material                ← NEW (from GridManager)
├─ Player/   PlayerController.cs · Pathfinder.cs                      ← moved
├─ Camera/   CameraRig.cs                                            ← moved
├─ Net/      RelayConnector.cs · RelayTestHUD.cs                     ← moved
├─ UI/       Minimap.cs · ChatPopup.cs · CommandConsole.cs · TunerPanels.cs   ← moved
└─ Commands/ Command.cs · CommandRegistry.cs · CommandRouter.cs · CommandScope.cs · CommandBootstrap.cs

DELETED: GridServer.cs · the Biomes color static + Biome enum (from Biome.cs)
```

**Layering rule** (convention, no asmdefs): everything may depend on **Core**; nothing
depends on **UI**.

## Modules

### Core/Game — single entry (the story)

```
Awake():
  ConfigureApp()                  // vsync / fps / runInBackground / camera setup
  settings.LoadAll()              // JsonPref loads biome + ground + water
  world  = new World(config…)     // the terrain pipeline
  view   = new WorldView(world…)  // builds first mesh + minimap, sets cam bounds
  camera = new CameraRig(…)
  water  = new WaterMaterial(view.WaterMat, waterSettings)
  Commands.Install()              // CommandBootstrap
Update():
  camera.Tick(dt)
  view.Follow(localPlayerCell)
```

`Game` keeps the `[SerializeField]` inspector fields (texture sheets, BiomeTiles SOs,
radii, colors) and hands them to subsystems via constructors — **no Inspector re-wiring**.
It remains the facade UI/Player already use: `Game.Instance.Cam`, `.CellCenter`,
`.WorldToCell`, `.World.IsWalkable`, `.MinimapTexture`, `.MinimapWorldCenter/Extent`.

### World — terrain pipeline (clear in/out)

Plain C#. One job: answer questions about a cell.
- **In:** `(x,y)` + seed/settings. **Out:** `bool IsLand(x,y)`, `Sprite LandSprite(x,y)`,
  `Color32 LandColor(x,y)`, `bool IsWalkable(x,y)`.
- Absorbs the cache (`GenAt`), `LandSpriteAt`, `BiomeFor`, `CoverageFor`, `LandColorAt`,
  `PickVariant`, `IsUsable`, `Hash01`. Owns `BiomeGenerator` + `GroundGenerator`.

### WorldView — streamer (clear in/out)

Plain C#. One job: keep the visible window current.
- **In:** center cell + `World`. **Out:** rebuilt window mesh, minimap texture, camera
  bounds rect. Exposes the water material for `WaterMaterial`.
- Absorbs `RebuildMesh`, `RebuildMinimap`, `UpdateView`, `meshCenter`/`viewCenter`. Calls
  `CellRenderer`.

### Core/JsonPref<T> — DRY settings

One generic helper replaces the 9 copy-pasted save/load/reset methods:

```
var biome = new JsonPref<BiomeSettings>("biome.json");
biome.Load(into);  biome.Save(from);  biome.Reset();
```

### World/WaterMaterial — material push

Small. Pushes `WaterSettings` into the runtime water material (submesh 1). Absorbs
`ApplyWaterSettings`.

## Deletions

- **Whole file:** `GridServer.cs` (`IGridServer`/`LocalGridServer`/`CellData`/`CellSnapshot`
  — unreferenced, a bounded-grid-era leftover).
- **From Biome.cs:** the `Biomes` static (`WaterShallow`/`WaterDeep`/`GroundColors`/
  `ColorOf`), `ApplyPalette`/`Tinted`, and the `Biome` enum. `BiomeGenerator.At` returns
  `bool IsLand`; `CellRenderer` takes `Func<int,int,bool>`.
- **Dead knobs + their sliders:** `octaves` (climate-only), `coldThreshold`,
  `hotThreshold`, `wetThreshold`, `waterTint`, `waterShoreLevel`, `waterDeepLevel`,
  `waterDepthCells`, `landBackgroundLevel`.

## Surviving tuner knobs

- **Biome (7):** seed, biomeScale, warpStrength, seaLevel, waterScale, continentContrast,
  minimapBrightness.
- **Ground:** scale, octaves, mountainLevel, rockyLevel, forestLevel, grass/forest/rocky/
  mountainCoverage.
- **Water:** animSpeed, waveFreq, waveDrift, calm, windX, windY, styleRow.

## Phased plan — each phase compiles + Plays before the next

| Phase | Change | Risk |
|---|---|---|
| **0** | Dead-code purge + climate→`bool` collapse (behavior-preserving) | low |
| **1** | `JsonPref<T>`; replace the 9 settings methods | low |
| **2** | Move files into folders (move `.cs` + `.cs.meta` together to preserve GUIDs) | low–med |
| **3** | Split `GridManager` → `Game` + `World` + `WorldView` + `WaterMaterial` | med |

**Phase 2 GUID safety:** PlayerController, Minimap, ChatPopup, CommandConsole, TunerPanels,
RelayConnector/HUD live on scene objects/prefabs. Always **move** files (keep the `.meta`),
never recreate, so scene/prefab references don't break.

## Out of scope (future, optional)

- **Phase 4 — extract `LineEditor`** out of the 454-line `CommandConsole` (caret /
  selection / clipboard / word-nav). Worthwhile "separate helper" move, but the riskiest UI
  surgery, so deferred.
- **asmdefs** (Approach B) — compiler-enforced boundaries; layer on later if needed.

## Verification (per phase)

Use the unity-mcp verify chain (note: `execute_code` is broken on this machine):
**refresh assets → read Console (expect 0 compile errors) → enter Play**, then confirm:

- Terrain renders identically; coastline still dual-grid autotiles; interior land variety
  (grass/forest/rocky/mountain + blank coverage) unchanged.
- Open water still animates; calm/ripple split intact.
- Minimap colors match the rendered land; water is the flat navy; viewport box + center dot.
- Camera pan (right-drag / arrows), wheel-zoom-to-cursor, spacebar recenter, bounds clamp.
- Player WASD step, double-click auto-move, pathfinding around water.
- Relay host/join; command console open/submit/feedback; chat popup.
- Tuner panels: sliders mutate live; Save/Reset persist across Play stop.
