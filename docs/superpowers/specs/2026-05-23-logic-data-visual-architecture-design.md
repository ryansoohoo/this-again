# Logic / Data / Visual Architecture — Design

**Date:** 2026-05-23
**Status:** Approved (design); implementation plan to follow.

## Goal

Make every system follow a data-oriented-style separation of dependencies: a one-way
**Input → Logic → Data → Visual** flow. Logic writes data; visual reads data and writes only
visuals. This is *not* full ECS — managers/OOP stay; it's a discipline of dependency direction.
Preserve all current verified behavior (this is a restructure, not a feature change).

## Decisions (locked during brainstorming)

- **Data = plain C# state objects** (structs/classes), held by a manager/owner. No pools/ECS,
  no runtime-mutated ScriptableObjects.
- **Separate types per concern, minimal extra components** — each concern is its own class;
  only add extra MonoBehaviour components where it earns it (e.g. `PlayerView`).
- **Uniform pass across every system** — end state: all systems conform to one documented
  convention. Offenders get real restructuring; already-conformant systems are documented +
  lightly renamed only (no logic rewrite — see §7).
- **Codify by naming convention + a written rule** (`ARCHITECTURE.md`), not subfolders or a
  marker-interface framework.

## The contract (`Assets/_Scripts/ARCHITECTURE.md`)

One-way flow: **Input → Logic → Data → Visual.**

| Role | Names | May read | May write | Never |
|---|---|---|---|---|
| **Data** | plain nouns / `*State`, `*Settings` | — | (mutated by logic) | no behavior; no refs to logic/visual |
| **Logic** | `*System`, manager, `*Generator` | input, data | **data only** | never touches transform/animator/material/UI |
| **Visual** | `*View` (+ existing HUDs) | data | **visual only** | never mutates game data |

- **Update ordering enforces the arrow:** logic runs in `Update`; visual reads in `LateUpdate`
  (or `Game` ticks logic→visual in order), so visual always sees the finished frame's data.
- **Data-flow mechanisms:** continuous state → visual *pulls* from data each frame; discrete
  events → the `GameLog`-style event bus.
- **`Game` is the composition root** — the one place allowed to wire across concerns.
- **Input lane:** device reading lives in logic (or an input helper) that writes intent into
  data; input-driven UI (tuner/console) editing config/command data is part of the input lane.
- Existing well-named types keep their names; the doc records each one's role. New types take
  the role suffix.

## Per-system pass

