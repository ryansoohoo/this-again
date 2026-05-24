# Underworld Player-vs-Player Collision Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop in-instance (underworld) players from walking through each other via a pure, deterministic, kinematic circle resolver run server-authoritatively after movement integration.

**Architecture:** A new pure `CollisionStep` (in `Minifantasy.InstanceSim`, sibling to `InstanceStep`/`MovementStep`) does mover-yields circle push-apart over one room's bodies, with per-axis wall re-validation. `AttackSimSystem` becomes two-phase: integrate every in-instance player as today, then group by `regionKey` and resolve each room, writing resolved positions back to `ServerPlayer.worldPos`. Resolved positions flow through the existing snapshot stream — **no wire/prefab/scene changes**. The resolver also ships a circle `Overlap` query as the broadphase seam for the future pixel-perfect hitbox layer.

**Tech Stack:** Unity 6, Netcode for GameObjects (custom hub, no per-player NetworkObject), C#, NUnit EditMode tests, unity-mcp for compile/test/Play verification.

Spec: `docs/superpowers/specs/2026-05-24-underworld-player-collision-design.md`.

---

## Conventions used in every task

- **MCP compile/verify chain** (`execute_code` is broken on this machine): after editing/adding `.cs`, run `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` (expect empty) → for tests `run_tests mode=EditMode assembly_names=[...]` then poll `get_test_job`.
- **Adding a new `.cs` file:** always `refresh_unity scope=all` (not `scripts`) so Unity imports the new file before compiling.
- **Commits:** new files committed on their own; never `git add` shared WIP files (scene, prefabs, unrelated settings/assets). **No Claude attribution** in messages.
- **Assembly boundaries:** `CollisionStep` + `CollisionBody` go in `Minifantasy.InstanceSim` (pure; refs `Minifantasy.Combat` + `Minifantasy.Movement`; no Netcode/`Game`). `AttackSimSystem` (Assembly-CSharp/Net) already references `Minifantasy.InstanceSim` (it uses `InstanceStep`), so `CollisionStep` is reachable there with no asmdef change.

---

# Task 1: `CollisionStep` core — `CollisionBody` + mover-yields `Resolve`

**Files:**
- Create: `Assets/_Scripts/InstanceSim/CollisionStep.cs`
- Create: `Assets/Tests/EditMode/InstanceSim/CollisionStepTests.cs` (existing `Minifantasy.InstanceSim.Tests` asmdef)

