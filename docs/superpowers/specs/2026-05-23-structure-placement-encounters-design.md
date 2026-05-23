# Structure Placement + ZORK Encounters — Design

- **Date:** 2026-05-23
- **Status:** Approved (brainstorming) — ready for implementation plan
- **Scope:** v1 vertical slice

## 1. Goal

Scatter named "structure" sites (forest village/home/town/mansion, farm, camp, walled town) across the
infinite world using a new deterministic noise layer that runs *after* terrain generation, and let the
player trigger a ZORK-style text encounter by walking onto one. v1 target loop:

> Walk onto a site → movement locks → a text popup opens → type `enter` or `leave` (with inline ghost
> autocomplete) → on `enter`, "A villager greets you to *Oakhollow*." → `leave` closes the popup and unlocks.

The work splits along the user's **logic / visual / data** axes, in two parts: **A. Placement** (new) and
**B. Encounter loop** (mostly wiring existing seams).

### In scope (v1)
- Deterministic, data-driven placement of single-tile structure sites; live-tunable density.
- Sites render on the existing terrain mesh (replace the cell's tile) + show as minimap markers.
- Deterministic per-site name (no replication).
- Walk-on detection, movement lock (incl. halting click-to-move), locked text popup.
- `enter` / `leave` encounter commands; greet text with the site name.
- Inline ghost autocomplete in the console (Tab to accept), shown only on unambiguous prefixes.

### Out of scope (deferred — see §8)
- Branching dialogue, per-step command gating, quests.
- Persistent / networked town memory ("who visited / helped / robbed").
- Non-forest landmark types beyond the listed set (bridges, caves, towers, castles, ruins), winter sheet.
- Structures as movement obstacles or enterable interiors.

## 2. Fit with the existing pipeline

`World` is a pure query layer ("what is at cell (x,y)?") over two deterministic noise generators
(`BiomeGenerator` land/water, `GroundGenerator` ground-cover), with per-cell tile choices made via
`Hash01(cell, seed, salt)` so every Netcode client agrees with **zero replication**. The placement layer is
a third generator in the same mold; the encounter loop reuses the already-scaffolded command system
(`CommandScope.Encounter`, `CommandRouter.Enter/ExitEncounter`, `CommandRegistry.Suggest`,
`InputState.Typing` movement gate, `OutputType.Encounter`).

**Assembly boundaries** (do not move types across these):
- `Minifantasy.Commands` asmdef (`Assets/_Scripts/Commands/`): pure command logic, unit-tested in
  `Assets/Tests/EditMode/`. We add **no new types here** — v1 reuses the existing `Encounter` scope.
- Game assembly (`Assembly-CSharp`, no asmdef): `World/`, `Player/`, `UI/`, `Game.cs`, `CommandBootstrap.cs`,
  and the new `EncounterManager`. New placement types live in `World/`.

## 3. Part A — Placement

### 3.1 Data (new ScriptableObject + settings; user-curated like biomes)

**`StructureSet`** (`ScriptableObject`, `World/StructureSet.cs`) — data only, `[CreateAssetMenu]`:
- `StructureDef[] defs`
- `string[] namePool` — shared site-name pool (e.g. "Oakhollow", "Mossfen"); deterministic pick per site.

**`StructureDef`** (`[Serializable]`):
- `string id` — stable key (e.g. `"forest_town"`), used as the town-memory key later.
- `string label` — shown in encounter text (e.g. `"the town"`, `"the village"`, `"a camp"`).
- `GroundType[] validOn` — terrain this type may spawn on (land cells only).
- `float spawnWeight` — relative frequency among the defs valid at a given cell.
- `BiomeTileVariant[] variants` — **reuses the existing weighted-variant type** ([BiomeTiles.cs:18]); the
  "(2 variations)" / "(4 variations)" counts map to entry counts. Sprites must be slices of the summer sheet
  (same shared-material constraint as biome tiles; reuse `World.IsUsable` foreign-texture rejection).

**`StructureSettings`** (`[Serializable]`, `World/StructureSettings.cs`) — live-tunable like `GroundSettings`:
- `int blockSize = 14` — region block edge in cells (density; smaller = denser). "Moderate" default.
- `float siteChance = 0.6f` — probability a block contains a site.
- `Color markerColor` — minimap marker tint for site cells.

The structure sprites are **full tiles** (icon + baked ground), so a site **replaces** the cell's tile.

### 3.2 Logic — `StructureGenerator` (`World/StructureGenerator.cs`, pure C#)

Parallel to `GroundGenerator`. Holds the seed + `StructureSettings`; depends on terrain queries
(`IsLand`, `GroundType At`) passed in by `World`.

**`StructureSite SiteAt(int cx, int cy)`** (returns null when the cell is not a site) — jittered region grid:
1. Block = `(floor(cx/blockSize), floor(cy/blockSize))`.
2. `if Hash01(block, seed, SALT_PRESENCE) >= siteChance` → no site in this block → return null.
3. Jittered candidate cell = block origin + `(floor(Hash01(block,seed,SALT_X) * blockSize),
   floor(Hash01(block,seed,SALT_Y) * blockSize))`.
4. `if candidate != (cx,cy)` → return null. (So each block yields ≤1 site cell; O(1) per query.)
5. Read terrain at the candidate: must be land. Gather `defs` whose `validOn` contains its `GroundType`.
   If none → return null. Weighted-pick one by `spawnWeight` using `Hash01(candidate,seed,SALT_TYPE)`.
6. Pick a sprite variant (weighted, `Hash01(...,SALT_VARIANT)`) and a name
   (`namePool[Hash to index]`, `SALT_NAME`).
7. Return `StructureSite { def, cell=(cx,cy), name, sprite }`.

All salts are distinct large constants so site placement does not correlate with the biome/ground noise.
Determinism: identical seed → identical sites on every client.

**`StructureSite`** (small **class** in the same file; reference type so `null` = no site): `def`, `cell`,
`name`, `sprite`.

### 3.3 Visual — `World` changes (no `CellRenderer` change)

- `LandSprite(cx,cy)`: consult `structureGen.SiteAt` **first**; if a site exists, return its `sprite`
  (overrides the biome variant). Else current behavior. `CellRenderer` already draws whatever `LandSprite`
  returns ([CellRenderer.cs:107]) — **zero renderer changes**.
- `LandColor(cx,cy)`: if a site exists, return `structureSettings.markerColor`; else current behavior →
  sites appear as dots on the minimap.
- New public `StructureSite SiteAt(int cx,int cy)` (cached like `landCache` if needed) for the encounter layer.

### 3.4 Wiring (`Game.cs`, `World.cs`, `TunerPanels.cs`)

- `Game`: add serialized `StructureSet structures` + `StructureSettings structureSettings`; pass both into
  `WorldConfig`. Add a `JsonPref<StructureSettings>` (mirrors biome/ground/water persistence).
- `WorldConfig`: add `StructureSet structures; StructureSettings structureSettings;`.
- `World.Rebuild()`: construct `structureGen` alongside `gen`/`groundGen`; clear the site cache.
- `TunerPanels`: add a "Structures" accordion (blockSize, siteChance sliders) mirroring the existing panels;
  changes call `Game.Regenerate()` (which already does `World.Rebuild()` + `view.Refresh()`).

### 3.5 Default defs (scaffolding; sprites assigned by Ryan in the Inspector)

| id | label | validOn | variants |
|----|-------|---------|----------|
| forest_village | the village | Forest | 1 |
| forest_home | a home | Forest | 2 |
| forest_town | the town | Forest | 2 |
| forest_mansion | the mansion | Forest | 1 |
| farm | a farm | Grass | 2 |
| camp | a camp | Grass, Forest | 2 |
| walled_town | the walled town | Grass, Forest | 4 |

`spawnWeight` defaults equal; tune later. (Setup of the `StructureSet` asset + sprite assignment is a manual
step; the plan will scaffold the 7 defs and Ryan drags in the 14 summer-sheet slices.)

## 4. Part B — Encounter loop

### 4.1 Logic — `EncounterManager` (new `MonoBehaviour`, game assembly; local-client only)

Added by `Game.Awake` via `AddComponent` (like `TunerPanels`), so no manual scene wiring. Finds the
`CommandConsole` via `FindFirstObjectByType` (like `CommandBootstrap` finds `RelayConnector`).

State: `bool inEncounter`, `Vector2Int lastCell`, `StructureSite current`.

Each `Update` (owner only):
- `cell = PlayerMovement.LocalInstance.CurrentCell()`.
- **Trigger** when `cell != lastCell && !inEncounter && World.SiteAt(cell) != null`:
  1. `current = SiteAt(cell)`; `inEncounter = true`.
  2. Halt movement: `PlayerMovement.LocalInstance.Halt()` → `HaltServerRpc` (clears server path/target +
     zeroes intent; WASD stops once the console opens). See §4.3.
  3. `CommandRouter.Instance.EnterEncounter()` (World→Encounter scope swap).
  4. `console.OpenLocked()` (open + lock; §4.4).
  5. `GameLog.Post(OutputType.Encounter, $"You approach {current.name}, {current.def.label}. (enter / leave)")`.
- Always set `lastCell = cell` at the end. (After `leave`, the player is still standing on the site cell;
  because `cell == lastCell`, it does **not** re-trigger until they step off and back on.)

`End()` (called by the `leave` command): `inEncounter = false`, `current = null`,
`CommandRouter.ExitEncounter()`, `console.Unlock()`. Exposes `EncounterManager.Instance.Current` so commands
can read the site name/label.

### 4.2 Commands (`CommandBootstrap.cs`, `CommandScope.Encounter`)

Mirror the existing `fight`/`flee` template ([CommandBootstrap.cs:37]):
- `enter` → `CommandResult.Ok($"A villager greets you to {Current.name}. \"Welcome, traveler.\"",
  keepOpen:true, output:Encounter)`.
- `leave` → `EncounterManager.Instance.End()` then `CommandResult.Ok("You leave.", keepOpen:false,
  output:Encounter)`. Ordering matters: `End()` clears the lock **before** the result returns, so the
  console's normal "Ok + !keepOpen → Close()" path can close it.
- v1: both available the whole encounter (no per-step gating). The demo `fight`/`attack`/`flee` may remain.

### 4.3 Netcode — `PlayerMovement.Halt()` + `HaltServerRpc`

The typing-lock zeroes WASD intent but an in-flight click-to-move path keeps running server-side
([PlayerMovement.cs:103]). Add `public void Halt()` (owner) → `HaltServerRpc()` that clears
`motion.path`, `motion.hasTarget`, and `moveInput.Value = Vector2.zero` on the server. This is the **only**
networking in v1.

### 4.4 Visual — `CommandConsole.cs` (UI assembly)

1. **Inline ghost autocomplete** — wire the existing seam (`Suggest()` [CommandConsole.cs:443] →
   `CommandRegistry.Suggest` [CommandRegistry.cs:56]):
   - In `Render()` (no-selection branch), compute `ghost = CommandRouter.Instance.Suggest(text)` and append
     it after the caret in gray (e.g. `<color=#FFFFFF60>{ghost}</color>`). Hidden automatically when the
     prefix is ambiguous or empty (`Suggest` returns `""`) — the user's "only on the for-sure words" rule.
   - **Tab**: if `ghost != ""`, insert it (accept completion). Add to the open-state key handling in
     `Update()`. (Inline ghost only — no list above the input.)
2. **Locked-open mode** — `public bool LockOpen` + make `Open()` callable (`OpenLocked()` = `Open()` +
   `LockOpen = true`; `Unlock()` = `LockOpen = false`). While `LockOpen`: Esc, click-outside, and
   empty-Enter must **not** close (guard those paths in `Update`/`HandleMouse`/`Confirm`). Only the `leave`
   command closes (it clears `LockOpen` via `End()` first). The console stays generic — it knows "locked",
   not "encounter".

### 4.5 Data — naming & text

- Name: deterministic `namePool[hash]` per site (§3.2). Same on every client; no sync.
- Intro/greet: template strings using `{name}` and `{def.label}` for v1. Per-encounter scripted text deferred.

## 5. Determinism & multiplayer

- Placement, variant, name, and minimap markers are all pure functions of `(cell, seed)` → no replication.
- The encounter popup, movement lock, and naming are **local/client-side**. Each player triggers only their
  own encounter (driven by `PlayerMovement.LocalInstance`).
- The single replicated action is `HaltServerRpc` (stop the server-authoritative path).
- Shared, persistent town memory is explicitly deferred (the hard networked part).

## 6. File manifest

**New:**
- `Assets/_Scripts/World/StructureSet.cs` — `StructureSet` SO + `StructureDef`.
- `Assets/_Scripts/World/StructureSettings.cs` — `StructureSettings`.
- `Assets/_Scripts/World/StructureGenerator.cs` — `StructureGenerator` + `StructureSite`.
- `Assets/_Scripts/EncounterManager.cs` — walk-on detection, lock, intro/greet orchestration.
- `Assets/_Structures/Structures.asset` — `StructureSet` instance (defs scaffolded; sprites by Ryan).

**Modified:**
- `World.cs` — `WorldConfig` fields; build `structureGen`; `SiteAt`; `LandSprite`/`LandColor` consult sites.
- `Game.cs` — serialized `StructureSet`/`StructureSettings`; pass to `WorldConfig`; `JsonPref`.
- `TunerPanels.cs` — Structures accordion (density sliders → `Regenerate`).
- `CommandConsole.cs` — ghost autocomplete + Tab; `LockOpen`/`Open()`/`Unlock()`.
- `CommandBootstrap.cs` — `enter`/`leave` encounter commands.
- `PlayerMovement.cs` — `Halt()` + `HaltServerRpc`.

## 7. Verification plan

- **EditMode unit tests** (`Assets/Tests/EditMode/`, existing harness): extend `CommandRegistryTests` for the
  ghost rule — ambiguous prefix → `""`, exact full word → `""`, unique prefix → completion suffix.
- **`StructureGenerator`** is in `Assembly-CSharp` (not reachable from the `Minifantasy.Commands` test
  asmdef), so determinism is verified by **manual play-mode** + an optional debug `sites` command (lists the
  nearest site + name). No asmdef restructuring in v1.
- **Play-mode** (per the project's unity-mcp flow — `execute_code` is broken; use refresh → read console →
  enter Play): sites appear on map + minimap; walking onto one locks movement and opens the popup; `ent`+Tab
  → `enter`; `enter` greets with the site name; `leave` closes + unlocks; stepping off and back on re-triggers.

## 8. Deferred / future

- Per-step command gating; branching dialogue (`talk`/`help`/`rob`/`quest`); encounter-script data format.
- Persistent + networked town memory keyed by `def.id` + block (who visited/helped/robbed); quests.
- Additional landmark types (bridges over water, caves in mountains, towers, castles, ruins); winter sheet.
- Structures as obstacles / enterable interiors.

## 9. Open assumptions

- Structure sprites are full summer-sheet tiles (confirmed) → replace model, no overlay.
- A site cell remains walkable (you stand on it to trigger); `IsWalkable` is unchanged.
- One site per block is enough spacing at "moderate" density; cross-block adjacency is rare and acceptable.
