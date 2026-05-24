# Underworld Player-vs-Player Collision — Kinematic, Deterministic — Design

- **Date:** 2026-05-24
- **Status:** Approved (brainstorming 2026-05-24) — implementation plan written (`docs/superpowers/plans/2026-05-24-underworld-player-collision.md`)
- **Scope:** Stop in-instance (underworld) players from walking through each other. A **pure, deterministic, kinematic** circle resolver runs **server-authoritatively** each fixed tick after movement integration; clients render the resolved positions through the existing snapshot stream and reconcile. The resolver and a per-room **body store + circle-overlap query** are built as the reusable **broadphase foundation** for the future pixel-perfect hitbox layer. **No client-side collision prediction, no hitboxes/damage, no overworld collision in v1.**

## 1. Goal

Today there is **zero entity-vs-entity collision** anywhere. Movement collision is **walls only**: `MovementStep.Step` does a per-axis slide against a `walkable(worldPos)` predicate ([Net/Movement/MovementStep.cs:18](Assets/_Scripts/Net/Movement/MovementStep.cs)). Two players in the same underworld room walk straight through each other.

Add **player-vs-player collision for in-instance players only**, reusing the determinism model already in place (`InstanceStep`/`MovementStep` are pure, fixed-tick, and shared by client predict / client replay / server sim). The resolver must be deterministic and stat-extensible because it is the foundation the future **hitbox** system builds on.