- [ ] **Step 1: Write the failing tests.** Create `Assets/Tests/EditMode/InstanceSim/CollisionStepTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

public class CollisionStepTests
{
    static readonly System.Func<Vector2, bool> Open = _ => true;

    static CollisionBody Body(ulong id, Vector2 pos, float invMass, float radius = 0.5f)
        => new CollisionBody { id = id, pos = pos, radius = radius, invMass = invMass };

    // --- mover-yields ---

    [Test]
    public void MoverIntoPinned_PinnedHolds_MoverPushedToTouching()
    {
        var bodies = new[]
        {
            Body(1, Vector2.zero, 0f),            // pinned
            Body(2, new Vector2(0.6f, 0f), 1f),   // mover, overlapping (0.6 < 1.0 sum-of-radii)
        };
        CollisionStep.Resolve(bodies, 2, Open, 4);
        Assert.AreEqual(Vector2.zero, bodies[0].pos);                  // pinned held its ground
        Assert.AreEqual(1.0f, bodies[1].pos.x, 1e-3f);                // mover pushed out to exactly touching
        Assert.AreEqual(0f, bodies[1].pos.y, 1e-3f);
    }

    [Test]
    public void TwoMovers_HeadOn_SplitEqually()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(-0.3f, 0f), 1f),
            Body(2, new Vector2( 0.3f, 0f), 1f),
        };
        CollisionStep.Resolve(bodies, 2, Open, 4);
        Assert.AreEqual(1.0f, bodies[1].pos.x - bodies[0].pos.x, 1e-3f);   // separated to sum-of-radii
        Assert.AreEqual(-bodies[0].pos.x, bodies[1].pos.x, 1e-3f);         // symmetric about the midpoint
    }

    [Test]
    public void Mover_GrazingPinned_KeepsTangentialMotion_SlidesAround()
    {
        var bodies = new[]
        {
            Body(1, Vector2.zero, 0f),
            Body(2, new Vector2(0.6f, 0.2f), 1f),
        };
        CollisionStep.Resolve(bodies, 2, Open, 4);
        Assert.Greater(bodies[1].pos.y, 0.15f);                           // tangential y not collapsed -> slid around
        Assert.GreaterOrEqual((bodies[1].pos - bodies[0].pos).magnitude, 1.0f - 1e-3f);  // no longer overlapping
    }

    [Test]
    public void BothPinned_SpawnOverlap_StillSeparate()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(-0.1f, 0f), 0f),
            Body(2, new Vector2( 0.1f, 0f), 0f),
        };
        CollisionStep.Resolve(bodies, 2, Open, 6);
        Assert.GreaterOrEqual((bodies[1].pos - bodies[0].pos).magnitude, 1.0f - 1e-3f);
    }

    [Test]
    public void ExactSamePosition_DeterministicTieBreak_Separates()
    {
        var bodies = new[]
        {
            Body(1, Vector2.zero, 1f),
            Body(2, Vector2.zero, 1f),
        };
        CollisionStep.Resolve(bodies, 2, Open, 6);
        Assert.Less(bodies[0].pos.x, bodies[1].pos.x);                    // lower id pushed -x (fixed-axis tie-break)
        Assert.AreEqual(0f, bodies[0].pos.y, 1e-4f);
        Assert.AreEqual(0f, bodies[1].pos.y, 1e-4f);
    }

    // --- walls (walls win over the push) ---

    [Test]
    public void MoverBlockedByWall_ResidualOverlapTolerated()
    {
        System.Func<Vector2, bool> wall = p => p.x <= 0.5f;   // x > 0.5 is non-walkable
        var bodies = new[]
        {
            Body(1, new Vector2(0.2f, 0f), 0f),   // pinned
            Body(2, new Vector2(0.5f, 0f), 1f),   // mover on the wall edge; push would be +x into the wall
        };
        CollisionStep.Resolve(bodies, 2, wall, 4);
        Assert.AreEqual(0.5f, bodies[1].pos.x, 1e-4f);                    // blocked at the wall, not shoved through
        Assert.AreEqual(new Vector2(0.2f, 0f), bodies[0].pos);           // pinned unmoved
    }

    [Test]
    public void MoverPushedTowardWall_SlidesAlong_NeverEntersWall()
    {
        System.Func<Vector2, bool> wall = p => p.x <= 1.0f;   // x > 1.0 is non-walkable
        var bodies = new[]
        {
            Body(1, new Vector2(0.6f, 0f), 0f),   // pinned
            Body(2, new Vector2(1.0f, 0.1f), 1f), // mover wedged against the wall at x = 1
        };
        CollisionStep.Resolve(bodies, 2, wall, 4);
        Assert.LessOrEqual(bodies[1].pos.x, 1.0f + 1e-4f);               // never crossed into the wall
        Assert.Greater(bodies[1].pos.y, 0.2f);                          // slid along the wall in +y instead
    }

    // --- stacks / determinism / no-op ---

    [Test]
    public void ThreeBodyLine_RelaxesTowardNonOverlap()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(0.0f, 0f), 1f),
            Body(2, new Vector2(0.4f, 0f), 1f),
            Body(3, new Vector2(0.8f, 0f), 1f),
        };
        CollisionStep.Resolve(bodies, 3, Open, 24);
        Assert.GreaterOrEqual((bodies[1].pos - bodies[0].pos).magnitude, 1.0f - 0.05f);
        Assert.GreaterOrEqual((bodies[2].pos - bodies[1].pos).magnitude, 1.0f - 0.05f);
    }

    [Test]
    public void NonOverlapping_Untouched()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(0f, 0f), 1f),
            Body(2, new Vector2(2f, 0f), 1f),     // 2.0 apart, radii sum 1.0 -> no overlap
        };
        CollisionStep.Resolve(bodies, 2, Open, 4);
        Assert.AreEqual(new Vector2(0f, 0f), bodies[0].pos);
        Assert.AreEqual(new Vector2(2f, 0f), bodies[1].pos);
    }

    [Test]
    public void Deterministic_SortedInput_SameResultRegardlessOfArrayOrder()
    {
        CollisionBody[] Make(params CollisionBody[] b)
        {
            System.Array.Sort(b, (x, y) => x.id.CompareTo(y.id));   // caller sorts by id
            return b;
        }
        var a = Make(Body(1, new Vector2(0f, 0f), 1f), Body(2, new Vector2(0.3f, 0f), 1f), Body(3, new Vector2(0.6f, 0.1f), 1f));
        var b = Make(Body(3, new Vector2(0.6f, 0.1f), 1f), Body(1, new Vector2(0f, 0f), 1f), Body(2, new Vector2(0.3f, 0f), 1f));
        CollisionStep.Resolve(a, 3, Open, 6);
        CollisionStep.Resolve(b, 3, Open, 6);
        for (int i = 0; i < 3; i++) { Assert.AreEqual(a[i].id, b[i].id); Assert.AreEqual(a[i].pos, b[i].pos); }
    }
}
```

