# Underworld Player Collision — Client-Side Prediction (Symmetric) — Design

- **Date:** 2026-05-24
- **Status:** Approved (brainstorming 2026-05-24) — implementation plan to follow (`writing-plans` next).
- **Parent:** Follow-up to `2026-05-24-underworld-player-collision-design.md`, which built the server-authoritative resolver and **explicitly deferred** client-side collision prediction ("Phase-4 territory") to here. That resolver was built pure/deterministic precisely so it could be promoted into client prediction **without a rewrite** — this design is that promotion.
- **Scope:** Predict player-vs-player push-apart on the **owner client** so you stop/slide against roommates **immediately** instead of overlapping-then-rubberbanding. Runs the existing pure `CollisionStep` inside `PredictionSystem` (live tick **and** reconcile replay), resolving the local player against the **rendered** positions of same-room ghosts, with **symmetric** mover weighting. Server stays the sole authority; reconcile eases the residual. **No wire / prefab / scene / `SnapshotEntry` changes. In-instance only.**

## 1. Goal

Collision today is **100% server-side**. The owner predicts attack+lunge+movement-vs-walls via the shared `InstanceStep`/`MovementStep` ([PredictionSystem.cs:58](Assets/_Scripts/Net/PredictionSystem.cs)), but **never runs `CollisionStep`**, and `Reconcile`/`ReplayFrom` replays through `MovementStep` only ([PredictionSystem.cs:104](Assets/_Scripts/Net/PredictionSystem.cs)). So the owner predicts as if the room is empty: walking into a roommate overlaps locally, and only the server's Phase-B resolve ([AttackSimSystem.cs:65](Assets/_Scripts/Net/AttackSimSystem.cs)) separates them — the push-off arrives one snapshot later and `Reconcile` eases it. That is the "small rubberband on contact" v1 deliberately accepted.

Add **owner-side prediction of the local player's own push-apart**, reusing the already-deterministic `CollisionStep` and the room's ghosts the client already receives (region-only AOI guarantees every roommate is in the snapshot). The owner resolves itself locally each predicted tick (and on replay) so contact feels responsive; the server remains authoritative and the existing `smoothOffset` absorbs divergence.

