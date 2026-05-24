# Underworld Predicted Movement — Design

- **Date:** 2026-05-23
- **Status:** Approved (brainstorming) — ready for implementation plan
- **Scope:** Add client-side prediction + server reconciliation for **free** movement in the underworld only, and raise the global fixed + network tick. Overworld grid movement is untouched. Movement only — no combat.

## 1. Goal

This is the **client-side prediction/reconciliation deferred** by the interest-managed-replication spec
(§10 "No client-side prediction in v1", §12 deferred). Today the local player *is* an interpolated
`SelfGhost` ([GhostManager.cs:50](Assets/_Scripts/Net/GhostManager.cs)) — you press a key, the input goes
to the server ([LocalPlayer.cs:62](Assets/_Scripts/Net/LocalPlayer.cs) → `SubmitInputRpc`), the server steps
you ([PlayerSimSystem.cs:10](Assets/_Scripts/Net/PlayerSimSystem.cs)) and streams a snapshot, and only then
do you see yourself move. That full round-trip is fine for deliberate grid stepping but will feel floaty for
free analog movement.

Make underworld movement **clean and snappy**: the owner predicts its own free movement locally and
immediately, the server stays authoritative, and the client reconciles against the server's authoritative
position by replaying un-acked inputs. The overworld stays exactly as it is (grid, server-authoritative,
self-ghost).

### Decisions (Ryan, brainstorming 2026-05-23)
- **Combat is underworld-only and is OUT of scope here.** This spec is the movement foundation built *before*
  combat; the commitment/feint combat design is a later spec.
- **Hybrid movement:** overworld keeps grid/cell-step (unchanged); the underworld uses **free analog**
  movement (new). Chosen over all-grid and all-free.
- **Free underworld movement = client-side prediction + server reconciliation.** Chosen over
  owner-authoritative ("snappy but trust-only") and server-auth/no-prediction ("floaty"). Rationale: one
  authoritative world state, so future server-authoritative combat hit resolution has a single source of
  truth; also self-heals desyncs and won't need ripping out if the underworld ever goes public.
- **Kinematic mover, not `Rigidbody2D`.** A shared, deterministic `(pos, input, dt) → pos` step is required
  for replay to match the server; PhysX/Box2D float results are not deterministic across machines. Collision
  in v1 is simple room-bounds / walkability, which a kinematic mover handles fine.
- **Circular ring buffer** for the input/state history: fixed power-of-two capacity, indexed by
  `tick & (capacity-1)`, zero per-frame allocation (keeps the GC-conscious profile).
- **Raise the tick globally, set once at startup, not per region** (the conclusion from the tick-rate
  discussion — a single NetworkManager cannot run two tick rates at once, and runtime changes reset NGO's
  tick alignment): NGO `NetworkConfig.TickRate` 30→60, `ReplicationSettings.snapshotHz` 15→30,
  `Time.fixedDeltaTime` 0.02→1/60.
- **No lag-compensation rewind, no anti-cheat hardening.** Loose server validation is enough at friends
  scale; deferred.

### In scope (v1)
- A deterministic, shared kinematic movement step used by both the client prediction and the server sim.
- Owner-side prediction loop for in-instance players: per-fixed-tick input sampling, immediate local
  integration, a circular ring buffer of `(tick, input, predictedPos)`, tick-stamped input sent to the server.
- Server-side authoritative free sim for in-instance players, processing tick-stamped inputs in order and
  reporting the last processed tick.
- Reconciliation: compare predicted-pos-at-acked-tick to the authoritative self position; snap + replay
  un-acked inputs on mismatch; smooth small corrections.
- Render integration: in-instance, the self transform is driven by prediction (not the self-ghost); remote
  players and the overworld self-ghost are unchanged.
- The global tick raise.

### Out of scope (deferred)
- **All combat** (attacks, feint, block/parry/dodge, hitboxes, posture) — separate spec.
- **Overworld changes** — grid movement, its input path, and its self-ghost stay as-is.
- **Remote-player prediction** — other players in your room remain interpolated ghosts (in the past). Only
  *your own* player is predicted.
