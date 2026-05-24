# Underworld Collision — Client-Side Prediction (Symmetric) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Predict the local player's own player-vs-player push-apart on the owner client (live tick + reconcile replay) so contact feels responsive instead of overlapping-then-rubberbanding, while the server stays authoritative.

**Architecture:** Add a pure `CollisionStep.ResolveOne` (in `Minifantasy.InstanceSim`) that resolves ONE focal body against a set of others and returns only the focal body's resolved position (internally id-sorted to match the server's room resolve). `GhostManager` gains a read-only accessor that yields same-room remote `CollisionBody`s at their **rendered** positions with **symmetric** mover weighting (a ghost that moved between its last two snapshots is a mover). `PredictionSystem` calls a new `ResolveSelfCollision` helper after integrating the local player — in `FixedTick`, `FixedTickInstance`, and `ReplayFrom` — keeping only self's resolved position. No wire/prefab/scene/`SnapshotEntry` changes; in-instance only.

**Tech Stack:** Unity 6, Netcode for GameObjects (custom hub, no per-player NetworkObject), C#, NUnit EditMode tests, unity-mcp for compile/test/Play verification.

Spec: `docs/superpowers/specs/2026-05-24-underworld-collision-prediction-design.md`. Builds on the shipped server-side resolver (`docs/superpowers/plans/2026-05-24-underworld-player-collision.md`).

---

## Conventions used in every task