### Decisions (Ryan, brainstorming 2026-05-24)
- **Server-authoritative only (v1).** The server resolves overlaps each fixed tick; clients keep predicting their own movement-vs-walls and the existing `Reconcile()` eases the push-off. This matches the current reality — movement prediction exists but collision is not predicted — and matches the research: clients render remote players *in the past*, so only the server can authoritatively resolve, with reconcile smoothing the correction. The resolver is still built **pure + deterministic** so it can later be promoted into client prediction (Phase-4-style) without a rewrite.
- **Soft push-apart, mover-yields.** Overlapping players separate **along the line between centers** (so a mover *slides around* rather than dead-stopping). **Stationary players hold their ground** (they absorb none of the separation); whoever moved into the occupied space takes the push. Two movers split it 50/50. This is lower-grief than symmetric jostle and is the exact shape a future knockback/mass stat plugs into.
- **Circle body for movement collision.** A coarse circle per player — correct and standard for top-down action. Pixel-perfect movement collision is explicitly *not* wanted (characters would snag on each other's silhouettes).
- **Pixel-perfect is the hitbox layer, not collision.** Hitboxes (future) test against the victim's **opaque human-sprite pixels** (the weapon layer excluded), per animation frame — a separate, heavier *narrowphase*. v1 builds the shared **broadphase** (per-room circle bodies + an overlap query) so the hitbox narrowphase plugs in on top with no rework.
- **In-instance only.** Overworld movement (grid/click via `PlayerSimSystem`) is untouched; no collision there.

### Assumptions (reasonable defaults; flag if wrong)
- **One cell = one world unit** (`cellSizePixels 16` / `pixelsPerUnit 16` → `CellWorld == 1.0`, [Game.cs:118](Assets/_Scripts/Game.cs)). Default `collisionRadius = 0.4` → two players settle ~0.8 units apart, comfortably inside a 1-unit cell and well within the 24×24 room.
- **"Moved this tick" decides pinned vs mover.** A player whose net displacement over the ticks processed this `FixedUpdate` is below an epsilon is **pinned** (`invMass = 0`); otherwise a **mover** (`invMass = 1`). A wall-blocked player (slid to ~zero) correctly reads as pinned.
- **Resolve once per `FixedUpdate`** on every in-instance player's latest integrated position. The jitter-buffered input design keeps players within ~1 tick of each other, so a single post-integration resolve is accurate; a stricter per-shared-tick resolve is a documented future refinement.
- **Walls always win.** After a push, a body is re-validated against `walkable` with the same per-axis slide as `MovementStep`, so it can never be shoved into the moat; residual overlap when wedged between a wall and a pinned player is tolerated (clears as bodies shuffle).
- **Float determinism is moot in v1** because only the server resolves. Sorted-by-id iteration + a fixed iteration count keep the result reproducible for unit tests and for a future client-prediction promotion.

### In scope (v1)
- A pure, deterministic **`CollisionStep`** (in `Minifantasy.InstanceSim`): mover-yields circle push-apart with wall re-validation, plus a **circle-overlap query** (the hitbox broadphase seam).
- A **`CollisionBody`** value type (`id`, `pos`, `radius`, `invMass`) — the per-room body representation; `invMass` is the knockback/mass hook.
- **Two-phase server tick**: `AttackSimSystem.StepInstanceFixed` integrates every in-instance player (as today), then groups by `regionKey` and resolves each room.
- Two **`MovementSettings`** knobs: `collisionRadius`, `collisionIterations`.
- EditMode unit tests for `CollisionStep`.

### Out of scope (deferred)
- **Client-side prediction of collision** (Phase-4 territory: predict against extrapolated remote ghosts + reconcile). v1 relies on server authority + the existing reconcile.
- **Hitboxes / hurtboxes / damage**, the **pixel-perfect human-sprite narrowphase**, per-frame opaque-pixel masks, weapon-layer exclusion. The broadphase `Overlap` query is the only seam built now.
- **Overworld collision**, NPC/enemy bodies (no enemies yet), variable per-entity radius/mass *values* (the per-body fields exist; v1 fills them from one config + binary pinned/mover).
- **Per-shared-tick resolution** under catch-up storms.

## 2. Fit with the existing pipeline (why it's shaped this way)

- **Determinism keystone.** `MovementStep` ([Net/Movement/MovementStep.cs](Assets/_Scripts/Net/Movement/MovementStep.cs)) and `InstanceStep` ([InstanceSim/InstanceStep.cs](Assets/_Scripts/InstanceSim/InstanceStep.cs)) are pure (no `Time`/random/Netcode/physics) and shared by predict/replay/server. `CollisionStep` joins them as a pure function in the same `Minifantasy.InstanceSim` asmdef, unit-tested the same way.
- **Per-player step can't host collision.** `InstanceStep.Step(ref atk, ref pos, …)` sees exactly **one** player ([AttackSimSystem.cs:20-37](Assets/_Scripts/Net/AttackSimSystem.cs)). Player-vs-player needs *all* roommates at once, so resolution is a **room-level pass after** per-player integration — never inside `InstanceStep`/`MovementStep`.
- **Rooms are already grouped + fully visible.** `regionKey` identifies a room (interior origin; `(0,0)` = overworld) ([PlayerRegistry.cs:11](Assets/_Scripts/Net/PlayerRegistry.cs)), and in-instance AOI is **region-only** so the server already replicates every roommate to every roommate ([AreaOfInterestSystem.cs](Assets/_Scripts/Net/Aoi/AreaOfInterestSystem.cs)). The server holds every position; grouping by `regionKey` is the whole broadphase, and it's the same set the hitbox sweep will use.
- **No physics, on purpose.** `MovementStep`'s own comment: *"no physics — this purity is what makes reconciliation's replay match the server."* A Unity `Rigidbody2D` solution would be non-deterministic, impure, and unusable in replay — rejected.
- **No wire changes needed.** Resolved `worldPos` flows through the existing `SnapshotEntry` stream ([ReplicationHub.cs:106](Assets/_Scripts/Net/ReplicationHub.cs)); the owner's `PredictionSystem.Reconcile` ([PredictionSystem.cs:77](Assets/_Scripts/Net/PredictionSystem.cs)) already eases authoritative corrections.
- **Assembly boundaries (do not cross):** the pure cores `Minifantasy.Combat`, `Minifantasy.Movement`, and `Minifantasy.InstanceSim` (refs the first two) never reference Netcode/`Game`. `CollisionStep` + `CollisionBody` live in `Minifantasy.InstanceSim`; they take a `Func<Vector2,bool> walkable` (like `InstanceCtx`) and never touch `Game`/`ServerPlayer`. The two-phase orchestration lives in `AttackSimSystem` (Net/Assembly-CSharp), which maps `ServerPlayer` ↔ `CollisionBody`.
- **Convention** (`Assets/_Scripts/ARCHITECTURE.md`): Input → Logic → Data → Visual. `CollisionBody` = Data; `CollisionStep` = Logic (pure); no Visual (positions render through the existing ghost path).

## 3. Component overview

```
 SERVER (host, FixedUpdate)                                        VIEWER CLIENTS
 ─────────────────────────                                        ──────────────
 AttackSimSystem.StepInstanceFixed:
   Phase A — INTEGRATE (per in-instance player, as today)
     drain InputCommands → InstanceStep(atk,pos,cmd)  ◄── pure    (unchanged)
     record moved?  → invMass (0 pinned / 1 mover)
   Phase B — RESOLVE (per regionKey room)
     build CollisionBody[] (sorted by id)
     CollisionStep.Resolve(bodies, walkable, iterations) ◄── pure, deterministic
       mover-yields push-apart + per-axis wall re-validate
     write resolved pos → sp.worldPos
        │
        └── existing AOI snapshot stream (resolved worldPos) ─────►  ghost interp (unchanged)

 OWNER CLIENT: still predicts movement-vs-walls only; server's pushed-off pos arrives in the
 snapshot → existing Reconcile() eases the correction (expected small rubberband on contact).

 HITBOX SEAM (future): OnStrike → CollisionStep.Overlap(center,radius) [broadphase]
                                 → pixel-perfect human-sprite test [narrowphase, deferred]
```

| Unit | Side | Kind | Responsibility |
|------|------|------|----------------|
| `CollisionBody` | shared | Data | `{ ulong id; Vector2 pos; float radius; float invMass; }` — one player's circle for a tick. `invMass=0` pinned, `1` mover; future stat hook. |
| `CollisionStep.Resolve` | shared | Logic (pure) | Mover-yields circle push-apart over one room's bodies, with per-axis wall re-validation. Deterministic (sorted input + fixed iterations). |
| `CollisionStep.Overlap` | shared | Logic (pure) | Circle-overlap broadphase query (`center,radius → indices`). The hitbox seam; unused by collision itself. |
| `AttackSimSystem` (two-phase) | server | Logic | Integrate all in-instance players (as today) → group by `regionKey` → resolve each room → write back `worldPos`. |
| `MovementSettings` (+2 knobs) | shared | Data | `collisionRadius`, `collisionIterations`. |

## 4. The resolver — `CollisionStep` (pure, deterministic)

`Minifantasy.InstanceSim`, sibling to `InstanceStep`. No `Time`/random/Netcode/`Game`.

```csharp
public struct CollisionBody
{
    public ulong id;       // deterministic sort key + write-back key
    public Vector2 pos;    // mutated in place during Resolve
    public float radius;
    public float invMass;  // 0 = pinned (immovable this tick); 1 = mover; future: stat-driven mass
}

public static class CollisionStep
{
    // Resolve player-vs-player overlaps for ONE room. `bodies[0..count)` MUST be pre-sorted by id
    // (the caller sorts) so iteration order — and thus the result — is deterministic. `walkable` is the
    // same predicate MovementStep uses. Mutates bodies[i].pos in place.
    public static void Resolve(CollisionBody[] bodies, int count, System.Func<Vector2,bool> walkable, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        for (int i = 0; i < count; i++)
        for (int j = i + 1; j < count; j++)
        {
            Vector2 d = bodies[j].pos - bodies[i].pos;
            float rsum = bodies[i].radius + bodies[j].radius;
            float dist2 = d.sqrMagnitude;
            if (dist2 >= rsum * rsum) continue;                 // not overlapping

            float dist = Mathf.Sqrt(dist2);
            Vector2 normal = dist > Eps ? d / dist : Vector2.right;  // degenerate same-pos → fixed axis (i<j ⇒ i goes -x)
            float pen = rsum - dist;

            // mover-yields: pinned absorb nothing; movers share by invMass; both-pinned → equal split fallback.
            float inv = bodies[i].invMass + bodies[j].invMass;
            float wi, wj;
            if (inv <= 0f) { wi = 0.5f; wj = 0.5f; }            // both pinned (e.g. spawn overlap) → separate gently
            else { wi = bodies[i].invMass / inv; wj = bodies[j].invMass / inv; }

            bodies[i].pos = SlideClamp(bodies[i].pos, -normal * (pen * wi), walkable);
            bodies[j].pos = SlideClamp(bodies[j].pos,  normal * (pen * wj), walkable);
        }
    }

    // Broadphase query seam for the future hitbox layer: fill `results` with indices of bodies whose circle
    // overlaps the (center,radius) probe. Unused by collision; v1 ships it so OnStrike becomes a one-liner.
    public static int Overlap(CollisionBody[] bodies, int count, Vector2 center, float radius, int[] results)
    {
        int n = 0;
        for (int i = 0; i < count && n < results.Length; i++)
        {
            float rsum = bodies[i].radius + radius;
            if ((bodies[i].pos - center).sqrMagnitude < rsum * rsum) results[n++] = i;
        }
        return n;
    }

    // Apply a positional correction with the SAME per-axis wall slide as MovementStep, so a body pushed toward a
    // wall slides along it instead of entering non-walkable space (walls win over the push).
    static Vector2 SlideClamp(Vector2 from, Vector2 delta, System.Func<Vector2,bool> walkable)
    {
        Vector2 next = from;
        Vector2 tryX = new Vector2(from.x + delta.x, next.y);
        if (delta.x != 0f && walkable(tryX)) next.x = tryX.x;
        Vector2 tryY = new Vector2(next.x, from.y + delta.y);
        if (delta.y != 0f && walkable(tryY)) next.y = tryY.y;
        return next;
    }

    const float Eps = 1e-5f;
}
```

- **Tangential slide** is automatic: correction is along the contact normal only, so motion perpendicular to it is preserved — a mover walking into a pinned body slides around it.
- **Iterations** (default 3) relax multi-body stacks; at ≤6 bodies/room `O(count² · iterations)` is trivial.
- **Determinism**: caller pre-sorts by `id`; fixed iteration count; symmetric math; degenerate-overlap normal is a fixed axis with the lower-id body pushed `-x`. v1 server-only makes cross-machine float drift irrelevant; this keeps it test-stable and promotion-ready.

## 5. Data — `MovementSettings` additions

`Assets/_Scripts/Net/MovementSettings.cs` (persisted via `JsonPref`, already passed to `StepInstanceFixed`):

```csharp
    [Min(0.05f)] public float collisionRadius = 0.4f;   // per-player body radius (world units); 1 cell == 1 unit
    [Min(1)]     public int   collisionIterations = 3;  // relaxation passes per room per tick
```

`collisionRadius` is read into every `CollisionBody.radius` for v1 (uniform); per-body storage means a future per-entity stat overrides it without touching the resolver.

## 6. Logic — `AttackSimSystem` two-phase refactor

`Assets/_Scripts/Net/AttackSimSystem.cs`. Today `StepInstanceFixed` is a single loop that drains+steps+writes `sp.worldPos` per player ([AttackSimSystem.cs:11-39](Assets/_Scripts/Net/AttackSimSystem.cs)). Split into:

- **Phase A — Integrate** (unchanged per-player body): for each `inInstance` player, record `startPos = sp.worldPos`, drain contiguous `InputCommand`s through `InstanceStep` (or `MovementStep` if no weapon), emit transitions as today. After draining, `moved = (sp.worldPos - startPos).sqrMagnitude > MovedEps` → `invMass = moved ? 1f : 0f`.
- **Phase B — Resolve per room**: group integrated in-instance players by `regionKey` (reusable scratch, sorted by `id`); for each room fill a `CollisionBody[]` (`id`, `pos = sp.worldPos`, `radius = cfg.collisionRadius`, `invMass`), call `CollisionStep.Resolve(bodies, count, _walkAt, cfg.collisionIterations)`, write each `bodies[k].pos` back to its `ServerPlayer.worldPos`.

Reuses the existing static `_walkAt` predicate ([AttackSimSystem.cs:9](Assets/_Scripts/Net/AttackSimSystem.cs)). Scratch (grouping dict/list + a growable `CollisionBody[]`) is held in reusable static fields to avoid per-tick allocation (matching `ReplicationHub`'s scratch discipline). Overworld players (`regionKey == (0,0)` / `!inInstance`) are skipped entirely — no behavior change there.

**Ordering note (future):** transitions/`OnStrike` still fire during Phase A on pre-resolve positions; for v1 `OnStrike` only logs so this is immaterial. When real hits land, the hit query should run in/after Phase B on resolved positions — recorded as a deferred ordering refinement, not v1 work.

## 7. Netcode, determinism & performance
- **No wire / prefab / scene changes.** Resolved `worldPos` rides the existing snapshot; the owner's `Reconcile()` eases the correction. Expected v1 behavior: walking into someone briefly predicts overlap locally, then the server's pushed-off position arrives and smooths out — an acceptable small rubberband at friends-scale latency, and the chosen tradeoff.
- **CPU:** one extra grouping pass + `O(count²·iters)` per room per `FixedUpdate`; with ≤6 players/room this is negligible. Overworld unaffected.
- **Determinism:** pure resolver, sorted input, fixed iterations. Server is the sole arbiter in v1; the function is promotion-ready for client prediction later.
- **Topology:** unchanged (Relay host-is-a-player).

## 8. Hitbox seam (explicit; no logic in v1)
The future hitbox flow is **broadphase → narrowphase**:
1. **Broadphase (built now):** `CollisionStep.Overlap(roomBodies, center, radius)` returns candidate roommates near an attack volume — the same per-room body store collision uses. The server already holds every position.
2. **Narrowphase (deferred):** for each candidate, a **pixel-perfect** test against the victim's **human-sprite** opaque pixels (weapon layer excluded) at the victim's current animation frame. The frame is already known server-side via `AttackPose.Pack(phase, frame, dir)` carried on `ServerPlayer.attackState`; the test needs baked per-frame opaque masks of the human body layers (a future build-time asset). `AttackSimSystem.OnStrike` ([AttackSimSystem.cs:73](Assets/_Scripts/Net/AttackSimSystem.cs)) stays a log in v1; later it calls `Overlap` then the narrowphase, with no wire/client change.

## 9. File manifest

**New**
- `Assets/_Scripts/InstanceSim/CollisionStep.cs` — `CollisionBody` + pure `Resolve` (mover-yields push-apart + wall slide) + `Overlap` broadphase query + `SlideClamp` helper.
- `Assets/Tests/EditMode/InstanceSim/CollisionStepTests.cs` — in the existing `Minifantasy.InstanceSim.Tests` asmdef.

**Modified**
- `Assets/_Scripts/Net/AttackSimSystem.cs` — two-phase (integrate → group-by-`regionKey` → resolve); reusable scratch; `invMass` from moved-this-tick.
- `Assets/_Scripts/Net/MovementSettings.cs` — `collisionRadius`, `collisionIterations`.

**Unchanged (called out):** no edits to `InstanceStep`, `MovementStep`, `PredictionSystem`, `ReplicationHub`, `SnapshotEntry`, `PlayerRegistry`, AOI, ghosts, prefabs, or the scene.

## 10. Verification plan
Per the unity-mcp flow (`execute_code` broken; **refresh `scope=all` → poll `editor_state` ready → `read_console` clean → Play → screenshot**; MCP can't drive the IMGUI Host button):
- **Unit (EditMode, `Minifantasy.InstanceSim.Tests`):** mover-into-pinned (pinned holds, mover pushed to just-touching); head-on two movers split 50/50; tangential graze preserves forward motion (slide-around); both-pinned spawn-overlap separates via fallback; wall-clamp (a body pushed toward a non-walkable cell slides along, never enters it); exact same-position degenerate tie-break is deterministic; 3-body line relaxes toward non-overlap over iterations; **order-independence** (shuffle input then sort → identical result); non-overlapping bodies are untouched.
- **MCP-verifiable:** compile clean; all EditMode assemblies green; Play boots with no console errors.
- **Manual host (documented — required):** two clients in one underworld room cannot walk through each other; walking into a standing player pushes *you* around them while they hold ground; two players mutually shoving behave sanely; a player can't be shoved into/through the moat wall; overworld players still overlap freely (no collision).

## 11. Open assumptions & deferred
- **Repo carries WIP** — commit the two new files (and the two small edits) on their own; do **not** `git add` shared WIP (scene, prefabs, other settings/assets).
- **`collisionRadius` default 0.4** assumes 1 cell == 1 unit; tune in `movement.json` after the host playtest if bodies feel too large/small.
- **Resolve-once-per-FixedUpdate** assumes near-aligned tick rates (true under the jitter buffer); per-shared-tick resolution is the refinement if catch-up storms cause overlap leakage.
- **Deferred:** client-side collision prediction; hitboxes/hurtboxes/damage + the pixel-perfect human-sprite narrowphase + per-frame masks; overworld/enemy collision; per-entity radius/mass values + knockback (the `invMass` field is the hook).
