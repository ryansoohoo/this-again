# Day/Night Cycle + Structure Floor Shadow — Design

- **Date:** 2026-05-23
- **Status:** Approved (brainstorming), revised post-discovery — ready for implementation plan
- **Scope:** v1 visual pass (look-only)

## 1. Goal

Make the top-down world feel alive with a **server-synced day/night cycle** (a global color tint that ramps
warm dawn → bright noon → orange dusk → dim blue night) and a **fixed floor shadow under structure tiles**,
without disturbing the tuned art, the custom water shader, or the deterministic single-mesh streaming model.

### Decisions (Ryan, brainstorming + planning discovery 2026-05-23)
- **Top-down**, shadows lie flat on the floor, all pixel-art and **pixel-perfect** (hard requirement).
- **Character shadows: OUT.** Discovery during planning: the Minifantasy pack already ships a `_Shadows/` set
  and the player prefab already renders a pixel-perfect, per-frame, per-facing floor shadow (a "Shadow" child
  on a dedicated "Shadow" sorting layer, animator-driven — see §2). Ryan's call: **leave it untouched, add no
  sun-driven character shadow.** This removed the from-scratch bake tool / new component / new sorting layer the
  first draft proposed.
- **Casters:** only **structures** get a new static (non-sun) floor shadow.
- **Day/night tech:** **global multiplicative tint** — no URP 2D lights, no lit-material conversion.
- **Day/night scope:** look-only, **server-synced** time so all clients share the sky. The tint reaches
  terrain, water, and the player's **body** sprites (not the existing shadow child).
- Defaults: ~8-minute cycle; night = deep dim blue.
- **v1 = day/night cycle + structure floor shadow.**