- **MCP compile/verify chain** (`execute_code` is broken on this machine): after editing/adding `.cs`, run `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` (expect empty) → for tests `run_tests mode=EditMode assembly_names=[...]` then poll `get_test_job`.
- **Adding a new `.cs` file:** always `refresh_unity scope=all` (not `scripts`) so Unity imports it before compiling. (This plan only modifies existing files, but the rule stands if a test file is added.)
- **Commits:** commit only the files each task touches; **never** `git add` shared WIP (scene, prefabs, unrelated settings/assets). **No Claude attribution** in messages.
- **Assembly boundaries:** `CollisionStep`/`CollisionBody` live in pure `Minifantasy.InstanceSim` (no Netcode/`Game`). `PredictionSystem` + `GhostManager` are in Assembly-CSharp (`Net/`), which already references `Minifantasy.InstanceSim` (via `InstanceStep`/`AttackSimSystem`), so `CollisionStep.ResolveOne` and `CollisionBody` are reachable there with no asmdef change. The EditMode test asmdef `Minifantasy.InstanceSim.Tests` can reach `ResolveOne` (it's in the pure asmdef) but **cannot** reach `PredictionSystem`/`GhostManager` (Assembly-CSharp) — so Tasks 2–3 are verified by compile + Play boot + the manual host test, not unit tests. This is intentional; do not fake coverage by mocking the MonoBehaviour singletons.

---

# Task 1: `CollisionStep.ResolveOne` — focal-body resolve for client prediction

The genuinely new, pure logic: assemble self + remotes, **sort by id** (so the result matches the server's id-sorted room resolve), `Resolve`, and return **only** the focal body's position (remotes are server-owned and discarded by the caller). Copy-not-mutate so the caller's `others` array is untouched.

**Files:**
- Modify: `Assets/_Scripts/InstanceSim/CollisionStep.cs`
- Modify: `Assets/Tests/EditMode/InstanceSim/CollisionStepTests.cs` (existing `Minifantasy.InstanceSim.Tests` asmdef; reuses the existing `Body(...)` helper and `Open` predicate)

- [ ] **Step 1: Write the failing tests.** Append these inside the existing `CollisionStepTests` class, before its final `}`:

```csharp
    // --- ResolveOne: focal-body resolve for client prediction (id-sort + read back self only) ---

    [Test]
    public void ResolveOne_SelfMoverIntoPinned_ReturnsSelfPushedOut()
    {
        var self = Body(2, new Vector2(0.6f, 0f), 1f);          // mover, overlapping a pinned other (0.6 < 1.0 radii sum)
        var others = new[] { Body(1, Vector2.zero, 0f) };       // pinned
        var scratch = new CollisionBody[4];
        Vector2 p = CollisionStep.ResolveOne(self, others, 1, Open, 4, scratch);
        Assert.AreEqual(1.0f, p.x, 1e-3f);                      // self pushed out to exactly touching
        Assert.AreEqual(0f, p.y, 1e-3f);
    }

    [Test]
    public void ResolveOne_BothMovers_SelfGetsItsHalf()
    {
        var self = Body(2, new Vector2(0.3f, 0f), 1f);
        var others = new[] { Body(1, new Vector2(-0.3f, 0f), 1f) };   // also a mover -> 50/50 split
        var scratch = new CollisionBody[4];
        Vector2 p = CollisionStep.ResolveOne(self, others, 1, Open, 4, scratch);
        Assert.AreEqual(0.5f, p.x, 1e-3f);                     // self moved its half: 0.3 -> 0.5 (gap opens to 1.0)
        Assert.AreEqual(0f, p.y, 1e-3f);
    }

    [Test]
    public void ResolveOne_OrderIndependent_SameSelfResult()
    {
        var self = Body(2, new Vector2(0.3f, 0.1f), 1f);
        var a = new[] { Body(1, Vector2.zero, 0f),            Body(3, new Vector2(0.7f, 0f), 0f) };
        var b = new[] { Body(3, new Vector2(0.7f, 0f), 0f),   Body(1, Vector2.zero, 0f) };   // reversed input order
        Vector2 p1 = CollisionStep.ResolveOne(self, a, 2, Open, 6, new CollisionBody[4]);
        Vector2 p2 = CollisionStep.ResolveOne(self, b, 2, Open, 6, new CollisionBody[4]);
        Assert.AreEqual(p1, p2);   // internal id-sort makes the self result independent of caller array order
    }

    [Test]
    public void ResolveOne_NoOthers_ReturnsSelfUnchanged()
    {
        var self = Body(2, new Vector2(1f, 2f), 1f);
        Vector2 p = CollisionStep.ResolveOne(self, new CollisionBody[0], 0, Open, 4, new CollisionBody[2]);
        Assert.AreEqual(new Vector2(1f, 2f), p);
    }

    [Test]
    public void ResolveOne_PinnedSelf_StandingStill_NotPushedByMover()
    {
        var self = Body(2, Vector2.zero, 0f);                   // self standing still (pinned)
        var others = new[] { Body(1, new Vector2(0.6f, 0f), 1f) };  // a mover overlapping self
        Vector2 p = CollisionStep.ResolveOne(self, others, 1, Open, 4, new CollisionBody[4]);
        Assert.AreEqual(Vector2.zero, p);                      // mover yields; standing self holds ground (matches server)
    }

    [Test]
    public void ResolveOne_DoesNotMutateCallerOthersArray()
    {
        var self = Body(2, new Vector2(0.6f, 0f), 1f);
        var others = new[] { Body(1, Vector2.zero, 0f) };
        CollisionStep.ResolveOne(self, others, 1, Open, 4, new CollisionBody[4]);
        Assert.AreEqual(Vector2.zero, others[0].pos);         // others copied into scratch; caller's array untouched
    }
```

- [ ] **Step 2: Run the tests; verify they FAIL.** `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` (expect a compile error: `CollisionStep.ResolveOne` not defined) → `run_tests mode=EditMode assembly_names=["Minifantasy.InstanceSim.Tests"]` → `get_test_job` → expect compile failure / no run.

- [ ] **Step 3: Implement `ResolveOne` + `SortById`.** In `Assets/_Scripts/InstanceSim/CollisionStep.cs`, add both methods to the `CollisionStep` class, immediately **after** `Resolve` and **before** `Overlap`:

```csharp
    // Resolve a single focal body against `others[0..count)` and return ONLY the focal body's resolved position.
    // The client uses this for collision prediction: self is the focal body; `others` are same-room ghosts whose
    // server-owned positions are discarded. `scratch` must hold at least count+1 bodies and is overwritten; the
    // caller's `others` array is NOT mutated (it is copied in). Bodies are id-sorted internally so the order — and
    // thus the result — matches the server's id-sorted room resolve. No-op (returns self.pos) when count == 0.
    public static Vector2 ResolveOne(CollisionBody self, CollisionBody[] others, int count, Func<Vector2, bool> walkable, int iterations, CollisionBody[] scratch)
    {
        for (int i = 0; i < count; i++) scratch[i] = others[i];
        scratch[count] = self;
        int n = count + 1;
        SortById(scratch, n);
        Resolve(scratch, n, walkable, iterations);
        for (int i = 0; i < n; i++) if (scratch[i].id == self.id) return scratch[i].pos;
        return self.pos;   // unreachable (self is always present)
    }

    // Insertion sort by id (n is tiny: roommates + 1). Allocation-free; gives the same deterministic order the
    // server gets from sorting its per-room bodies by id.
    static void SortById(CollisionBody[] a, int n)
    {
        for (int i = 1; i < n; i++)
        {
            var key = a[i];
            int j = i - 1;
            while (j >= 0 && a[j].id > key.id) { a[j + 1] = a[j]; j--; }
            a[j + 1] = key;
        }
    }
```

- [ ] **Step 4: Run the tests; verify they PASS.** `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` (empty) → `run_tests mode=EditMode assembly_names=["Minifantasy.InstanceSim.Tests"]` → `get_test_job` → all green (the existing resolver/overlap tests still pass).

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Scripts/InstanceSim/CollisionStep.cs Assets/Tests/EditMode/InstanceSim/CollisionStepTests.cs
git commit -m "InstanceSim: CollisionStep.ResolveOne — focal-body resolve for client prediction (unit-tested)"
```

---

# Task 2: `GhostManager` roommate collision-body accessor

Expose the room's remote ghosts as `CollisionBody`s for the predictor: **rendered** position (`CurrentPos`) and **symmetric** mover weighting (`invMass = 1` if the ghost moved between its last two snapshots, i.e. `toPos != fromPos`). Region-only AOI guarantees every non-self ghost is a same-room roommate, so no room filtering is needed. Read-only; no rendering/interp change.

**Files:**
- Modify: `Assets/_Scripts/Net/GhostManager.cs`

- [ ] **Step 1: Add the accessor methods.** In `GhostManager.cs`, add these two public methods plus the epsilon constant inside the `GhostManager` class (e.g. immediately after the `static Vector2 CurrentPos(Ghost g) => ...` line at [GhostManager.cs:86](Assets/_Scripts/Net/GhostManager.cs)):

```csharp
    const float MoveEps = 1e-6f;   // sq-distance below which a ghost is treated as not-moving between snapshots

    // Count of same-room remotes (every ghost except self). In-instance AOI is region-only, so all ghosts are
    // roommates. The predictor calls this to size its buffer before FillRoommateBodies.
    public int RoommateCount(ulong selfId)
    {
        int n = 0;
        foreach (var kv in ghosts) if (kv.Key != selfId) n++;
        return n;
    }

    // Fill buf[0..RoommateCount) with one CollisionBody per same-room remote, for the owner's collision prediction:
    //   pos     = CurrentPos(g) — the RENDERED (interpolated, on-screen) position, so you stop when sprites touch
    //   radius  = the caller's collision radius (uniform, same value the server uses)
    //   invMass = 1 if the ghost moved between its last two snapshots (a "mover"), else 0 (pinned) — mirrors the
    //             server's mover-yields weighting symmetrically. Order is arbitrary; ResolveOne id-sorts internally.
    public void FillRoommateBodies(ulong selfId, float radius, CollisionBody[] buf)
    {
        int k = 0;
        foreach (var kv in ghosts)
        {
            if (kv.Key == selfId) continue;
            if (k >= buf.Length) break;
            var g = kv.Value;
            float invMass = (g.toPos - g.fromPos).sqrMagnitude > MoveEps ? 1f : 0f;
            buf[k++] = new CollisionBody { id = kv.Key, pos = CurrentPos(g), radius = radius, invMass = invMass };
        }
    }
```

- [ ] **Step 2: Verify compile.** `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` → expect empty. (No unit test: `GhostManager` is a MonoBehaviour in Assembly-CSharp, unreachable from the EditMode test asmdefs and dependent on scene singletons — verified by compile here, by Play boot in Task 3, and by the manual host test in Task 4.)

- [ ] **Step 3: Commit.**

```bash
git add Assets/_Scripts/Net/GhostManager.cs
git commit -m "Net: GhostManager roommate collision-body accessor (rendered pos + snapshot-delta invMass)"
```

---

# Task 3: `PredictionSystem` — predict self collision in live tick + reconcile replay

Wire the predictor: cache `localId`, add reusable buffers + a `ResolveSelfCollision` helper, and call it after integrating the local player in **all three** step paths. The stored `predictedPos` becomes the collision-resolved position, and `ReplayFrom` re-runs the resolve so the live and replayed paths agree (otherwise contact reconciles every tick).

**Files:**
- Modify: `Assets/_Scripts/Net/PredictionSystem.cs`

- [ ] **Step 1: Add the `Unity.Netcode` using.** At the top of `PredictionSystem.cs` ([PredictionSystem.cs:1-2](Assets/_Scripts/Net/PredictionSystem.cs)), add the import so `NetworkManager` is available. The top of the file becomes:

```csharp
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
```

- [ ] **Step 2: Add fields for localId + collision buffers.** Inside the `PredictionSystem` class, alongside the existing private fields (`buffer`, `smoothOffset`, `prevPos` at [PredictionSystem.cs:15-17](Assets/_Scripts/Net/PredictionSystem.cs)), add:

```csharp
    ulong localId;                                       // cached on Activate; excludes self + sorts deterministically
    const float MovedEps = 1e-6f;                        // MUST match AttackSimSystem.MovedEps so client invMass mirrors the server
    CollisionBody[] _remotes = new CollisionBody[8];     // roommate bodies, filled by GhostManager (grown as needed)
    CollisionBody[] _scratch = new CollisionBody[8];     // ResolveOne work buffer (>= remotes+1)
```

- [ ] **Step 3: Cache `localId` in `Activate`.** In `Activate` ([PredictionSystem.cs:27-36](Assets/_Scripts/Net/PredictionSystem.cs)), add the `localId` assignment (the rest of the method is unchanged). The method becomes:

```csharp
    public void Activate(Vector2 startPos)
    {
        var cfg = Game.Instance.MovementCfg;
        buffer = new InputRingBuffer(Mathf.Max(8, Mathf.NextPowerOfTwo(cfg.inputBufferCapacity)));
        localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
        Pos = startPos;
        prevPos = startPos;
        smoothOffset = Vector2.zero;
        Tick = 0;
        Active = true;
    }
```

- [ ] **Step 4: Add the `ResolveSelfCollision` helper + capacity grower.** Add these two methods to the `PredictionSystem` class (e.g. just below `ReplayFrom`):

```csharp
    // Predict the local player's own push-apart: resolve self against same-room ghosts (rendered positions,
    // symmetric mover weighting) with the SAME deterministic CollisionStep the server runs, and return self's
    // resolved position (remotes are server-owned, discarded). `candidate` is the just-integrated position;
    // `beforeMove` is the position before this tick's movement, so invMass mirrors the server's pre-collision
    // moved-this-tick rule. No-op when alone in the room. Called on the live tick AND reconcile replay.
    Vector2 ResolveSelfCollision(Vector2 candidate, Vector2 beforeMove)
    {
        var gm = GhostManager.Instance; var game = Game.Instance;
        if (gm == null || game == null) return candidate;
        int rc = gm.RoommateCount(localId);
        if (rc == 0) return candidate;
        EnsureCollisionCapacity(rc);
        var cfg = game.MovementCfg;
        gm.FillRoommateBodies(localId, cfg.collisionRadius, _remotes);
        float invMass = (candidate - beforeMove).sqrMagnitude > MovedEps ? 1f : 0f;
        var self = new CollisionBody { id = localId, pos = candidate, radius = cfg.collisionRadius, invMass = invMass };
        return CollisionStep.ResolveOne(self, _remotes, rc, Walkable, cfg.collisionIterations, _scratch);
    }

    void EnsureCollisionCapacity(int rc)
    {
        if (_remotes.Length < rc) _remotes = new CollisionBody[Mathf.NextPowerOfTwo(rc)];
        if (_scratch.Length < rc + 1) _scratch = new CollisionBody[Mathf.NextPowerOfTwo(rc + 1)];
    }
```

- [ ] **Step 5: Resolve collision in `FixedTick` (movement-only).** In `FixedTick` ([PredictionSystem.cs:43-53](Assets/_Scripts/Net/PredictionSystem.cs)), replace the single line `Pos = MovementStep.Step(Pos, move, dt, cfg.moveSpeed, Walkable);` with the integrate-then-resolve pair. The method becomes:

```csharp
    public void FixedTick(GateMod gate, float dt)
    {
        var cfg = Game.Instance.MovementCfg;
        Vector2 raw = SampleInput();
        Vector2 move = InstanceStep.FreeMove(raw, gate);
        Tick++;
        prevPos = Pos;                                  // remember where we were so the visual can interpolate across the step
        Vector2 stepped = MovementStep.Step(Pos, move, dt, cfg.moveSpeed, Walkable);
        Pos = ResolveSelfCollision(stepped, prevPos);   // predict our own push-apart vs same-room ghosts
        buffer.Store(new InputFrame { tick = Tick, input = move, predictedPos = Pos });
        ReplicationHub.Instance.SubmitInputTickRpc(new InputCommand { tick = Tick, rawMove = raw });
    }
```

- [ ] **Step 6: Resolve collision in `FixedTickInstance` (with weapon).** In `FixedTickInstance` ([PredictionSystem.cs:58-74](Assets/_Scripts/Net/PredictionSystem.cs)), replace `Pos = p;` with the resolve call. The store line is unchanged (it already runs after, storing the resolved `Pos`). The relevant lines become:

```csharp
        InstanceStep.Step(ref atk, ref p, new InstanceInput { rawMove = rawMove, attack = attack }, ctx);
        Pos = ResolveSelfCollision(p, prevPos);          // predict push-apart; p includes lunge so invMass mirrors the server
        buffer.Store(new InputFrame { tick = Tick, input = AttackLogic.LungeVelocity(atk, tl) ?? InstanceStep.FreeMove(rawMove, gate), predictedPos = Pos });
```

- [ ] **Step 7: Resolve collision in `ReplayFrom` (reconcile).** In `ReplayFrom` ([PredictionSystem.cs:104-117](Assets/_Scripts/Net/PredictionSystem.cs)), add the resolve after each per-tick `MovementStep` so replayed positions match the live path. The method becomes:

```csharp
    void ReplayFrom(Vector2 fromPos, uint ackTick)
    {
        var cfg = Game.Instance.MovementCfg;
        Vector2 p = fromPos;
        for (uint t = ackTick + 1; t <= Tick; t++)
        {
            if (!buffer.TryGet(t, out var f)) break;
            Vector2 before = p;
            p = MovementStep.Step(p, f.input, Time.fixedDeltaTime, cfg.moveSpeed, Walkable);
            p = ResolveSelfCollision(p, before);   // re-resolve vs current ghost positions so replay == live path
            f.predictedPos = p;
            buffer.Store(f);
        }
        Pos = p;
        prevPos = Pos;   // a correction jumps the logical pos; smoothOffset eases the visual, so don't also interp across it
    }
```

- [ ] **Step 8: Verify compile + existing tests still green.** `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` (empty) → `run_tests mode=EditMode assembly_names=["Minifantasy.InstanceSim.Tests"]` → `get_test_job` → green (nothing in the pure asmdef changed behavior; the new `ResolveOne` tests from Task 1 still pass).

- [ ] **Step 9: Verify Play boots clean.** `manage_editor action=play` → poll `editor_state` until `isPlaying` and not `isCompiling` → `read_console types=["error","warning"]` (no new errors) → `manage_editor action=stop`. (Single-player boot exercises the no-roommates path: `RoommateCount == 0` → `ResolveSelfCollision` is a no-op, so movement prediction is unchanged when alone.)

- [ ] **Step 10: Commit.**

```bash
git add Assets/_Scripts/Net/PredictionSystem.cs
git commit -m "Net: client-side collision prediction in PredictionSystem (live tick + reconcile replay)"
```

---

# Task 4: Manual host verification (required — MCP cannot drive the IMGUI Host button)

The feel of prediction can only be confirmed with two real clients; EditMode tests + a clean Play boot are the MCP-verifiable gates, this is the human gate.

- [ ] **Step 1: Ask the user (Ryan) to run host + a second client**, both `enter` the **same** underworld site/room, and confirm:
  - Walking into another player now **stops/slides you immediately on your own screen** — no overlap-then-snap-back rubberband on contact (compare against the pre-change build to confirm the rubberband is gone).
  - Two players **walking into each other** meet symmetrically without deep interpenetration; the contact resolves cleanly at the moment of impact.
  - Standing still while another player walks into you: **you hold your ground** and they slide around you (mover-yields), with no self-jitter.
  - Pressed-together steady state: any residual jiggle stays **small and eased**, not a visible fight (this is the accepted heuristic limit from spec §8).
  - **Chasing a fleeing player**: you may briefly "catch" on their rendered position then smooth forward — confirm it's not jarring.
  - **Overworld** players still overlap freely (prediction is in-instance only).
  - Server authority intact: no tunneling through each other, no shoving anyone through the **moat wall**.
- [ ] **Step 2: If contact feels too sticky/loose,** tune `collisionRadius` / `collisionIterations` in `movement.json` (shared by client predict + server resolve, so they stay consistent). No code change needed.

---

## Self-review checklist (run after the last task)

- [ ] **Spec coverage:** focal-body resolve with id-sort + read-back-self (Task 1, spec §4); `GhostManager` accessor — rendered pos + symmetric snapshot-delta `invMass` (Task 2, spec §6); resolve in `FixedTick` + `FixedTickInstance` + `ReplayFrom` (Task 3 Steps 5–7, spec §5); live==replay self-consistency (Task 3 Steps 6–7, spec §7); deterministic id-sort + no-op-when-alone (Tasks 1 & 3, spec §8); no wire/prefab/scene change (only `CollisionStep.cs`, `GhostManager.cs`, `PredictionSystem.cs` + tests touched, spec §9); manual host feel test (Task 4, spec §10). ✔
- [ ] **No placeholders:** every code step shows the full method/test body and the exact MCP/commit commands. ✔
- [ ] **Type consistency:** `CollisionBody{id,pos,radius,invMass}`; `CollisionStep.ResolveOne(self, others, count, walkable, iterations, scratch)`; `GhostManager.RoommateCount(selfId)` + `FillRoommateBodies(selfId, radius, buf)`; `PredictionSystem.ResolveSelfCollision(candidate, beforeMove)` + `EnsureCollisionCapacity(rc)`; `MovedEps = 1e-6f` (client) matches `AttackSimSystem.MovedEps`. Used identically across Tasks 1–3. ✔
- [ ] **Testability honesty:** only the pure `ResolveOne` is unit-tested; `GhostManager`/`PredictionSystem` glue is verified by compile + Play boot + manual host (spec §10), not mocked. ✔

## Manual (host-required) verification summary

MCP can't drive the IMGUI Host button. After Task 3, the user runs host + a second client in one underworld room to confirm contact is responsive (no rubberband), symmetric on mutual approach, mover-yields when one stands still, stable under sustained press, and still server-authoritative (no tunneling, no shove-through-walls), with overworld unaffected. The pure `ResolveOne` unit tests + a clean Play boot are the MCP-verifiable gates.