### Decisions (Ryan, brainstorming 2026-05-24)
- **Approach A+ — symmetric local resolve.** The owner builds a one-room `CollisionBody[]` of **itself + every same-room ghost** and runs `CollisionStep.Resolve`, keeping only its own resolved position. Mover weighting mirrors the server: **self** is a mover if its integration moved it this tick; a **remote** is a mover if its ghost moved between its last two snapshots. When both are movers the owner applies its **50/50 half** locally and the server applies the remote's half, so the next snapshot lines up. (Chosen over A "self-only mover": the impact moment, when both players are clearly walking, is exactly when symmetry matches the server best — and impact is what you feel.)
- **Collide against the RENDERED remote position** (`CurrentPos(g) = Lerp(fromPos, toPos, t)` — what's on screen), not the freshest snapshot target (`toPos`) and not an extrapolation. You stop exactly when sprites visually touch. Accepted cost: the rendered ghost is the most stale seed, so the resolve sits slightly behind the server → marginally more reconcile easing, and chasing a fleeing remote can briefly "catch" on its rendered-past position. This is the standard choice for predicting against *other* entities and the right feel trade.
- **Discard the remotes' resolved positions.** Only the local player moves; ghosts keep being driven by snapshot interpolation, so **remotes never jitter**. This is the whole reason A+ is clean where full-room prediction (Approach B) is not.
- **No wire / prefab / scene change, in-instance only.** Reuses the existing snapshot, the existing `cfg.collisionRadius`/`cfg.collisionIterations`, and `PredictionSystem.Walkable`. Overworld (prediction inactive there) is untouched.
- **Rejected — Approach B (full-room prediction).** Predicting every remote forward needs remote inputs/extrapolation, mismatches the interpolated ghosts, and risks visible remote jitter for accuracy reconcile already provides. YAGNI at friends-scale.

### Assumptions (reasonable defaults; flag if wrong)
- **Region-only AOI ⇒ every non-self ghost is a roommate.** While in-instance the snapshot carries only same-room players ([AreaOfInterestSystem](Assets/_Scripts/Net/Aoi/AreaOfInterestSystem.cs)), so "all remote ghosts" == "all roommates"; no room filtering needed on the client.
- **Self `invMass` matches the server's rule.** Server computes `invMass` from the **post-integration, pre-collision** displacement ([AttackSimSystem.cs:61](Assets/_Scripts/Net/AttackSimSystem.cs)). The client computes self `invMass` the same way: `(candidate p − prevPos).sqrMagnitude > MovedEps` *before* the local resolve. This auto-captures lunge (moving) vs windup-rooted (pinned).
- **Remote `invMass` is a best-effort heuristic.** The client infers it from the ghost's **post-collision snapshot** displacement (`toPos − fromPos`), which approximates the server's pre-collision integration flag. They agree at the moment of impact (both clearly moving) and diverge most in a **sustained mutual press** (snapshot deltas collapse to ~0 → client reads "pinned"); reconcile + `smoothOffset` absorb that residual. Perfect agreement would require remote inputs on the wire (Approach B) — rejected.
- **Replay seeds remotes at their current rendered position for all replayed ticks.** `ReplayFrom` spans only the unacked window (~RTT in 60 Hz ticks, typically a few ticks); remotes move ≤ a few cm across it, so per-tick remote history is unnecessary in v1.
- **Sim 60 Hz, snapshots 30 Hz.** `Time.fixedDeltaTime = 1/60` ([Game.cs:157](Assets/_Scripts/Game.cs)); `snapshotHz = 30` default ([ReplicationSettings.cs:12](Assets/_Scripts/Net/Aoi/ReplicationSettings.cs)) ⇒ ghosts render ~1 snapshot (~33 ms) + ½RTT in the past. `collisionRadius = 0.21`, `collisionIterations = 3` ([MovementSettings.cs:14](Assets/_Scripts/Net/MovementSettings.cs)).

### In scope (v1)
- Local-player collision resolve inside `PredictionSystem`, in **all three** step paths: `FixedTick` (movement-only), `FixedTickInstance` (with weapon), and `ReplayFrom` (reconcile).
- A read-only **`GhostManager` accessor** that yields same-room remote `CollisionBody`s (rendered pos + snapshot-delta `invMass`).
- Symmetric mover weighting (self + remote) reusing the unchanged `CollisionStep.Resolve`.
- Deterministic ordering on the client (sort the body set by id, as the server does).

### Out of scope (deferred)
- **Approach B / full-room prediction**, remote inputs on the wire, ghost extrapolation/extrapolated rendering.
- **Writing back remote positions** (server-owned) or any change to remote ghost rendering.
- **Per-tick remote position history** for replay (use current rendered pos).
- **Server-side rejection enforcement** (cooldowns/hits) — orthogonal; this is movement push-apart only.
- Hitboxes/damage, overworld/enemy collision (unchanged from the parent spec).

## 2. Fit with the existing pipeline (why it's shaped this way)

- **The resolver is already promotion-ready.** `CollisionStep.Resolve` is pure (no `Time`/random/Netcode/`Game`/physics), takes a `Func<Vector2,bool> walkable`, and is deterministic given id-sorted input + fixed iterations ([CollisionStep.cs:24](Assets/_Scripts/InstanceSim/CollisionStep.cs)). The client calls the **identical function** the server does, with the same `radius`/`iterations` and the same `walkable` predicate (`PredictionSystem.Walkable`). Zero resolver changes.
- **Prediction already has the seams.** `PredictionSystem` already steps on the fixed tick, stores per-tick frames in a ring, and replays unacked inputs on reconcile. Collision slots in **right after** the position is integrated, in the live tick and in replay — the same place the server runs Phase B relative to Phase A.
- **The client already has every roommate.** Region-only AOI replicates all same-room players to the owner, and `GhostManager` holds them with `fromPos`/`toPos`/interp `t` ([GhostManager.cs:14](Assets/_Scripts/Net/GhostManager.cs)) — exactly the data needed for rendered position + mover detection. No new wire.
- **Authority unchanged.** Server Phase A/B is untouched; the owner's local resolve is a *prediction* that the existing `Reconcile` corrects against the authoritative `worldPos` in the snapshot. The `snap`/`smoothOffset` paths already handle teleports and easing.
- **Assembly boundaries (do not cross):** `CollisionStep`/`CollisionBody` stay in pure `Minifantasy.InstanceSim`. The orchestration (collecting ghost bodies, mover detection, sort, read-back) lives in `Net`/Assembly-CSharp (`PredictionSystem`, `GhostManager`) — the client-side counterpart of `AttackSimSystem`'s server-side orchestration. No pure-core file references Netcode/`Game`.
- **Convention** (`Assets/_Scripts/ARCHITECTURE.md`): Input → Logic → Data → Visual. Reuses `CollisionBody` (Data) + `CollisionStep` (pure Logic); the new glue is client orchestration; no Visual change (ghosts render as before; only the self logical position shifts, then renders through the existing `VisualPos` path).

## 3. Component overview

```
 OWNER CLIENT (FixedUpdate, 60 Hz)                                 SERVER (unchanged)
 ────────────────────────────────                                  ──────────────────
 PredictionSystem.FixedTick / FixedTickInstance:
   1. integrate self  → candidate p     (InstanceStep/MovementStep, pure)   AttackSimSystem.StepInstanceFixed:
   2. self invMass = (p - prevPos) moved? 1 : 0   (pre-collision, as server)   Phase A integrate (per player)
   3. bodies = [ self{p,invMass} ] + GhostManager roommates                     Phase B resolve per room
        remote.pos     = CurrentPos(ghost)   ◄── RENDERED (on-screen)              CollisionStep.Resolve  ◄── same fn
        remote.invMass = (toPos - fromPos) moved? 1 : 0  ◄── snapshot delta        write sp.worldPos
   4. sort bodies by id  (deterministic, like server)                                  │
   5. CollisionStep.Resolve(bodies, n, Walkable, iterations)  ◄── same pure fn         └─ AOI snapshot (authoritative worldPos)
   6. Pos = self body's resolved pos   (discard remotes' results)                          │
   7. store frame{ input, predictedPos = Pos }; send InputCommand                          ▼
                                                                                  PredictionSystem.Reconcile:
 PredictionSystem.ReplayFrom (on snapshot):                                         compare acked.predictedPos vs authPos
   per replayed tick: MovementStep → (steps 2-6 again, remotes at current render)    within eps → no-op; else ReplayFrom + ease
```

| Unit | Side | Kind | Responsibility |
|------|------|------|----------------|
| `CollisionStep.Resolve` | shared | Logic (pure) | **Unchanged.** Mover-yields circle push-apart over a body set; deterministic given id-sorted input. |
| `PredictionSystem` (3 paths) | client | Logic | After integrating self, build self+roommate bodies, sort by id, `Resolve`, keep self's resolved pos. Live tick (×2) + replay. |
| `GhostManager` accessor | client | Logic/Data | Yield same-room remote `CollisionBody`s: `pos = CurrentPos(ghost)` (rendered), `radius = cfg.collisionRadius`, `invMass = (toPos−fromPos) moved ? 1 : 0`. |
| `CollisionBody` | shared | Data | **Unchanged.** `{ id, pos, radius, invMass }`. |

## 4. The local resolve (the new core)

A single private helper in `PredictionSystem`, called by all three step paths after the self position is integrated:

```
Vector2 ResolveSelf(Vector2 selfPos, float selfInvMass):
    var gm = GhostManager.Instance; var cfg = Game.Instance.MovementCfg;
    if gm == null: return selfPos
    int rc = gm.RoommateCount(localId)              // remotes only; excludes self
    if rc == 0: return selfPos                      // alone in room → nothing to resolve
    EnsureCapacity(rc + 1)                           // grow reusable _bodies[]; no per-tick alloc
    gm.FillRoommateBodies(localId, cfg.collisionRadius, _bodies)   // fills _bodies[0..rc): rendered pos + snapshot-delta invMass
    _bodies[rc] = new CollisionBody { id = localId, pos = selfPos, radius = cfg.collisionRadius, invMass = selfInvMass }
    int n = rc + 1
    SortById(_bodies, n)                             // deterministic order, matching the server
    CollisionStep.Resolve(_bodies, n, Walkable, cfg.collisionIterations)
    return FindById(_bodies, n, localId).pos         // read back ONLY self; discard remotes
```

- **`localId`** comes from `NetworkManager.Singleton.LocalClientId` (cached on `Activate`).
- **`selfInvMass`** is computed by the caller from the **pre-collision** integration delta: `(selfPos − prevPos).sqrMagnitude > MovedEps ? 1f : 0f` (same `MovedEps` constant/rule as `AttackSimSystem`).
- **Reusable `_bodies[]`** grows by `NextPowerOfTwo` like `AttackSimSystem._bodies`; no allocation on the steady path.
- **Sort + find by id**: `n` is tiny (≤ roommates+1); a simple insertion sort + linear find is fine and keeps client order identical to the server's id sort.

## 5. Integration points

1. **`FixedTick`** (movement-only, [PredictionSystem.cs:43](Assets/_Scripts/Net/PredictionSystem.cs)): after `Pos = MovementStep.Step(...)`, set `Pos = ResolveSelf(Pos, MovedSince(prevPos))`. Store the **resolved** `Pos` in the frame.
2. **`FixedTickInstance`** (with weapon, [PredictionSystem.cs:58](Assets/_Scripts/Net/PredictionSystem.cs)): after `InstanceStep.Step` produces `p`, set `Pos = ResolveSelf(p, MovedSince(prevPos))` and store the resolved `Pos`. (The frame's stored `input` stays the pre-collision effective move vector — `LungeVelocity ?? FreeMove` — so replay can re-`MovementStep` it.)
3. **`ReplayFrom`** ([PredictionSystem.cs:104](Assets/_Scripts/Net/PredictionSystem.cs)): per replayed tick `t`, after `p = MovementStep.Step(p, f.input, …)`, set `p = ResolveSelf(p, (p − pBeforeStep) moved?)`, then `f.predictedPos = p; buffer.Store(f)`. Remotes are seeded at their **current** rendered positions for every replayed tick (assumption §1).

The `snap`/`HardSnap` paths are unchanged (a teleport bypasses local resolve; the next live tick resolves normally).

## 6. `GhostManager` accessor

Encapsulate ghost internals; expose only what the resolve needs:

```
public int RoommateCount(ulong selfId)                       // count of ghosts whose id != selfId
public void FillRoommateBodies(ulong selfId, float radius, CollisionBody[] buf)
    // for each ghost g with id != selfId, in any order (caller sorts):
    //   buf[k] = { id, pos = CurrentPos(g), radius, invMass = (g.toPos - g.fromPos).sqrMagnitude > Eps ? 1f : 0f }
```

`CurrentPos` already exists as a private static ([GhostManager.cs:86](Assets/_Scripts/Net/GhostManager.cs)); these methods reuse it. No other `GhostManager` behavior changes; rendering/interp untouched. `PredictionSystem` gains a `GhostManager.Instance` dependency (acceptable — both are client-only).

## 7. Self-consistency: live tick == replay

The live tick stores `predictedPos = ResolveSelf(...)` (collision-resolved). `Reconcile` compares `acked.predictedPos` vs the server's authoritative (collision-resolved) `worldPos`; **`ReplayFrom` must also run `ResolveSelf`** or it would recompute collision-free positions and trigger a correction on **every** tick while in contact. Running collision in both paths is mandatory and is what makes "predicted == replayed == (approximately) authoritative" hold, so reconcile stays a no-op except for genuine divergence.

## 8. Netcode, determinism & performance

- **No wire / prefab / scene / `SnapshotEntry` change.** Pure client-side addition.
- **Determinism / divergence sources (both bounded, both reconciled):** (1) remote seed = rendered (stale) vs server's current positions; (2) remote `invMass` heuristic vs server's integration flag. The client sorts by id and uses identical `radius`/`iterations`/`walkable`, so the resolver math itself matches; only the *inputs* to it differ by the staleness above.
- **Known feel costs (accepted):** sustained mutual press may produce a small continuous correction (eased by `smoothOffset`) where the remote-mover heuristic degrades; chasing a fleeing remote can briefly catch on its rendered-past position then snap forward. Both are the chosen trade for stop-when-sprites-touch responsiveness.
- **CPU:** one extra `O(n²·iters)` resolve (n = roommates+1 ≤ a handful, iters = 3) per fixed tick, plus the same for each replayed tick (a few per snapshot). Negligible. Skipped entirely when alone in the room (`rc == 0`).
- **Topology:** unchanged.

## 9. File manifest

**Modified**
- `Assets/_Scripts/Net/PredictionSystem.cs` — `localId` cache; `ResolveSelf` helper + reusable `_bodies[]`; call it in `FixedTick`, `FixedTickInstance`, and `ReplayFrom`; id sort + read-back.
- `Assets/_Scripts/Net/GhostManager.cs` — `RoommateCount` + `FillRoommateBodies` (rendered pos + snapshot-delta `invMass`).

**New (if a unit-testable seam is worth it)**
- Possibly a tiny pure helper for `invMass`-from-delta to unit-test, or a `CollisionStepTests` case asserting the symmetric-split read-back — see §10. Decided in the plan.

**Unchanged (called out):** `CollisionStep`, `CollisionBody`, `InstanceStep`, `MovementStep`, `AttackSimSystem` (server), `SnapshotEntry`, `ReplicationHub`, AOI, prefabs, scene. No server behavior change.

## 10. Verification plan

Per the unity-mcp flow (`execute_code` broken; **refresh `scope=all` → poll `editor_state` ready → `read_console` clean → Play → screenshot**; MCP can't drive the IMGUI Host button):
- **Unit (EditMode, `Minifantasy.InstanceSim.Tests`):** the resolver math is already covered. New value here is small and mostly lives in client glue (singletons, hard to unit-test). Candidate pure assertions: self-mover-into-pinned-remote read-back pushes self only; both-movers read-back gives self its 50/50 half; alone-in-room (`rc==0`) is a no-op. If the glue can't be cleanly isolated from `GhostManager`/`Game`, document that and lean on the manual test rather than faking coverage.
- **MCP-verifiable:** compile clean; all EditMode assemblies green; Play boots with no console errors.
- **Manual host (documented — required; the real test):** two clients in one underworld room. Walking into a standing player now **stops/slides you immediately on your own screen** (no overlap-then-rubberband). Two players walking into each other meet symmetrically without deep interpenetration. Pressed-together jiggle stays small/eased. Chasing a fleeing player may catch slightly then smooth — confirm it's not jarring. Overworld still has no collision. Compare against the pre-change build to confirm the rubberband-on-contact is gone.

## 11. Open assumptions & deferred

- **Repo carries WIP** — commit only the two edited files (+ any new test) on their own; do **not** `git add` shared WIP (scene, prefabs, other settings/assets).
- **Remote `invMass` heuristic** (snapshot-delta) is the main approximation; if sustained-press jiggle is annoying in the playtest, the refinement is per-tick remote history and/or carrying a remote "is-moving" flag — *not* in v1.
- **Replay uses current rendered remote positions** for all replayed ticks; per-tick remote history is the documented refinement if long-RTT replay windows cause visible correction.
- **Deferred:** Approach B (full-room prediction), remote inputs/extrapolation, server rejection enforcement, hitboxes/damage, overworld/enemy collision.