### In scope (v1)
- Synced day/night clock + tunable 4-stop color ramp; global tint on terrain, water, and the player body.
- A fixed darkened drop-shadow beneath structure-site tiles (reuses the structure's own tile — **no new art**).
- Live tuner panel + JSON persistence; a debug `time` command to scrub/freeze the clock.

### Out of scope (deferred — see §10)
- URP 2D `Light2D`, point lights, lamp/window emissive glows, normal maps (the lit-shader upgrade path).
- Any gameplay effect of time-of-day (vision radius, encounter rates, spawns).
- **Any character-shadow work** — the existing pack contact shadow is retained as-is.
- Sun-**driven** structure shadows; a hand-authored structure shadow sprite; weather, stars, moon phases.

## 2. Fit with the existing pipeline (why the design is shaped this way)

- **Render pipeline:** URP 17.3 with the **2D Renderer** (`Assets/Settings/Renderer2D.asset`). 2D lights exist
  but everything is **unlit** — terrain submesh [0] uses `Sprites/Default`
  ([CellRenderer.cs:37](Assets/_Scripts/World/CellRenderer.cs)), the character uses `CharacterUnlit.mat`
  (`Sprites/Default`), water uses `Custom/WaterWaves` with `Lighting Off`. Converting all that to lit shaders is
  the cost we avoid by choosing **global tint** (multiply a color into each material).
- **The world is one streaming mesh of flat 16×16 tiles.** `World.LandSprite(cx,cy)`
  ([World.cs:59](Assets/_Scripts/World/World.cs)) resolves ground/coast/biome/**structure** to a tile sprite;
  `CellRenderer.BuildWindowMesh` emits them into two submeshes ([0] summer sheet, [1] water). **Structures are
  not GameObjects** — a site just wins the cell's tile. So a structure shadow must be drawn **into the mesh**.
- **The player already has a baked floor shadow.** `PlayerPrefabBuildTool` adds a **"Shadow" child** on a
  dedicated **"Shadow" sorting layer** ([PlayerPrefabBuildTool.cs:32](Assets/Editor/PlayerPrefabBuildTool.cs)),
  and `CharacterAnimBuildTool` drives it **per-frame, per-facing** from the pack's
  `Characters/Human/_Shadows/{Idle,Walk}.png` ([CharacterAnimBuildTool.cs:55-56](Assets/Editor/CharacterAnimBuildTool.cs)).
  It is a fixed contact shadow (no sun direction). **v1 leaves it untouched**; day/night only tints the body.
- **Determinism:** terrain is a pure function of `(cell, seed)` with zero replication. The day/night clock
  follows suit — derived from network/local time + a fixed period, so every client agrees **without a
  NetworkVariable or per-frame RPC**.
- **Convention:** Input → Logic → Data → Visual (`Assets/_Scripts/ARCHITECTURE.md`): Data = plain
  `*State`/`*Settings`; Logic = `*System` writes data; Visual = `*View` reads data, writes only visuals.

**Assembly boundary (do not cross):** `Minifantasy.Commands` asmdef (`Assets/_Scripts/Commands/`) is pure and
unit-tested — **add no new types there**. The debug `time` command is registered from `CommandBootstrap` (game
assembly), reusing the existing `World` scope. Everything new lives in `Assembly-CSharp`.

## 3. Component overview

```
Game (Update)                              Game (Update, after Tick)        PlayerView (LateUpdate)
  └─ DayNightSystem.Tick()                   └─ DayNightView.Apply(state)      └─ tint body SpriteRenderers
       time = ServerTime|local / period           terrain mat[0]._Color             with Game.Instance.DayNight.tint
       state.timeOfDay, state.tint                 water  mat[1]._DayTint            (skips the "Shadow" child)
```

New folder `Assets/_Scripts/Environment/` holds the day/night system (matches the Core/World/Player/Camera/
Net/UI category layout). There is **no** bake tool, **no** new component on the player, and **no** new sorting
layer — the pack/prefab already provide the character's shadow.

## 4. Day/Night clock + tint

### 4.1 Data — `DayNightState` (`Environment/DayNightState.cs`, plain class)
- `float timeOfDay` — 0..1 (0 = midnight, 0.5 = noon).
- `Color tint` — the global multiply color for this instant.

(No `sunStep`/`shadowAlpha` — nothing is sun-driven; the structure shadow is static and the character shadow is
untouched.)

### 4.2 Data — `DayNightSettings` (`Environment/DayNightSettings.cs`, `[Serializable]`, tunable)
- `float cycleSeconds = 480` — full day length (~8 min).
- Four key colors: `nightColor` (deep dim blue), `dawnColor` (warm), `noonColor` (≈white), `duskColor` (orange).
- `float dawnTime = 0.25f`, `duskTime = 0.75f` — the color-stop times for dawn/dusk.

### 4.3 Logic — `DayNightSystem` + `DayNightMath` (`Environment/DayNightSystem.cs`)
- `DayNightSystem` (ticked by `Game.Update`): owns a `DayNightState`; `Tick()` sets `timeOfDay` (from
  `NetworkManager.Singleton.ServerTime.Time` when connected, else `Time.unscaledTimeAsDouble`) and `tint`.
  A nullable `TimeOverride` (set by the `time` command) freezes the clock locally for testing.
- `DayNightMath` (pure static, no scene deps — kept separate so it's unit-testable later if a test-reachable
  assembly is added; today the game assembly isn't reachable from the Commands test asmdef):
  - `TimeOfDay(seconds, cycleSeconds)` → 0..1 wrap.
  - `Tint(t, settings)` → piecewise lerp over stops `(0,night)(dawnTime,dawn)(0.5,noon)(duskTime,dusk)(1,night)`.

### 4.4 Visual — `DayNightView` (`Environment/DayNightView.cs`, plain class, applied by `Game`)
- Holds the terrain (`mat[0]`) and water (`mat[1]`) materials from `WorldView`.
- `Apply(state)`: `terrainMat.color = tint` (Sprites/Default `_Color`; nothing else writes it);
  `waterMat.SetColor("_DayTint", tint)` — a **new** uniform so we don't fight `WaterMaterial`, which only
  writes the wave params ([WaterMaterial.cs:17](Assets/_Scripts/World/WaterMaterial.cs)).
- **Player body tint** is applied in `PlayerView.LateUpdate` (players spawn at runtime): tint every child
  `SpriteRenderer` **except** the one named "Shadow", from `Game.Instance.DayNight.tint`. No new component, no
  prefab-tool change.

### 4.5 Shader — `WaterWaves.shader` (one tint hook)
- Add `_DayTint ("Day/Night Tint", Color) = (1,1,1,1)` + uniform; multiply **both** output branches by it (the
  calm `return _CalmColor * i.color` and the ripple `return tex2D(...) * i.color`). `_Color`/`_CalmColor` stay
  owned by `WaterMaterial`; `_DayTint` by `DayNightView`. New shader/`.cs` → `refresh scope=all`.

### 4.6 Wiring — `Game.cs`, `WorldView.cs`
- `WorldView`: expose `public Material TerrainMat { get; }` from `sharedMaterials[0]` next to the existing
  `WaterMat` ([WorldView.cs:22,34](Assets/_Scripts/World/WorldView.cs)).
- `Game`: `[Header("Day/Night")] DayNightSettings dayNight`; load `daynight.json` via `JsonPref`; after
  `waterMat` ([Game.cs:114](Assets/_Scripts/Game.cs)) build `DayNightSystem` + `DayNightView(view.TerrainMat,
  view.WaterMat)`; in `Update` call `Tick()` then `Apply(state)`. Facade `DayNight` (state) + `DayNightCfg`
  (settings) + `SetTimeOverride`/`TimeOverride`; `Save/ResetDayNightSettings()`.

### 4.7 UI — tuner + `time` command
- `TunerPanels`: a "Day/Night" accordion — cycleSeconds, dawn/duskTime, RGB sliders for the 4 colors, a
  **time scrubber** (calls `SetTimeOverride`), a "Live (resume)" button, Save/Reset → `daynight.json`.
- `CommandBootstrap`: `time` (World scope, optional arg) — `time` prints the clock; `time <0..1>` freezes it;
  `time off` resumes. Client-local verification aid.

## 5. Structure floor shadow (no new art)

A site's tile is a **full opaque 16×16** (icon + baked ground), so the shadow is just a **darkened, offset copy
of that same tile**, drawn underneath — no shadow sprite to author, and it stays a summer-sheet slice so the
single-material terrain mesh is unchanged.
- `StructureSettings`: add `bool castShadow = true`, `float shadowOffsetX = 0`, `float shadowOffsetY = -0.15f`
  (down, toward the camera), `Color shadowColor = (0,0,0,0.45)` — all JSON-safe (like the existing `markerColor`).
- `World`: `Sprite StructureShadowSpriteAt(cx,cy)` → `LandSprite(cx,cy)` when `castShadow && SiteAt != null`,
  else null; plus `StructureShadowOffset` (Vector2) and `StructureShadowColor` (Color32) from settings.
- `CellRenderer.BuildWindowMesh`: add params `(Func<int,int,Sprite> shadowAt, Vector2 shadowOffset, Color32
  shadowColor)`; give the local `Quad` helper a vertex-`Color32` parameter (existing calls pass white). In
  pass 1, for a site cell, emit the shadow quad (sprite UVs, at `center + offset`, vertex color =
  `shadowColor`) **before** the cell's own tile so painter's order draws it underneath. `WorldView.RebuildMesh`
  passes `world.StructureShadowSpriteAt`, `world.StructureShadowOffset`, `world.StructureShadowColor`.
- **Draw-order dependency:** with a **downward** (`shadowOffsetY < 0`) offset and the pass-1 loop's increasing
  `cy`, the affected neighbor below is already emitted, so the translucent shadow correctly paints over it; the
  site's opaque tile (emitted next) covers the overlap on its own cell. Sites are ≥ one block apart, so two
  sites' shadows never interact. The tuner allows other offsets but downward is the tested default.
- `TunerPanels` "Structures" accordion: add a `castShadow` toggle + `shadowOffsetX/Y` sliders (apply via the
  existing `Regenerate` → rebuilds the mesh).

*Limitation:* because the tile includes baked ground, the shadow is a soft dark **square** under the structure,
not a building silhouette. Acceptable for v1; a dedicated shadow sprite is a future polish.

## 6. Determinism, multiplayer & performance
- **Time** is a pure function of network/local time + `cycleSeconds` → identical on every client, **no
  replication**. The `time` override is local/client-only.
- **Tint** is purely visual/local: ~2 material color writes/frame + per-player body writes. **Structure
  shadow** adds a handful of quads to the existing mesh build (sites are sparse). Negligible on this CPU-bound
  game; no GPU lighting passes, no per-frame allocations.

## 7. File manifest

**New**
- `Assets/_Scripts/Environment/DayNightSettings.cs`
- `Assets/_Scripts/Environment/DayNightState.cs`
- `Assets/_Scripts/Environment/DayNightSystem.cs` (incl. `DayNightMath`)
- `Assets/_Scripts/Environment/DayNightView.cs`

**Modified**
- `Assets/_Shaders/WaterWaves.shader` — add `_DayTint`, multiply both output branches.
- `Assets/_Scripts/World/WorldView.cs` — expose `TerrainMat`; pass structure-shadow args to `BuildWindowMesh`.
- `Assets/_Scripts/Game.cs` — `DayNightSettings` field, build/tick `DayNightSystem`, apply `DayNightView`,
  `DayNight`/`DayNightCfg`/`SetTimeOverride` facade, `daynight.json` `JsonPref` + Save/Reset.
- `Assets/_Scripts/Player/PlayerView.cs` — tint body sprite renderers (excluding "Shadow") from the day tint.
- `Assets/_Scripts/UI/TunerPanels.cs` — Day/Night accordion + time scrubber; structure-shadow controls.
- `Assets/_Scripts/CommandBootstrap.cs` — debug `time` command (World scope).
- `Assets/_Scripts/World/StructureSettings.cs` — `castShadow`, `shadowOffsetX/Y`, `shadowColor`.
- `Assets/_Scripts/World/World.cs` — `StructureShadowSpriteAt`/`StructureShadowOffset`/`StructureShadowColor`.
- `Assets/_Scripts/World/CellRenderer.cs` — `Quad` vertex color; `BuildWindowMesh` shadow params + emission.

## 8. Phasing
1. **Day/night cycle** — Settings/State/System/Math/View + `_DayTint` + Game wiring + `time` command + player
   body tint + tuner. *Deliverable:* the world (terrain, water, player) visibly cycles day↔night, synced,
   tunable, persistent; `time` scrubs it.
2. **Structure floor shadow** — settings + `World` queries + `CellRenderer`/`WorldView` + tuner controls.
   *Deliverable:* each structure sits on a fixed darkened shadow.

## 9. Verification plan
Per the project's unity-mcp flow (`execute_code` is broken; use **refresh `scope=all` → poll `editor_state`
isCompiling → read_console (clean) → enter Play → screenshot**; the MCP screenshot captures the camera, **not**
IMGUI panels):
- **Phase 1:** `time 0 / 0.25 / 0.5 / 0.75` (or the scrubber) → screenshots show night/dawn/noon/dusk tint on
  terrain, water, and the player body; the existing character shadow still renders; tint persists after
  save+replay; `time off` resumes the live clock.
- **Phase 2:** a structure tile shows the darkened shadow beneath it; offset/toggle react in the tuner; no
  seams on neighbors; minimap unaffected.
- **Multiplayer:** host + a second client (manual host-click; MCP can't drive the IMGUI Host button) → both
  see the same time-of-day within clock tolerance.

## 10. Open assumptions & deferred
- `NetworkManager.ServerTime` is available when networked; local clock otherwise — both fine for look-only.
- Player body sprites tolerate `SpriteRenderer.color` tint without breaking the layered look; the "Shadow"
  child is identified by name and left alone.
- The darkened-tile structure shadow (a dark square) is good enough for v1; a bespoke shadow sprite, sun-driven
  structure/character shadows, and the URP 2D-lights upgrade (point lights, glows) are deferred.