- [ ] **Step 2: Run the tests; verify they FAIL.** `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` (expect a compile error: `CollisionStep`/`CollisionBody` not defined) → `run_tests mode=EditMode assembly_names=["Minifantasy.InstanceSim.Tests"]` → `get_test_job` → expect compile failure / no run.

- [ ] **Step 3: Implement `CollisionStep.cs`.** Create `Assets/_Scripts/InstanceSim/CollisionStep.cs`:

```csharp
using System;
using UnityEngine;

// Deterministic kinematic player-vs-player resolution for ONE underworld room, plus the broadphase overlap query
// the future hitbox layer reuses. Pure (no Time/random/Netcode/Game/physics) — same discipline as
// MovementStep/InstanceStep, so it stays unit-testable and can later run inside client prediction. The caller
// (AttackSimSystem) maps ServerPlayer <-> CollisionBody and pre-sorts bodies by id for deterministic order.
public struct CollisionBody
{
    public ulong id;       // deterministic sort key + write-back key
    public Vector2 pos;    // mutated in place during Resolve
    public float radius;
    public float invMass;  // 0 = pinned (held this tick); 1 = mover; future: stat-driven mass / knockback hook
}

public static class CollisionStep
{
    const float Eps = 1e-5f;

    // Resolve overlaps for one room. bodies[0..count) MUST be pre-sorted by id so iteration order — and thus the
    // result — is deterministic. Mover-yields: a pinned body (invMass 0) holds its ground and a mover absorbs the
    // push; two movers split it by invMass; two pinned bodies (e.g. a spawn overlap) fall back to an equal split
    // so they still separate. Correction is along the contact normal only, so a mover slides around a pinned body.
    public static void Resolve(CollisionBody[] bodies, int count, Func<Vector2, bool> walkable, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                {
                    Vector2 d = bodies[j].pos - bodies[i].pos;
                    float rsum = bodies[i].radius + bodies[j].radius;
                    float dist2 = d.sqrMagnitude;
                    if (dist2 >= rsum * rsum) continue;                        // not overlapping

                    float dist = Mathf.Sqrt(dist2);
                    Vector2 normal = dist > Eps ? d / dist : Vector2.right;     // degenerate same-pos -> fixed axis (i<j => i goes -x)
                    float pen = rsum - dist;

                    float inv = bodies[i].invMass + bodies[j].invMass;
                    float wi, wj;
                    if (inv <= 0f) { wi = 0.5f; wj = 0.5f; }                    // both pinned -> separate gently
                    else { wi = bodies[i].invMass / inv; wj = bodies[j].invMass / inv; }

                    bodies[i].pos = SlideClamp(bodies[i].pos, -normal * (pen * wi), walkable);
                    bodies[j].pos = SlideClamp(bodies[j].pos,  normal * (pen * wj), walkable);
                }
    }

    // Positional correction with the SAME per-axis wall slide as MovementStep: a body pushed toward a wall slides
    // along it instead of entering non-walkable space (walls win over the push).
    static Vector2 SlideClamp(Vector2 from, Vector2 delta, Func<Vector2, bool> walkable)
    {
        Vector2 next = from;
        Vector2 tryX = new Vector2(from.x + delta.x, next.y);
        if (delta.x != 0f && walkable(tryX)) next.x = tryX.x;
        Vector2 tryY = new Vector2(next.x, from.y + delta.y);
        if (delta.y != 0f && walkable(tryY)) next.y = tryY.y;
        return next;
    }
}
```