- **Lag-compensation rewind**, snapshot deltas, unreliable channels, and any anti-cheat beyond loose
  speed/teleport sanity checks.
- **Complex dungeon collision** — v1 is the existing flat 24×24 room (interior bounds + water moat as walls).

## 2. Fit with the existing pipeline

- **Current model (verified):** server-authoritative, no prediction. `ReplicationHub.Update`
  ([ReplicationHub.cs:63](Assets/_Scripts/Net/ReplicationHub.cs)) runs `PlayerSimSystem.StepAll` every frame
  (variable `Time.deltaTime`) and sends per-viewer `SnapshotClientRpc`
  ([ReplicationHub.cs:114](Assets/_Scripts/Net/ReplicationHub.cs)) at `snapshotHz`. Input arrives via
  `SubmitInputRpc(Vector2)` → `ServerPlayer.submittedInput`
  ([PlayerRegistry.cs:14](Assets/_Scripts/Net/PlayerRegistry.cs)). The client renders everyone — including
  itself — as an interpolated ghost ([GhostManager.cs:31](Assets/_Scripts/Net/GhostManager.cs)).
- **Why prediction needs new plumbing:** reconciliation requires (a) **tick-stamped inputs** (the current
  input is an on-change `Vector2`, no tick) and (b) a per-owner **last-processed-tick ack** on the snapshot
  (today's `SnapshotEntry` is `id,x,y,flags` only — [SnapshotEntry.cs:5](Assets/_Scripts/Net/SnapshotEntry.cs)).
- **Determinism foundation:** the free sim must step at a **fixed timestep** on both sides (the current sim's
  per-frame variable `dt` cannot be replayed deterministically). This is exactly why the fixed tick is being
  raised — `Time.fixedDeltaTime` becomes the sim/input tick period.
- **Architecture convention** (`Assets/_Scripts/ARCHITECTURE.md`): Input → Logic → Data → Visual; Data =
  `*State`/`*Settings`, Logic = `*System`, Visual = `*View`. New types follow this.
- **Assembly boundaries (do not cross):**
  - `Minifantasy.Commands` (`Assets/_Scripts/Commands/`) — add no new types.
  - The **Aoi asmdef** (`Assets/_Scripts/Net/Aoi/`, pure) holds `ReplicationSettings` +
    `AreaOfInterestSystem` + their tests. Prediction settings/logic reference the game and Unity input, so
    they live in **`Assembly-CSharp`** (`Net/`), not the Aoi asmdef.
- **Topology unchanged:** Relay host-is-a-player; the only NetworkObject is the scene `ReplicationHub`.

## 3. Component overview

```
 OWNER (client, in-instance)            SERVER (host)                       OTHER CLIENTS
 ──────────────────────────             ───────────                         ─────────────
 PredictionSystem (FixedUpdate @ fixedDeltaTime):
   sample input → tick++                ReplicationHub.Update:
   MovementStep(pos,input,dt) ──┐         in-instance players step on the
   store {tick,input,pos} in     │        same fixed tick, consuming the
   InputRingBuffer               │        per-player tick-stamped input buffer
   Hub.SubmitInputTickRpc(tick,input) ─► PlayerSimSystem free-integrate (MovementStep)
                                 │        records lastProcessedTick
   render self = predicted pos   │        writes authoritative worldPos
                                 ▼
   Reconcile(authPos, ackTick) ◄── Snapshot (self x,y + ackTick) ──► GhostManager.Apply (others)
     buf[ackTick].pos vs authPos:                                      interpolate ghosts (unchanged)
       error>eps → pos=authPos, replay inputs>ackTick, smooth
```

| Unit | Side | Kind | Responsibility |
|------|------|------|----------------|
| `MovementStep` | shared | Logic (pure static) | The single deterministic kinematic step `(pos, input, dt) → pos` + collision clamp. Used by client predict/replay **and** server sim. |
| `InputRingBuffer` | client | Data | Fixed power-of-two ring of `InputFrame { uint tick; Vector2 input; Vector2 predictedPos; }`, masked index, alloc-free. |
| `PredictionSystem` | client | Logic | In-instance: fixed-tick input sample → predict → store → send; reconcile on snapshot; smooth corrections; own the self transform. |
| `PlayerSimSystem` | server | Logic | **Extended**: branch on `inInstance` — grid step (overworld, unchanged) vs free-integrate via `MovementStep` (underworld), tracking `lastProcessedTick`. |
| `ReplicationHub` | both | `NetworkBehaviour` | **Extended**: new `SubmitInputTickRpc(uint,Vector2)`; per-owner input buffer; carry `ackTick` to the owner on the snapshot. |
| `GhostManager` | client | Logic | **Extended**: suppress the self-ghost while in-instance (the predicted self owns the transform); remote ghosts unchanged. |
| `MovementSettings` | both/data | Data (`JsonPref`) | Buffer capacity, reconcile epsilon, correction smooth time, move speed. Assembly-CSharp (not the Aoi asmdef). |

## 4. Data

### 4.1 `InputFrame` + `InputRingBuffer` (`Net/InputRingBuffer.cs`, Data)
```
struct InputFrame { uint tick; Vector2 input; Vector2 predictedPos; }
```
- Backing array of `InputFrame` with **power-of-two `Capacity`** (default **128** → ~2.1 s at 60 Hz, far
  exceeds any friends-scale un-acked window). Index = `tick & (Capacity-1)`.
- `Store(frame)` overwrites the slot. `TryGet(tick, out frame)` returns false if the stored slot's `tick`
  doesn't match (slot was lapped — only happens past the 2 s window → treated as a hard snap).
- No allocation after construction.

### 4.2 `MovementSettings` (`Net/MovementSettings.cs`, `[Serializable]`, `JsonPref` "movement.json")
- `float moveSpeed = 4` — matches the Hub's current `moveSpeed` ([ReplicationHub.cs:14](Assets/_Scripts/Net/ReplicationHub.cs)).
- `float reconcileEpsilon = 0.05` — position error (world units) below which no correction is applied.
- `float correctionSmoothTime = 0.1` — seconds to visually absorb a correction (render offset → 0), so
  reconciliation never hard-snaps under normal jitter.
- `int inputBufferCapacity = 128` — ring size (power of two).
- Tunable via the existing `TunerPanels` pattern; persisted with `JsonPref` like `ReplicationSettings`.

### 4.3 `SnapshotEntry` (extended, `Net/SnapshotEntry.cs`)
- Unchanged per-entry (`id,x,y,flags`). The **ack** is sent once per snapshot, not per entry (bandwidth):
  `SnapshotClientRpc` gains a `uint ackTick` parameter (the recipient's last processed input tick; `0` for
  overworld/non-predicted). The recipient's authoritative position is its own self entry `(x,y)`, already
  present.

### 4.4 `ServerPlayer` (extended, `Net/PlayerRegistry.cs`)
- Add: a small **server-side input buffer** (ring or sorted) of recent `(tick, input)` from the owner, and
  `uint lastProcessedTick`. Free-mode fields (current velocity if needed). Grid fields untouched.

## 5. Logic

### 5.1 `MovementStep` (`Player/MovementStep.cs`, pure static — the determinism keystone)
- `static Vector2 Step(Vector2 pos, Vector2 input, float dt, float speed, Func<int,int,bool> isWalkable)`:
  `dir = input.sqrMagnitude > 1 ? input.normalized : input; next = pos + dir * speed * dt;` then
  **collision clamp** against the room (reject moves into non-walkable/water cells via `isWalkable`; slide
  along axis-aligned walls). `speed` comes from `MovementSettings.moveSpeed`; the walkability delegate is
  supplied by the caller — client and server both pass the same `Game.IsWalkable` the grid sim uses — so the
  function stays pure (no globals, no `Time.*`).
- **Must be called identically** by client prediction, client replay, and server sim. No `Time.*`, no
  randomness, no physics — inputs in, position out.

### 5.2 `PredictionSystem` (`Net/PredictionSystem.cs`, client logic; runs only while in-instance)
- **FixedUpdate (period = `Time.fixedDeltaTime`):**
  1. `tick++` (client tick counter).
  2. Sample held movement input (level-triggered WASD — safe to read in FixedUpdate; edge-triggered combat
     inputs are a later concern).
  3. `pos = MovementStep.Step(pos, input, dt, world)` — immediate local prediction.
  4. `buffer.Store({tick, input, pos})`.
  5. `Hub.SubmitInputTickRpc(tick, input)` (see §5.4; include a short trailing window for loss tolerance).
- **On snapshot (`Reconcile(authPos, ackTick)`):**
  - `if (!buffer.TryGet(ackTick, out f))` → hard snap to `authPos` (lapped buffer / first frame).
  - `error = distance(f.predictedPos, authPos)`. If `error ≤ epsilon` → done.
  - Else: `pos = authPos`; **replay** every buffered frame with `tick > ackTick` through `MovementStep`,
    rewriting their `predictedPos`; set the new current `pos`. Add the pre-correction visual delta to a
    **render offset** that decays to zero over `correctionSmoothTime` (smooth, not snap).
- **Owns the self transform** while in-instance; exposes `SelfWorldPos` to the camera/`LocalPlayer`.

### 5.3 `PlayerSimSystem` (extended, server)
- `StepAll` keeps stepping **overworld** players per-frame exactly as today.
- **In-instance** players step on a **fixed accumulator at `Time.fixedDeltaTime`**: pop the owner's buffered
  input for the next tick (or repeat the last input if it hasn't arrived — small jitter tolerance), run
  `MovementStep`, write `worldPos`, set `lastProcessedTick = tick`. Loose validation: clamp per-tick
  displacement to `moveSpeed*dt*(1+margin)` (anti-teleport).

### 5.4 `ReplicationHub` (extended)
- **New** `SubmitInputTickRpc(uint tick, Vector2 input, RpcParams)` ([SendTo.Server, Everyone], caller =
  `SenderClientId`): append to that player's server-side input buffer (ignore ticks ≤ `lastProcessedTick`).
  The existing `SubmitInputRpc` stays for overworld.
- **Snapshot send** ([ReplicationHub.cs:78](Assets/_Scripts/Net/ReplicationHub.cs)): pass each viewer's
  `lastProcessedTick` as the `ackTick` argument to `SnapshotClientRpc`. `SnapshotClientRpc` forwards
  `ackTick` so the client calls `PredictionSystem.Reconcile(selfEntry.pos, ackTick)` while in-instance.
- Enter/leave RPCs ([ReplicationHub.cs:141](Assets/_Scripts/Net/ReplicationHub.cs)) are unchanged in
  behavior; they already set `inInstance`/`regionKey` and `Teleport` with the `snap` flag.

### 5.5 `GhostManager` (extended)
- When the self entry carries `InInstanceBit`, **do not** create/drive the self-ghost (the `PredictionSystem`
  owns the self transform). Remote ghosts and the overworld self-ghost are unchanged. On a `snap`-flagged
  self entry (teleport in/out), hand the authoritative position to `PredictionSystem` to reset its buffer +
  position.

## 6. Tick configuration (global, set once at startup)

| Knob | From | To | Where |
|------|------|----|-------|
| NGO `NetworkConfig.TickRate` | 30 (default) | **60** | `NetworkManager` in `SampleScene` (Inspector) or set in code before start. |
| `Time.fixedDeltaTime` | 0.02 (50 Hz) | **1/60 (~0.0167)** | `Game` startup, beside `Application.targetFrameRate = 120` ([Game.cs:143](Assets/_Scripts/Game.cs)). |
| `ReplicationSettings.snapshotHz` | 15 | **30** | `replication.json` default; the field's `[Range(1,30)]` ([ReplicationSettings.cs:12](Assets/_Scripts/Net/Aoi/ReplicationSettings.cs)) already permits 30 (its current max). |

- Global (one value for the whole sim), set once — never toggled per region.
- **Cost:** negligible. 6 players at 60 Hz net tick + 30 Hz snapshot is well within the CPU headroom from the
  performance work; the renderer is not the bottleneck. The 30 Hz snapshot only affects how often the owner
  *reconciles* and how fresh remote ghosts are — prediction makes the local feel instant regardless.
- The owner's prediction tick is the FixedUpdate count at `Time.fixedDeltaTime`; aligning the net tick to the
  same 60 Hz keeps input cadence and sim cadence matched.

## 7. Mode switching (enter / leave instance)

- The server side already exists: `EnterInstanceRpc`/`LeaveInstanceRpc` set `regionKey`, `inInstance`, and
  `Teleport` with `snap`.
- **Client reacts to the self entry's `InInstanceBit`:**
  - off→on (entered): `PredictionSystem` activates, seeds `pos` from the snap'd authoritative position,
    clears the ring buffer, resets `tick`; `GhostManager` stops drawing the self-ghost.
  - on→off (left): `PredictionSystem` deactivates; `GhostManager` resumes the self-ghost; overworld grid
    feel returns.
- No new RPCs for the switch — it rides the existing snapshot `InInstanceBit` + `snap`.

## 8. Determinism, multiplayer & performance

- **Determinism** rests entirely on the single `MovementStep` + fixed timestep + integer tick alignment.
  Kinematic only; no `Rigidbody2D`, no `Time.*` inside the step, no per-frame `dt`.
- **Snappy** owner feel: local prediction applies input the same FixedUpdate it is sampled; the round trip is
  hidden. Corrections smooth over `correctionSmoothTime` so jitter never snaps.
- **Remote players** stay interpolated ghosts (≈one snapshot in the past) — unchanged, and acceptable for
  traversal; combat-relevant interpolation tuning is a later (combat) concern.
- **Bandwidth:** owner→server adds one tiny tick-stamped input per fixed tick (with a short redundancy
  window); server→owner adds a single `uint` per snapshot. Trivial at friends scale; does not regress the
  AOI scaling story.
- **Loss tolerance:** owner sends a trailing window of recent inputs each tick; the server applies any it
  hasn't processed. A dropped snapshot just delays one reconcile — prediction keeps moving.

## 9. File manifest

**New**
- `Assets/_Scripts/Player/MovementStep.cs` — pure deterministic kinematic step + collision (shared).
- `Assets/_Scripts/Net/InputRingBuffer.cs` — `InputFrame` + power-of-two ring buffer.
- `Assets/_Scripts/Net/PredictionSystem.cs` — client predict/store/send + reconcile + self transform.
- `Assets/_Scripts/Net/MovementSettings.cs` — prediction tunables (`JsonPref` "movement.json").

**Modified**
- `Assets/_Scripts/Net/ReplicationHub.cs` — `SubmitInputTickRpc`; per-player server input buffer; `ackTick`
  on `SnapshotClientRpc`; fixed-accumulator free sim for in-instance players (or delegate to `PlayerSimSystem`).
- `Assets/_Scripts/Net/PlayerSimSystem.cs` — `inInstance` branch: free-integrate via `MovementStep` on the
  fixed tick; track `lastProcessedTick`; loose displacement validation.
- `Assets/_Scripts/Net/PlayerRegistry.cs` — `ServerPlayer`: server input buffer + `lastProcessedTick`.
- `Assets/_Scripts/Net/SnapshotEntry.cs` / `SnapshotClientRpc` — carry `ackTick` (per-snapshot, not per-entry).
- `Assets/_Scripts/Net/GhostManager.cs` — suppress the self-ghost while in-instance; route snap resets to
  `PredictionSystem`.
- `Assets/_Scripts/Net/LocalPlayer.cs` — in-instance, read `SelfWorldPos`/`CurrentCell` from
  `PredictionSystem` instead of the self-ghost; overworld path unchanged.
- `Assets/_Scripts/Game.cs` — set `Time.fixedDeltaTime = 1/60` at startup; own `MovementSettings`
  (`movement.json`); wire `PredictionSystem`.
- `Assets/Scenes/SampleScene.unity` — `NetworkManager.NetworkConfig.TickRate = 60`; place the
  `PredictionSystem` object if it is a scene singleton (mirroring `GhostManager`/`LocalPlayer`).
- `Assets/_Scripts/Net/Aoi/ReplicationSettings.cs` — `snapshotHz` default 15→30 (within the existing range).
- `Assets/_Scripts/UI/TunerPanels.cs` — a "Movement" accordion (epsilon, smooth time, speed) + save.

## 10. Phasing

1. **Tick raise + fixed-step sim (no prediction yet).** Set the three tick knobs; move the in-instance server
   sim onto a fixed accumulator using `MovementStep`; free movement works server-authoritatively (still
   floaty). *Deliverable:* you can free-move in the room (server-auth), overworld unchanged, console clean.
2. **Tick-stamped input + ack plumbing.** Add `SubmitInputTickRpc`, the server input buffer,
   `lastProcessedTick`, and `ackTick` on the snapshot. *Deliverable:* the owner's input is tick-correlated and
   the ack arrives (verify by logging predicted-vs-auth error — still no correction applied).
3. **Prediction + reconciliation + render.** `PredictionSystem` predicts locally, owns the self transform,
   reconciles with replay + smoothing; `GhostManager` suppresses the self-ghost in-instance.
   *Deliverable:* underworld movement is immediate; corrections are invisible under normal conditions.

## 11. Verification plan

Per the project's unity-mcp flow (`execute_code` is broken; use **refresh `scope=all` → poll
`editor_state.isCompiling` → `read_console` clean → enter Play → screenshot**; the MCP screenshot captures
the camera, not IMGUI):

- **Multi-client harness:** Unity **Multiplayer Play Mode** (being installed) — one main + virtual player(s)
  to get host + client locally without the manual-Host-button limitation MCP hits.
- **Phase 1:** in the underworld room, WASD produces free (non-grid) movement; the overworld still grid-steps;
  console clean; `NetworkConfig.TickRate == 60`, `Time.fixedDeltaTime ≈ 0.0167`, `snapshotHz == 30`.
- **Phase 2:** log `error = |predictedPos[ackTick] − authPos|` each snapshot; on a LAN/local host it stays
  ≈0; inputs and acks advance monotonically.
- **Phase 3:** with artificial latency (NGO/Unity Transport debug simulator), the **owner does not
  rubber-band** (movement stays immediate); corrections are smooth, not snapping; a second client sees the
  mover as a smooth interpolated ghost; entering/leaving the room switches feel cleanly (predicted ↔ grid)
  with a clean teleport (no interpolation streak).
- **Determinism check:** a focused edit-mode test feeding an identical `(input, dt)` sequence through
  `MovementStep` from two start states yields identical paths (the property reconciliation relies on).

## 12. Open assumptions & deferred

- **Edit-mode tests for `MovementStep`** belong in the existing test setup (mirroring the world-gen/AOI tests)
  — but **not** in the `Minifantasy.Commands` or Aoi asmdefs. Add an Assembly-CSharp-reachable test asmdef if
  needed.
- **Repo has uncommitted WIP** (usual for this project). Don't bundle shared files (scene, settings) into
  unrelated commits; commit the new prediction files on their own.
- **Input sampling in FixedUpdate** is fine for level-triggered movement; **edge-triggered combat inputs**
  (attack/feint/dodge taps) will need Update-buffered capture consumed on the fixed tick — handled in the
  combat spec, not here.
- **Server free-sim location:** §5.3 puts it in `PlayerSimSystem` driven by a fixed accumulator inside
  `ReplicationHub.Update`; if cleaner, the in-instance fixed loop can live directly in the Hub. Decide during
  planning.
- **`snapshotHz > 30`** would require widening the `[Range(1,30)]` cap; 30 is sufficient because prediction,
  not snapshot rate, drives owner responsiveness. Revisit only if remote-ghost smoothness in combat demands it.
- **Anti-cheat:** only loose per-tick displacement clamping in v1. Full validation/lag-comp is deferred with
  combat.
