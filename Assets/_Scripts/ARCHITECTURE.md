# Script architecture — Logic / Data / Visual

One-way dependency flow: **Input → Logic → Data → Visual.** This is a discipline of dependency
direction, not ECS. Managers/OOP are fine.

| Role | Names | Reads | Writes | Never |
|------|-------|-------|--------|-------|
| Data   | plain nouns / `*State`, `*Settings` | — | (mutated by logic) | no behavior; no refs to logic/visual |
| Logic  | `*System`, manager, `*Generator`    | input, data | data only | transforms/animators/materials/UI |
| Visual | `*View` (+ HUDs)                    | data | visual only | game data |

- **Update order enforces the arrow:** logic in `Update`, visual in `LateUpdate` (or `Game` ticks
  logic→visual in order), so visual sees the finished frame.
- **Flow:** continuous state → visual pulls each frame; discrete events → the `GameLog` event bus.
- **`Game`** is the composition root — the only place that wires across concerns.
- **Input lane:** device reading lives in logic/input helpers that write intent into data;
  input-driven UI (TunerPanels, CommandConsole) editing config/command data is part of this lane.

## System roles

- **Player/** — Data: `PlayerMotion`. Logic: `PlayerInput` (helper). Visual: `PlayerView`.
  (Movement is server-simulated in `Net/PlayerSimSystem`; `PlayerView` reads a ghost's transform → animator.)
- **Camera/** — Data: `CameraState`. Logic: `CameraSystem`. Visual: `CameraView` (sole camera writer).
- **Net/** — Data: `NetState`, `PlayerRegistry`/`ServerPlayer`, `SnapshotEntry`, `Aoi/ReplicationSettings`+`AoiPlayer`.
  Logic: `RelayConnector`, `ReplicationHub` (the only NetworkObject; routes RPCs + AOI snapshots), `PlayerSimSystem`,
  `Aoi/AreaOfInterestSystem`, `GhostManager`, `LocalPlayer`. Visual: `RelayTestHUD` + the Ghost prefab.
- **World/** — Data: `*Settings` + `World`'s cache. Logic: `BiomeGenerator`/`GroundGenerator` + `World` queries.
  Visual: `WorldView`/`CellRenderer`/`WaterMaterial`.
- **Commands/** — Data: `Command`/`CommandResult`/`CommandScope`/`OutputType`. Logic: `CommandRegistry`/
  `CommandRouter`. Visual: `CommandConsole`/`ChatPopup`. (Own assembly — keep it pure.)
- **Core/** — `GameLog` (data bus), `InputState` (data flag), `JsonPref` (persistence util).
- **Game.cs** — composition root: builds + ticks systems (logic) then views (visual).