- [ ] **Step 4: Run the tests; verify they PASS.** `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` (empty) → `run_tests mode=EditMode assembly_names=["Minifantasy.InstanceSim.Tests"]` → `get_test_job` → all green.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Scripts/InstanceSim/CollisionStep.cs Assets/_Scripts/InstanceSim/CollisionStep.cs.meta Assets/Tests/EditMode/InstanceSim/CollisionStepTests.cs Assets/Tests/EditMode/InstanceSim/CollisionStepTests.cs.meta
git commit -m "InstanceSim: pure deterministic mover-yields circle collision resolver (unit-tested)"
```

---

# Task 2: `CollisionStep.Overlap` — broadphase query seam (hitbox foundation)

**Files:**
- Modify: `Assets/_Scripts/InstanceSim/CollisionStep.cs`
- Modify: `Assets/Tests/EditMode/InstanceSim/CollisionStepTests.cs`

- [ ] **Step 1: Write the failing test.** Append inside `CollisionStepTests` (before the final `}`):

```csharp
    [Test]
    public void Overlap_ReturnsOnlyBodiesWithinProbe()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(0f, 0f), 0f),     // radius 0.5
            Body(2, new Vector2(0.8f, 0f), 0f),   // radius 0.5
            Body(3, new Vector2(5f, 0f), 0f),     // far away
        };
        var results = new int[8];
        int n = CollisionStep.Overlap(bodies, 3, new Vector2(0.2f, 0f), 0.3f, results);
        Assert.AreEqual(2, n);          // body0 (dist 0.2 < 0.8) and body1 (dist 0.6 < 0.8); body2 misses
        Assert.AreEqual(0, results[0]);
        Assert.AreEqual(1, results[1]);
    }
```

- [ ] **Step 2: Run the test; verify it FAILS.** `refresh_unity scope=all wait_for_ready=true` → `run_tests mode=EditMode assembly_names=["Minifantasy.InstanceSim.Tests"]` → `get_test_job` → fails to compile (`Overlap` not defined).

- [ ] **Step 3: Implement `Overlap`.** In `CollisionStep.cs`, add this method to the `CollisionStep` class (immediately after `Resolve`):

```csharp
    // Broadphase query seam for the future hitbox layer: fill `results` with indices of bodies whose circle
    // overlaps the (center,radius) probe; returns the count written. Unused by collision itself — v1 ships it so
    // the OnStrike hit seam later becomes "Overlap roommates, then pixel-perfect narrowphase" with no rework.
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
```

- [ ] **Step 4: Run the test; verify it PASSES.** `run_tests mode=EditMode assembly_names=["Minifantasy.InstanceSim.Tests"]` → `get_test_job` → all green.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Scripts/InstanceSim/CollisionStep.cs Assets/Tests/EditMode/InstanceSim/CollisionStepTests.cs
git commit -m "InstanceSim: CollisionStep.Overlap circle broadphase query (hitbox seam)"
```

---

# Task 3: `MovementSettings` collision knobs

**Files:**
- Modify: `Assets/_Scripts/Net/MovementSettings.cs`

- [ ] **Step 1: Add the two fields.** In `MovementSettings.cs`, add inside the class (after `inputBufferCapacity`):