| System | Data | Logic | Visual | Work |
|---|---|---|---|---|
| **Player** | `PlayerMotion` (server state) | `PlayerInput` (helper) + `PlayerMovement : NetworkBehaviour` | `PlayerView` | Restructure |
| **Camera** | `CameraState` (desired pos/ortho, bounds, follow) | `CameraSystem` (was `CameraRig`) | `CameraView` | Restructure |
| **Net** | `NetState` (status, joinCode) | `RelayConnector` | `RelayTestHUD` | Split data out |
| **World** | settings + `World` cache | generators + `World` queries | `WorldView`/`CellRenderer`/`WaterMaterial` | Conform (doc) |
| **Commands** | `Command`/`CommandResult`/`CommandScope`/`OutputType` | `CommandRegistry`/`CommandRouter` | `CommandConsole`/`ChatPopup` | Conform (doc) |
| **UI** | (reads others' data) | — | `Minimap`/`ChatPopup`/`CommandConsole`/`TunerPanels` | Conform (input-UI noted) |
| **Core** | `GameLog`/`InputState` | `JsonPref` | — | Conform (doc) |

## Player (the centerpiece)

Today `PlayerController : NetworkBehaviour` fuses owner-input, server movement, movement state,
and the animator. Split into:

- **`PlayerMotion`** (data, plain, server-side): `cell, moving, toCell, fromPos, toPos, moveT,
  stepDuration, hasTarget, targetCell, path (List<Vector2Int>), pathIndex`.
- **`PlayerInput`** (logic, plain helper): reads Keyboard/Mouse → returns intent (8-way `Vector2`
  dir + optional double-click world point). Holds only click-timing state.
- **`PlayerMovement : NetworkBehaviour`** (logic): owns the `moveInput` NetworkVariable +
  `PlayerMotion`. Owner → writes intent via `PlayerInput`; server → runs `Pathfinder` + the step
  loop, writing `PlayerMotion` **and the authoritative transform** (the replicated transform is
  *data*, not visual). Exposes `LocalInstance` + `CurrentCell()` (used by `Game` for the view).
- **`PlayerView` : MonoBehaviour** (visual, all clients): in `LateUpdate`, reads the replicated
  transform delta → derives `facing` → drives the animator (Speed/DirX/DirY). Holds only
  visual-local state (`facing`, `lastPos`). Touches nothing but the animator.

Two components on the Player prefab (`PlayerMovement`, `PlayerView`); two plain types
(`PlayerMotion`, `PlayerInput`).

**Netcode mapping:** server-logic writes networked data (authoritative transform via
NetworkTransform + the `moveInput` NetworkVariable); every client's visual reads it. `PlayerMotion`
stays server-only working state (clients only need the replicated transform). `facing` remains
client-derived in `PlayerView`. No new replication.

## Camera

Today `CameraRig` both computes the desired position/zoom and writes `cam.transform` directly.

- **`CameraState`** (data): inputs `Bounds` (set by `WorldView`) and `FollowTarget` (set by
  `Game`); outputs `desiredPosition` (Vector3) and `desiredOrthoSize` (float).
- **`CameraSystem`** (logic, was `CameraRig`): reads input + `CameraState` → computes desired
  position/zoom (pan, wheel-zoom-to-cursor, spacebar recenter, bounds clamp, pixel-perfect snap).
  Writes `CameraState` only; never touches the camera. May *read* screen/aspect metrics
  (environment, not game data). Keeps its private drag/recenter working state.
- **`CameraView`** (visual): the single place that writes `cam.transform.position` +
  `cam.orthographicSize`, from `CameraState`. `Game` calls `CameraSystem.Tick()` then
  `CameraView.Apply()` in order.

This is the thinnest split, but it makes the framing math testable and isolates the one
camera-write. (Kept per approval.)

## Net

- **`NetState`** (data, plain): `status` (string), `joinCode` (string).
- **`RelayConnector`** (logic): host/join async → writes `NetState` + posts progress to `GameLog`.
- **`RelayTestHUD`** (visual): reads `NetState` → IMGUI (instead of reading `RelayConnector`
  fields directly).

## Already-conformant systems (World, Commands, UI, Core)

These already obey the one-way rule. The uniform pass for them is **documentation in
`ARCHITECTURE.md` + light naming only — no logic rewrite.** Rationale: the terrain renderer and
the command framework are verified/tested; cosmetic renames there are risk with no behavioral
gain. The doc carries their role labels:

- **World:** `World` is the terrain logic/data layer (owns the cache + generators, exposes
  queries); `WorldView`/`CellRenderer`/`WaterMaterial` are visual; settings are data.
- **Commands:** `Command`/`CommandResult`/`CommandScope`/`OutputType` = data; `CommandRegistry`/
  `CommandRouter` = logic; `CommandConsole`/`ChatPopup` = visual (the reference example).
- **UI:** `Minimap`/`ChatPopup` are visual readers; `CommandConsole`/`TunerPanels` are
  input-lane visual (capture input → write command/settings data → trigger logic).
- **Core:** `GameLog` = data bus/event; `InputState` = data flag; `JsonPref` = persistence util.

## Phasing & verification

Each phase: refresh → read Console (0 errors) → Play/screenshot → commit. (`execute_code` is
broken; use the refresh→console→play chain. New `.cs` files need `refresh_unity scope=all`.)

| Phase | Change | Risk |
|---|---|---|
| **P0** | Write `ARCHITECTURE.md` (the contract) | none |
| **P1** | Camera split → `CameraState`/`CameraSystem`/`CameraView` | low–med |
| **P2** | Player split → `PlayerMotion`/`PlayerInput`/`PlayerMovement`/`PlayerView` | med (the big one) |
| **P3** | Net split → `NetState` | low |
| **P4** | Conformance docs + light naming for the rest | low |

**P2 Netcode check:** verify in Play that WASD step, double-click auto-move, pathfinding around
water, and walk/idle + 4-way facing animation all still work; the Player prefab keeps its
NetworkObject/NetworkTransform and the two components (`PlayerMovement`, `PlayerView`) with
serialized fields preserved (move `.cs`+`.meta` together if relocating; new components need
Inspector wiring on the prefab — call this out during execution).

## Out of scope

- Full ECS / struct-of-arrays pools (entity counts are tiny).
- Renaming already-conformant types for suffix-uniformity (e.g. `World`→`TerrainSystem`) — left
  as-is unless requested.
- Per-folder asmdefs (unchanged from the prior refactor decision).