```csharp
    [Min(0.05f)] public float collisionRadius = 0.4f;    // per-player body radius, world units (1 cell == 1 unit)
    [Min(1)]     public int   collisionIterations = 3;   // relaxation passes per room per fixed tick
```

- [ ] **Step 2: Verify compile.** `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` → expect empty.

- [ ] **Step 3: Commit.**

```bash
git add Assets/_Scripts/Net/MovementSettings.cs
git commit -m "Net: MovementSettings collisionRadius + collisionIterations knobs"
```

---

# Task 4: `AttackSimSystem` two-phase — integrate, then resolve per room

**Files:**
- Modify: `Assets/_Scripts/Net/AttackSimSystem.cs`

End state: every in-instance player integrates as today (Phase A), then players are grouped by `regionKey` and each room is resolved by `CollisionStep` (Phase B), writing resolved positions back to `ServerPlayer.worldPos`. Overworld is untouched. No wire changes.

- [ ] **Step 1: Add the `using` for `List`.** At the top of `AttackSimSystem.cs`, below `using UnityEngine;`, add:

```csharp
using System.Collections.Generic;
```

- [ ] **Step 2: Add scratch fields + the grouping struct.** Inside the `AttackSimSystem` class, just below the existing `static Game _gm;` / `_walkAt` lines, add:

```csharp
    const float MovedEps = 1e-6f;   // sq-distance below which a player is "pinned" (didn't move this tick)

    struct Pending { public ulong id; public ServerPlayer sp; public Vector2Int region; public float invMass; }
    static readonly List<Pending> _pending = new();
    static CollisionBody[] _bodies = new CollisionBody[8];
    static readonly Comparison<Pending> _byRegionThenId = (a, b) =>
    {
        int c = a.region.x.CompareTo(b.region.x); if (c != 0) return c;
        c = a.region.y.CompareTo(b.region.y);     if (c != 0) return c;
        return a.id.CompareTo(b.id);
    };
```

- [ ] **Step 3: Replace the body of `StepInstanceFixed` with the two-phase version.** Replace the entire current method ([AttackSimSystem.cs:11-39](Assets/_Scripts/Net/AttackSimSystem.cs)) with:

```csharp
    public static void StepInstanceFixed(PlayerRegistry reg, WeaponCatalog catalog, MovementSettings cfg, float dt)
    {
        var gm = Game.Instance; if (gm == null) return;
        _gm = gm;

        // ---- Phase A: integrate each in-instance player (attack + lunge + movement vs walls), as before ----
        _pending.Clear();
        foreach (var kv in reg.Players)
        {
            var sp = kv.Value;
            if (!sp.inInstance) continue;
            Vector2 startPos = sp.worldPos;
            if (sp.serverInputs != null)
            {
                var def = catalog != null ? catalog.Get(sp.weaponId) : null;
                while (sp.serverInputs.TryGet(sp.lastProcessedTick + 1, out var c))
                {
                    if (def != null)
                    {
                        var ctx = new InstanceCtx { timeline = def.Timeline, scales = sp.attackScales, dt = dt, speed = cfg.moveSpeed, walkable = _walkAt };
                        sp.prevAttackPhase = sp.attackState.phase;
                        var atk = sp.attackState; var pos = sp.worldPos;
                        InstanceStep.Step(ref atk, ref pos, new InstanceInput { rawMove = c.rawMove, attack = ToIntent(c) }, ctx);
                        sp.attackState = atk; sp.worldPos = pos;
                        EmitTransitions(kv.Key, sp, def, c.tick);
                    }
                    else
                    {
                        sp.worldPos = MovementStep.Step(sp.worldPos, c.rawMove, dt, cfg.moveSpeed, _walkAt);
                    }
                    sp.lastInput = c.rawMove;
                    sp.lastProcessedTick++;
                }
            }
            // mover-yields weighting: moved this tick -> mover (1), otherwise pinned (0, holds ground)
            float invMass = (sp.worldPos - startPos).sqrMagnitude > MovedEps ? 1f : 0f;
            _pending.Add(new Pending { id = kv.Key, sp = sp, region = sp.regionKey, invMass = invMass });
        }

        // ---- Phase B: resolve player-vs-player overlaps per room (deterministic: sorted by region then id) ----
        if (_pending.Count < 2) return;
        _pending.Sort(_byRegionThenId);
        int start = 0;
        while (start < _pending.Count)
        {
            int end = start + 1;
            while (end < _pending.Count && _pending[end].region == _pending[start].region) end++;
            int n = end - start;
            if (n > 1)
            {
                if (_bodies.Length < n) _bodies = new CollisionBody[Mathf.NextPowerOfTwo(n)];
                for (int k = 0; k < n; k++)
                {
                    var pp = _pending[start + k];
                    _bodies[k] = new CollisionBody { id = pp.id, pos = pp.sp.worldPos, radius = cfg.collisionRadius, invMass = pp.invMass };
                }
                CollisionStep.Resolve(_bodies, n, _walkAt, cfg.collisionIterations);
                for (int k = 0; k < n; k++) _pending[start + k].sp.worldPos = _bodies[k].pos;
            }
            start = end;
        }
    }
```

(Leave `ToIntent`, `EmitTransitions`, `Evt`, and `OnStrike` exactly as they are — only `StepInstanceFixed` changes.)

- [ ] **Step 4: Verify compile + existing tests.** `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` (empty) → `run_tests mode=EditMode assembly_names=["Minifantasy.InstanceSim.Tests"]` → `get_test_job` → green (the resolver tests still pass; nothing else regressed).

- [ ] **Step 5: Verify Play boots clean.** `manage_editor action=play` → poll `editor_state` until playing → `read_console types=["error","warning"]` (no new errors) → `manage_editor action=stop`.

- [ ] **Step 6: Commit.**

```bash
git add Assets/_Scripts/Net/AttackSimSystem.cs
git commit -m "Net: two-phase in-instance sim — integrate then resolve player collision per room"
```

- [ ] **Step 7: Manual host verification (required — MCP cannot drive the IMGUI Host button).** Ask the user (Ryan) to run host + a second client, both `enter` the same underworld site/room, and confirm:
  - Two players **cannot walk through each other** (you stop/slide on contact).
  - Walking into a **standing** player pushes **you** around them while **they hold their ground** (mover-yields).
  - Two players mutually shoving behave sanely (no jitter explosion, no tunneling).
  - You **cannot** be shoved into or through the **moat wall**.
  - **Overworld** players still overlap freely (no collision there).
  - If bodies feel too large/small, tune `collisionRadius` in `movement.json`.

---

## Self-review checklist (run after the last task)
- [ ] **Spec coverage:** `CollisionBody` + `Resolve` (Task 1, spec §4); `Overlap` broadphase seam (Task 2, spec §8); `collisionRadius`/`collisionIterations` (Task 3, spec §5); two-phase integrate→group→resolve with `invMass` from moved-this-tick (Task 4, spec §6); no wire/prefab/scene edits (spec §9 "Unchanged"); manual host checks (Task 4 Step 7, spec §10). ✔
- [ ] **No placeholders:** every step shows full test or impl code and the exact MCP/commit commands. ✔
- [ ] **Type consistency:** `CollisionBody{id,pos,radius,invMass}`, `CollisionStep.Resolve(bodies,count,walkable,iterations)`, `CollisionStep.Overlap(bodies,count,center,radius,results)`, `MovementSettings.collisionRadius/collisionIterations` are used identically across Tasks 1–4. ✔
- [ ] All `Minifantasy.InstanceSim.Tests` green; Play boots clean; manual-host items listed for the user.

## Manual (host-required) verification summary
MCP can't drive the IMGUI Host button. After Task 4 the user runs host + a second client in one underworld room to confirm: players can't walk through each other; mover-yields (walking into a stationary player pushes the mover around them); mutual shoving is stable; no shoving through the moat wall; overworld unaffected. EditMode tests + a clean Play boot are the MCP-verifiable gates.
