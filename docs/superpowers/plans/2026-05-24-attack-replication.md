# Attack Replication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replicate in-instance attacks across the network — server-authoritative state + lunge, owner-side prediction/reconciliation/rollback, hybrid wire (snapshot pose + reliable events), riding the existing AOI snapshot — with an `OnStrike` hit seam and no damage in v1.

**Architecture:** One pure `InstanceStep` (attack→lunge→move) is run by client predict, client replay, and server sim. Lunge moves into the Combat core. Wire structs carry primitives only (no `Movement`→`Combat` asmdef coupling). Attack data rides the existing per-viewer AOI snapshot + a reliable event RPC. Spec: `docs/superpowers/specs/2026-05-24-attack-replication-design.md`.

**Tech Stack:** Unity 6, Netcode for GameObjects (custom hub, no per-player NetworkObject), C#, NUnit EditMode tests, unity-mcp for compile/test/Play verification.

---

## Conventions used in every task

- **MCP compile/verify chain** (`execute_code` is broken on this machine): after editing/adding `.cs`, run `refresh_unity scope=all wait_for_ready=true` → `read_console types=["error"]` (expect empty) → for tests `run_tests mode=EditMode assembly_names=[...]` then poll `get_test_job`.
- **Adding a new `.cs` file:** always `refresh_unity scope=all` (not `scripts`) so Unity imports the new file before compiling.
- **Commits:** new files committed on their own; never `git add` shared WIP files (scene, `Ghost.prefab`, settings, unrelated `_Combat` assets). **No Claude attribution** in messages.
- **Assembly boundaries:** pure cores `Minifantasy.Combat` (`Combat/Core/`) and `Minifantasy.Movement` (`Net/Movement/`) must not reference each other or Netcode/Game. New shared step lives in new `Minifantasy.InstanceSim` (refs both). Wire structs (Netcode) live in Net (Assembly-CSharp).

---

# Phase 1 — Lunge into the core + shared `InstanceStep` (local refactor, behavior-identical)

End state: the local owner derives the lunge via the pure core through `InstanceStep`, still **sending the same effective move** it sends today, so single-player feel and the unchanged server are untouched. Pure logic is unit-tested.

## Task 1.1: Lunge curve into `AttackTimeline`

**Files:**
- Modify: `Assets/_Scripts/Combat/Core/AttackTimeline.cs`
- Modify: `Assets/_Scripts/Combat/AttackDefinition.cs` (`BuildTimeline`)

- [ ] **Step 1: Add the field.** In `AttackTimeline.cs`, add to the class:

```csharp
    public AnimationCurve lungeCurve;  // copied from the SO; lets the pure core compute the lunge without an SO ref
```

- [ ] **Step 2: Populate it in `BuildTimeline`.** In `AttackDefinition.cs`, inside the object initializer returned by `BuildTimeline()`, add:

```csharp
            lungeCurve = lungeCurve,
```

(the right-hand `lungeCurve` is the SO's existing field).

- [ ] **Step 3: Verify compile.** `refresh_unity scope=all` → `read_console types=["error"]` → expect empty.

- [ ] **Step 4: Commit.**

```bash
git add Assets/_Scripts/Combat/Core/AttackTimeline.cs Assets/_Scripts/Combat/AttackDefinition.cs
git commit -m "Combat: carry lungeCurve in AttackTimeline (lunge into the deterministic core)"
```

## Task 1.2: `AttackLogic.LungeVelocity` (pure)

**Files:**
- Modify: `Assets/_Scripts/Combat/Core/AttackLogic.cs`
- Test: `Assets/Tests/EditMode/Combat/AttackLogicTests.cs`

- [ ] **Step 1: Write the failing tests.** Append to `AttackLogicTests.cs` (before the final `}`):

```csharp
    static AttackTimeline TimelineWithCurve(AnimationCurve c)
    {
        var tl = MakeTimeline();
        tl.lungeCurve = c;
        return tl;
    }

    [Test]
    public void LungeVelocity_NullOutsideHitWindow()
    {
        var tl = TimelineWithCurve(AnimationCurve.Constant(0, 1, 1f));
        Assert.IsNull(AttackLogic.LungeVelocity(new AttackState { phase = AttackPhase.Idle }, tl));
    }

    [Test]
    public void LungeVelocity_ZeroDuringWindup()
    {
        var tl = TimelineWithCurve(AnimationCurve.Constant(0, 1, 1f));
        Assert.AreEqual(Vector2.zero, AttackLogic.LungeVelocity(new AttackState { phase = AttackPhase.Anticipation }, tl));
        Assert.AreEqual(Vector2.zero, AttackLogic.LungeVelocity(new AttackState { phase = AttackPhase.TapWindup }, tl));
    }

    [Test]
    public void LungeVelocity_InHit_IsLockedAimTimesCurve()
    {
        var tl = TimelineWithCurve(AnimationCurve.Constant(0, 1, 1f)); // curve = 1 everywhere
        var s = new AttackState { phase = AttackPhase.Hit, phaseElapsed = 0f, lockedAim = new Vector2(1, 0) };
        var v = AttackLogic.LungeVelocity(s, tl).Value;
        Assert.AreEqual(1f, v.x, 1e-3f);
        Assert.AreEqual(0f, v.y, 1e-3f);
    }

    [Test]
    public void LungeVelocity_NullCurve_IsZeroVector()
    {
        var tl = MakeTimeline(); // lungeCurve null
        var s = new AttackState { phase = AttackPhase.Hit, lockedAim = new Vector2(1, 0) };
        Assert.AreEqual(Vector2.zero, AttackLogic.LungeVelocity(s, tl).Value);
    }
```

- [ ] **Step 2: Run, expect FAIL.** `run_tests mode=EditMode assembly_names=["Minifantasy.Combat.Tests"]` → `get_test_job` → compile error / missing `LungeVelocity`.

- [ ] **Step 3: Implement.** In `AttackLogic.cs`, add (mirrors the deleted `LocalPlayer.AttackMoveOverride` semantics: `null`=idle/normal WASD, `Vector2.zero`=rooted windup, vector=lunge):

```csharp
    // The movement override an attack imposes: null outside an attack (normal WASD), zero during the wind-up
    // (rooted), and the lunge vector during hit/follow-through (lockedAim * curve(progress)). Pure.
    public static Vector2? LungeVelocity(AttackState s, AttackTimeline tl)
    {
        switch (s.phase)
        {
            case AttackPhase.Hit:
            case AttackPhase.FollowThrough:
                float speed = tl.lungeCurve != null ? Mathf.Clamp01(tl.lungeCurve.Evaluate(LungeProgress(s, tl))) : 0f;
                return s.lockedAim * speed;
            case AttackPhase.Anticipation:
            case AttackPhase.TapWindup:
                return Vector2.zero;
            default:
                return null;
        }
    }
```

- [ ] **Step 4: Run, expect PASS.** `run_tests` Combat → all green.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Scripts/Combat/Core/AttackLogic.cs Assets/Tests/EditMode/Combat/AttackLogicTests.cs
git commit -m "Combat: add pure AttackLogic.LungeVelocity (lunge fully in the core)"
```

## Task 1.3: `Minifantasy.InstanceSim` asmdef + `InstanceStep`

**Files:**
- Create: `Assets/_Scripts/InstanceSim/Minifantasy.InstanceSim.asmdef`
- Create: `Assets/_Scripts/InstanceSim/InstanceStep.cs`
- Create: `Assets/Tests/EditMode/InstanceSim/Minifantasy.InstanceSim.Tests.asmdef`
- Create: `Assets/Tests/EditMode/InstanceSim/InstanceStepTests.cs`

- [ ] **Step 1: Create the runtime asmdef** `Minifantasy.InstanceSim.asmdef`:

```json
{
    "name": "Minifantasy.InstanceSim",
    "references": ["Minifantasy.Combat", "Minifantasy.Movement"],
    "autoReferenced": true,
    "noEngineReferences": false
}
```

- [ ] **Step 2: Create `InstanceStep.cs`:**

```csharp
using System;
using UnityEngine;

// One deterministic in-instance tick: step the attack, derive the lunge, then step movement (lunge overrides
// WASD). Pure (no Time/random/Netcode). Shared by client predict, client replay, and server sim — the single
// source of truth that keeps all three in lockstep, exactly like MovementStep does for movement alone.
public struct InstanceInput
{
    public Vector2 rawMove;     // raw WASD intent (-1/0/1 per axis); used only when the attack imposes no lunge
    public AttackIntent attack; // reconstructed from wire bits on each side
}

public struct InstanceCtx
{
    public AttackTimeline timeline;
    public PhaseScales scales;
    public float dt;
    public float speed;
    public Func<Vector2, bool> walkable;
}

public static class InstanceStep
{
    public static void Step(ref AttackState atk, ref Vector2 pos, in InstanceInput cmd, in InstanceCtx ctx)
    {
        atk = AttackLogic.Step(atk, cmd.attack, ctx.timeline, ctx.scales, ctx.dt);
        Vector2? lunge = AttackLogic.LungeVelocity(atk, ctx.timeline);
        Vector2 move = lunge ?? cmd.rawMove;
        pos = MovementStep.Step(pos, move, ctx.dt, ctx.speed, ctx.walkable);
    }
}
```

- [ ] **Step 3: Create the test asmdef** `Minifantasy.InstanceSim.Tests.asmdef`:

```json
{
    "name": "Minifantasy.InstanceSim.Tests",
    "references": ["Minifantasy.InstanceSim", "Minifantasy.Combat", "Minifantasy.Movement", "UnityEngine.TestRunner", "UnityEditor.TestRunner"],
    "includePlatforms": ["Editor"],
    "overrideReferences": true,
    "precompiledReferences": ["nunit.framework.dll"],
    "autoReferenced": false,
    "defineConstraints": ["UNITY_INCLUDE_TESTS"]
}
```

- [ ] **Step 4: Write failing tests** `InstanceStepTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

public class InstanceStepTests
{
    static InstanceCtx Ctx(AttackTimeline tl) => new InstanceCtx
    {
        timeline = tl, scales = PhaseScales.One, dt = 0.02f, speed = 4f,
        walkable = _ => true,
    };

    static AttackTimeline Tl()
    {
        return new AttackTimeline
        {
            anticipation    = new[] { new TimedFrame { column = 0, duration = 0.1f } },
            tapAnticipation = new[] { new TimedFrame { column = 0, duration = 0.1f } },
            hit             = new[] { new TimedFrame { column = 1, duration = 0.1f } },
            followThrough   = new[] { new TimedFrame { column = 2, duration = 0.1f } },
            directions      = new[] { new DirectionEntry { canonicalDir = new Vector2(1, 0), row = 0 } },
            dirs            = new[] { new Vector2(1, 0) },
            lungeCurve      = AnimationCurve.Constant(0, 1, 1f),
        };
    }

    static InstanceInput Move(Vector2 m) => new InstanceInput { rawMove = m, attack = new AttackIntent { aimDir = new Vector2(1, 0) } };

    [Test]
    public void Deterministic_SameInputs_SameOutput()
    {
        var tl = Tl();
        var a1 = default(AttackState); Vector2 p1 = Vector2.zero;
        var a2 = default(AttackState); Vector2 p2 = Vector2.zero;
        for (int i = 0; i < 50; i++)
        {
            InstanceStep.Step(ref a1, ref p1, Move(new Vector2(1, 0)), Ctx(tl));
            InstanceStep.Step(ref a2, ref p2, Move(new Vector2(1, 0)), Ctx(tl));
        }
        Assert.AreEqual(p1, p2);
        Assert.AreEqual(a1.phase, a2.phase);
    }

    [Test]
    public void NoAttack_MovesByRawWASD()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero;
        InstanceStep.Step(ref atk, ref pos, Move(new Vector2(1, 0)), Ctx(tl));
        Assert.Greater(pos.x, 0f);   // walked east
    }

    [Test]
    public void Windup_RootsMovement()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero;
        var press = new InstanceInput { rawMove = new Vector2(1, 0), attack = new AttackIntent { pressed = true, held = true, aimDir = new Vector2(1, 0) } };
        InstanceStep.Step(ref atk, ref pos, press, Ctx(tl)); // -> Anticipation (rooted)
        Assert.AreEqual(AttackPhase.Anticipation, atk.phase);
        Assert.AreEqual(0f, pos.x, 1e-4f);   // rooted despite rawMove east
    }

    [Test]
    public void Hit_LungesAlongLockedAim()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero;
        var press = new InstanceInput { rawMove = Vector2.zero, attack = new AttackIntent { pressed = true, held = true, aimDir = new Vector2(1, 0) } };
        InstanceStep.Step(ref atk, ref pos, press, Ctx(tl));
        var release = new InstanceInput { rawMove = Vector2.zero, attack = new AttackIntent { released = true, aimDir = new Vector2(1, 0) } };
        InstanceStep.Step(ref atk, ref pos, release, Ctx(tl)); // commit -> TapWindup
        // advance into Hit
        for (int i = 0; i < 10 && atk.phase != AttackPhase.Hit; i++)
            InstanceStep.Step(ref atk, ref pos, new InstanceInput { attack = new AttackIntent { aimDir = new Vector2(1, 0) } }, Ctx(tl));
        Assert.AreEqual(AttackPhase.Hit, atk.phase);
        float before = pos.x;
        InstanceStep.Step(ref atk, ref pos, new InstanceInput { rawMove = new Vector2(0, 1), attack = new AttackIntent { aimDir = new Vector2(1, 0) } }, Ctx(tl));
        Assert.Greater(pos.x, before);   // lunged east (locked aim), not north (rawMove)
        Assert.AreEqual(0f, pos.y, 1e-4f);
    }
}
```

- [ ] **Step 5: Verify FAIL→PASS.** `refresh_unity scope=all` → `read_console` → `run_tests mode=EditMode assembly_names=["Minifantasy.InstanceSim.Tests"]` → fix until green.

- [ ] **Step 6: Commit.**

```bash
git add Assets/_Scripts/InstanceSim Assets/Tests/EditMode/InstanceSim
git commit -m "InstanceSim: shared deterministic attack+lunge+move step (unit-tested)"
```

## Task 1.4: Route the local owner through `InstanceStep` (behavior-identical)

**Files:**
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs`
- Modify: `Assets/_Scripts/Net/PredictionSystem.cs`

Goal: replace the separate `attack.Tick` + `AttackMoveOverride` + `prediction.FixedTick(OverrideInput)` with one `InstanceStep` call per fixed tick. **Still send the effective move** (`lunge ?? rawMove`) so the unchanged server matches — pure local refactor.

- [ ] **Step 1: Add a unified fixed step to `PredictionSystem`.** Add a method that steps attack+move together and returns the effective input it used (so it can still be sent), writing the predicted attack state out:

```csharp
    // Unified in-instance fixed step: runs the shared InstanceStep, stores the frame, sends the effective input.
    // Phase 1 sends the effective move so the (still unchanged) server matches; Phase 2 switches the wire to raw.
    public void FixedTickInstance(ref AttackState atk, AttackIntent attack, Vector2 rawMove, AttackTimeline tl, PhaseScales scales, float dt)
    {
        var cfg = Game.Instance.MovementCfg;
        Tick++;
        prevPos = Pos;
        var ctx = new InstanceCtx { timeline = tl, scales = scales, dt = dt, speed = cfg.moveSpeed, walkable = Walkable };
        var cmd = new InstanceInput { rawMove = rawMove, attack = attack };
        InstanceStep.Step(ref atk, ref Pos_backing, cmd, ctx);   // see Step 2 re: Pos setter
        Vector2 effective = AttackLogic.LungeVelocity(atk, tl) ?? rawMove;
        buffer.Store(new InputFrame { tick = Tick, input = effective, predictedPos = Pos });
        ReplicationHub.Instance.SubmitInputTickRpc(Tick, effective);
    }
```

- [ ] **Step 2: Make `Pos` settable by ref.** `Pos` is `{ get; private set; }`. Add a backing field usable by `ref`:

```csharp
    Vector2 Pos_backing;
    public Vector2 Pos { get => Pos_backing; private set => Pos_backing = value; }
```

(Replace the auto-property `public Vector2 Pos { get; private set; }` with this. All existing assignments to `Pos` still compile.)

- [ ] **Step 3: Rewrite `LocalPlayer.FixedUpdate`** to call the unified step instead of `attack.Tick` + `prediction.FixedTick`. The attack state now lives where prediction can step it — keep `AttackSystem` for the hit-window log + state ownership, but step it through `PredictionSystem`. Replace the body that builds `ai`, calls `attack.Tick`, sets `OverrideInput`, and calls `prediction.FixedTick`:

```csharp
    void FixedUpdate()
    {
        if (!Ready) return;
        bool inst = InInstance;
        if (inst && !wasInInstance && SelfWorldPos.HasValue) prediction.Activate(SelfWorldPos.Value);
        else if (!inst && wasInInstance) prediction.Deactivate();
        wasInInstance = inst;

        if (prediction.Active && currentAttack != null)
        {
            var ai = new AttackIntent
            {
                pressed = attackPressed, held = attackHeld, released = attackReleased,
                feint = attackFeint, aimDir = attackAim,
            };
            attackPressed = attackReleased = attackFeint = false;
            var atk = attack.State;
            prediction.FixedTickInstance(ref atk, ai, attackRawMove, currentAttack.Timeline, attack.Scales, Time.fixedDeltaTime);
            attack.SetState(atk);   // see Step 4
        }
        else if (prediction.Active)
        {
            prediction.FixedTick(Time.fixedDeltaTime);   // no weapon equipped: movement only
        }
    }
```

(Replace the `attackAim` capture in `Update` to also store `attackRawMove` = the raw WASD `intent.dir`; add `Vector2 attackRawMove;` beside the other latch fields. `AttackMoveOverride` and `RotateDeg` are already removed from a prior session; if any remnant remains, delete it.)

- [ ] **Step 4: Add `AttackSystem.SetState`** so the prediction-stepped state flows back, and keep the hit-window log there:

```csharp
    public void SetState(AttackState s)
    {
        if (prevPhase != AttackPhase.Hit && s.phase == AttackPhase.Hit && /* def known by caller */ true)
            Debug.Log("[attack] hit window open");
        prevPhase = s.phase;
        state = s;
    }
```

(Trim `AttackSystem.Tick` to delegate to `AttackLogic`-via-prediction or keep it for non-predicted contexts; the owner now drives state through `PredictionSystem`. Keep `State`/`Scales` as-is.)

- [ ] **Step 5: Verify behavior parity.** `refresh_unity scope=all` → `read_console` empty → `run_tests mode=EditMode` (all assemblies) green → enter Play, confirm boot clean (`read_console` empty), stop Play. Single-player attack feel unchanged (manual: hold/release/feint still animate locally; movement + lunge identical).

- [ ] **Step 6: Commit.**

```bash
git add Assets/_Scripts/Net/LocalPlayer.cs Assets/_Scripts/Net/PredictionSystem.cs Assets/_Scripts/Combat/AttackSystem.cs
git commit -m "Net: route owner attack+move through shared InstanceStep (behavior parity)"
```

---

# Phase 2 — Server authority + the wire

End state: client sends raw move + attack input; server runs `InstanceStep` authoritatively, derives the lunge, and emits attack pose (snapshot) + events to AOI viewers. No remote rendering yet (Phase 3).

## Task 2.1: `InputCommand` + aim quantization

**Files:**
- Create: `Assets/_Scripts/Net/InputCommand.cs`
- Create: `Assets/_Scripts/Combat/Core/AimQuant.cs` (pure; in Combat so both sides share it)
- Test: `Assets/Tests/EditMode/Combat/AimQuantTests.cs`

- [ ] **Step 1: Failing test for the aim round-trip** `AimQuantTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

public class AimQuantTests
{
    [Test]
    public void RoundTrip_PreservesDirectionWithinTolerance()
    {
        foreach (var deg in new[] { 0f, 45f, 90f, 137f, 180f, -90f, 359f })
        {
            var v = new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
            ushort q = AimQuant.Encode(v);
            Vector2 r = AimQuant.Decode(q);
            Assert.Less(Vector2.Angle(v, r), 0.02f);   // < 0.02 degrees error
        }
    }

    [Test]
    public void Deterministic_SameVector_SameCode()
    {
        var v = new Vector2(0.3f, 0.7f);
        Assert.AreEqual(AimQuant.Encode(v), AimQuant.Encode(v));
    }
}
```

- [ ] **Step 2: Implement `AimQuant.cs`:**

```csharp
using UnityEngine;

// Quantize an aim direction to a ushort angle and back. The owner encodes BEFORE predicting and sending, so
// client prediction and the server consume the identical direction (determinism for reconciliation). Pure.
public static class AimQuant
{
    const float Scale = 65536f / 360f;
    public static ushort Encode(Vector2 dir)
    {
        if (dir.sqrMagnitude < 1e-12f) return 0;
        float deg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg; // -180..180
        if (deg < 0f) deg += 360f;
        return (ushort)(Mathf.Round(deg * Scale) % 65536f);
    }
    public static Vector2 Decode(ushort code)
    {
        float deg = code / Scale;
        float r = deg * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(r), Mathf.Sin(r));
    }
}
```

- [ ] **Step 3: Verify FAIL→PASS** (`Minifantasy.Combat.Tests`).

- [ ] **Step 4: Create `InputCommand.cs`** (Net, Assembly-CSharp):

```csharp
using Unity.Netcode;
using UnityEngine;

// One owner tick: raw move + attack edges (packed) + quantized aim + equipped weapon. Primitives only, so the
// pure Movement/Combat asmdefs are never referenced from the wire. The server reconstructs AttackIntent from bits.
public struct InputCommand : INetworkSerializable
{
    public uint tick;
    public Vector2 rawMove;
    public byte attackBits;   // bit0 pressed, bit1 held, bit2 released, bit3 feint
    public ushort aimAngle;
    public byte weaponId;

    public const byte Pressed = 1, Held = 2, Released = 4, Feint = 8;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref tick);
        s.SerializeValue(ref rawMove);
        s.SerializeValue(ref attackBits);
        s.SerializeValue(ref aimAngle);
        s.SerializeValue(ref weaponId);
    }
}
```

- [ ] **Step 5: Verify compile** (`refresh_unity scope=all` → console empty).

- [ ] **Step 6: Commit.**

```bash
git add Assets/_Scripts/Combat/Core/AimQuant.cs Assets/Tests/EditMode/Combat/AimQuantTests.cs Assets/_Scripts/Net/InputCommand.cs
git commit -m "Net/Combat: InputCommand wire struct + pure aim quantization"
```

## Task 2.2: `WeaponCatalog` SO

**Files:**
- Create: `Assets/_Scripts/Combat/WeaponCatalog.cs`
- Create asset: `Assets/_Combat/WeaponCatalog.asset` (via menu, in Step 3)

- [ ] **Step 1: Create `WeaponCatalog.cs`:**

```csharp
using UnityEngine;

// Shared byte-id ↔ AttackDefinition map. The wire carries a 1-byte id; the server resolves timing/lunge and
// remotes resolve frames from the same ordered list. LocalPlayer.weapons[] becomes a view onto Weapons.
[CreateAssetMenu(menuName = "Minifantasy/Weapon Catalog", fileName = "WeaponCatalog")]
public sealed class WeaponCatalog : ScriptableObject
{
    public AttackDefinition[] weapons;

    public AttackDefinition Get(byte id) => weapons != null && id < weapons.Length ? weapons[id] : null;
    public int IndexOf(AttackDefinition def)
    {
        if (weapons == null) return -1;
        for (int i = 0; i < weapons.Length; i++) if (weapons[i] == def) return i;
        return -1;
    }
}
```

- [ ] **Step 2: Verify compile.**

- [ ] **Step 3: Create the asset + populate** (manual/MCP): `Assets > Create > Minifantasy > Weapon Catalog` at `Assets/_Combat/WeaponCatalog.asset`; drag the `_Combat/*.asset` weapon definitions into `weapons` in the intended slot order (index = wire id). Confirm `Game`/`ReplicationHub`/`LocalPlayer` will reference this asset (wired in later tasks).

- [ ] **Step 4: Commit** (code only; the `.asset` is user-curated — commit separately if desired, do not bundle WIP):

```bash
git add Assets/_Scripts/Combat/WeaponCatalog.cs
git commit -m "Combat: WeaponCatalog SO (byte id <-> AttackDefinition)"
```

## Task 2.3: `ServerPlayer` attack fields + command buffer

**Files:**
- Modify: `Assets/_Scripts/Net/PlayerRegistry.cs`

- [ ] **Step 1: Add fields to `ServerPlayer`:**

```csharp
    public AttackState attackState;
    public PhaseScales attackScales = PhaseScales.One;
    public byte weaponId;
    public AttackPhase prevAttackPhase;          // for transition detection
    public System.Collections.Generic.Queue<AttackEvent> pendingEvents;  // drained into snapshots
```

(The existing `serverInputs` `InputRingBuffer` of `InputFrame` is replaced by a buffer of `InputCommand` — see Task 2.4 Step 1.)

- [ ] **Step 2: Verify compile** (will fail until `AttackEvent` exists — acceptable; sequence with Task 2.5). Defer commit until 2.5.

## Task 2.4: `AttackSimSystem` (server authoritative step + events + hit seam)

**Files:**
- Create: `Assets/_Scripts/Net/AttackSimSystem.cs`
- Modify: `Assets/_Scripts/Net/PlayerRegistry.cs` (`serverInputs` → `InputRingBuffer<InputCommand>` — generalize the ring, see Step 1)
- Modify: `Assets/_Scripts/Net/Movement/InputRingBuffer.cs` (generalize to `InputRingBuffer<T>` where `T` has a `tick`) **OR** add a parallel `CommandRingBuffer` in Net to keep `Minifantasy.Movement` pure.

> **Asmdef note:** `InputRingBuffer` lives in `Minifantasy.Movement`. To avoid that asmdef referencing Netcode/`InputCommand`, add a **`CommandRingBuffer`** (same ring logic, holds `InputCommand`) in `Assets/_Scripts/Net/` rather than generalizing the Movement one. Code below uses `CommandRingBuffer`.

- [ ] **Step 1: Create `CommandRingBuffer.cs`** (Net):

```csharp
// Fixed-capacity (power-of-two) ring of InputCommands, indexed by tick & mask. Mirrors InputRingBuffer but for
// the combined command (kept in Net so Minifantasy.Movement stays pure). A slot matches only if its tick equals.
public sealed class CommandRingBuffer
{
    readonly InputCommand[] buf;
    readonly uint mask;
    public CommandRingBuffer(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0) throw new System.ArgumentException("power of two >= 2");
        buf = new InputCommand[capacity];
        mask = (uint)(capacity - 1);
    }
    public void Store(InputCommand f) => buf[f.tick & mask] = f;
    public bool TryGet(uint tick, out InputCommand f) { f = buf[tick & mask]; return f.tick == tick; }
}
```

(Change `ServerPlayer.serverInputs` type to `CommandRingBuffer`.)

- [ ] **Step 2: Create `AttackSimSystem.cs`:**

```csharp
using UnityEngine;

// Server authoritative in-instance step: drain contiguous InputCommands, run the shared InstanceStep (attack +
// lunge + movement), advance lastProcessedTick, and on attack phase transitions enqueue AttackEvents + call the
// OnStrike hit seam. Mirrors PlayerSimSystem.StepInstanceFixed; replaces its in-instance body. Pure server-side.
public static class AttackSimSystem
{
    static Game _gm;
    static readonly System.Func<Vector2, bool> _walkAt = p => { var c = _gm.WorldToCell(p); return _gm.IsWalkable(c.x, c.y); };

    public static void StepInstanceFixed(PlayerRegistry reg, WeaponCatalog catalog, MovementSettings cfg, float dt)
    {
        var gm = Game.Instance; if (gm == null) return;
        _gm = gm;
        foreach (var kv in reg.Players)
        {
            var sp = kv.Value;
            if (!sp.inInstance || sp.serverInputs == null) continue;
            var def = catalog != null ? catalog.Get(sp.weaponId) : null;
            while (sp.serverInputs.TryGet(sp.lastProcessedTick + 1, out var c))
            {
                var intent = ToIntent(c);
                if (def != null)
                {
                    var ctx = new InstanceCtx { timeline = def.Timeline, scales = sp.attackScales, dt = dt, speed = cfg.moveSpeed, walkable = _walkAt };
                    sp.prevAttackPhase = sp.attackState.phase;
                    InstanceStep.Step(ref sp.attackState, ref sp.worldPos, new InstanceInput { rawMove = c.rawMove, attack = intent }, ctx);
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
    }

    static AttackIntent ToIntent(InputCommand c) => new AttackIntent
    {
        pressed = (c.attackBits & InputCommand.Pressed) != 0,
        held = (c.attackBits & InputCommand.Held) != 0,
        released = (c.attackBits & InputCommand.Released) != 0,
        feint = (c.attackBits & InputCommand.Feint) != 0,
        aimDir = AimQuant.Decode(c.aimAngle),
    };

    static void EmitTransitions(ulong id, ServerPlayer sp, AttackDefinition def, uint tick)
    {
        var prev = sp.prevAttackPhase; var now = sp.attackState.phase;
        if (prev == now) return;
        sp.pendingEvents ??= new System.Collections.Generic.Queue<AttackEvent>();
        if (prev == AttackPhase.Idle && now == AttackPhase.Anticipation)
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Started, sp, tick));
        if (now == AttackPhase.Hit)
        {
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Struck, sp, tick));
            OnStrike(id, sp, def, tick);   // hit seam (v1: log)
        }
        if (now == AttackPhase.Idle && prev == AttackPhase.Anticipation && sp.attackState.cooldown > 0f)
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Feinted, sp, tick));
    }

    static AttackEvent Evt(ulong id, byte kind, ServerPlayer sp, uint tick) => new AttackEvent
    {
        attackerId = id, kind = kind, weaponId = sp.weaponId, tick = tick, aimAngle = AimQuant.Encode(sp.attackState.lockedAim),
    };

    // The forward hitbox seam. v1: log. Later: sweep same-regionKey players within a weapon-derived volume.
    static void OnStrike(ulong id, ServerPlayer sp, AttackDefinition def, uint tick)
    {
        Debug.Log($"[attack] STRIKE id={id} weapon={def.attackId} tick={tick} pos={sp.worldPos}");
    }
}
```

- [ ] **Step 3: Verify compile** (needs `AttackEvent` from 2.5 — do 2.5 next, then refresh).

## Task 2.5: `AttackEvent` + `SnapshotEntry` attack block

**Files:**
- Create: `Assets/_Scripts/Net/AttackEvent.cs`
- Modify: `Assets/_Scripts/Net/SnapshotEntry.cs`
- Test: `Assets/Tests/EditMode/Net/SnapshotEntryTests.cs` (+ asmdef if none exists for Net tests — otherwise place under an existing reachable test asmdef; if Net has no test asmdef, create `Minifantasy.Net.Tests` referencing nothing but Assembly-CSharp is impossible → instead test the **pure** packing via a helper in Combat. See Step 1.)

> **Testability note:** `SnapshotEntry` lives in Assembly-CSharp (Netcode), which no asmdef can reference. To keep the pack/unpack **unit-testable**, put the pose **bit-packing** in a pure helper `AttackPose` in `Minifantasy.Combat` and have `SnapshotEntry` call it. Test `AttackPose` round-trip in `Minifantasy.Combat.Tests`.

- [ ] **Step 1: Create pure `AttackPose.cs`** (Combat) + failing test:

`Assets/_Scripts/Combat/Core/AttackPose.cs`:
```csharp
// Pure bit-packing of the remote-renderable attack pose into one byte: phase(3) | frame(3) | dir(2).
public static class AttackPose
{
    public static byte Pack(AttackPhase phase, int frame, int dir)
        => (byte)(((int)phase & 0x7) | ((frame & 0x7) << 3) | ((dir & 0x3) << 6));
    public static void Unpack(byte b, out AttackPhase phase, out int frame, out int dir)
    {
        phase = (AttackPhase)(b & 0x7);
        frame = (b >> 3) & 0x7;
        dir = (b >> 6) & 0x3;
    }
}
```

`Assets/Tests/EditMode/Combat/AttackPoseTests.cs`:
```csharp
using NUnit.Framework;

public class AttackPoseTests
{
    [Test]
    public void Pack_Unpack_RoundTrips()
    {
        byte b = AttackPose.Pack(AttackPhase.Hit, 5, 3);
        AttackPose.Unpack(b, out var ph, out var fr, out var dr);
        Assert.AreEqual(AttackPhase.Hit, ph);
        Assert.AreEqual(5, fr);
        Assert.AreEqual(3, dr);
    }
}
```

- [ ] **Step 2: Verify FAIL→PASS** (`Minifantasy.Combat.Tests`).

- [ ] **Step 3: Create `AttackEvent.cs`** (Net):

```csharp
using Unity.Netcode;

public struct AttackEvent : INetworkSerializable
{
    public ulong attackerId;
    public byte kind;        // Started/Struck/Feinted
    public byte weaponId;
    public uint tick;
    public ushort aimAngle;

    public const byte Started = 0, Struck = 1, Feinted = 2;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref attackerId);
        s.SerializeValue(ref kind);
        s.SerializeValue(ref weaponId);
        s.SerializeValue(ref tick);
        s.SerializeValue(ref aimAngle);
    }
}
```

- [ ] **Step 4: Extend `SnapshotEntry.cs`** — add the attack block + a flag bit, conditionally serialized:

```csharp
    public const byte SnapBit = 1, InInstanceBit = 2, AttackingBit = 4;

    // attack block (present iff AttackingBit) — set by the server from the authoritative attack state
    public byte weaponId;
    public byte pose;        // AttackPose.Pack(phase, frame, dir)
    public ushort residual;  // AimQuant of the residual/aim for weapon tilt
    public byte selfExtra;   // self entry only: bit0 windupComplete, bits1-7 quantized cooldown
```

Update `NetworkSerialize` to read/write the block conditionally (note `flags` must be serialized first so the reader knows whether to read the block):

```csharp
    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref x);
        s.SerializeValue(ref y);
        s.SerializeValue(ref flags);
        if ((flags & AttackingBit) != 0)
        {
            s.SerializeValue(ref weaponId);
            s.SerializeValue(ref pose);
            s.SerializeValue(ref residual);
            s.SerializeValue(ref selfExtra);   // only meaningful for the self entry; cheap to always include in-block
        }
    }
```

- [ ] **Step 5: Verify compile** (`refresh_unity scope=all` → console empty; `AttackSimSystem` from 2.4 now compiles too).

- [ ] **Step 6: Commit** (Tasks 2.3–2.5 together):

```bash
git add Assets/_Scripts/Net/PlayerRegistry.cs Assets/_Scripts/Net/AttackSimSystem.cs Assets/_Scripts/Net/CommandRingBuffer.cs Assets/_Scripts/Net/AttackEvent.cs Assets/_Scripts/Net/SnapshotEntry.cs Assets/_Scripts/Combat/Core/AttackPose.cs Assets/Tests/EditMode/Combat/AttackPoseTests.cs
git commit -m "Net: server attack sim + AttackEvent + snapshot attack block (+ pure AttackPose)"
```

## Task 2.6: AOI in-instance region-only

**Files:**
- Modify: `Assets/_Scripts/Net/Aoi/AreaOfInterestSystem.cs`
- Test: `Assets/Tests/EditMode/Aoi/AttackVisibilityTests.cs` (existing `Minifantasy.Aoi.Tests`)

- [ ] **Step 1: Failing test** (add to the Aoi test assembly):

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class AttackVisibilityTests
{
    [Test]
    public void InInstance_SeesWholeRoom_RegardlessOfRadius()
    {
        var room = new Vector2Int(16384, 16384);
        var players = new List<AoiPlayer>
        {
            new AoiPlayer(1, new Vector2(0, 0), room),
            new AoiPlayer(2, new Vector2(1000, 1000), room),   // far beyond showRadius, same room
        };
        var s = new ReplicationSettings { showRadius = 10, hideRadius = 12 };
        var result = new HashSet<ulong>();
        AreaOfInterestSystem.VisibleFor(1, players, s, new HashSet<ulong>(), result);
        Assert.IsTrue(result.Contains(2));   // same room → visible despite distance
    }

    [Test]
    public void Overworld_StillUsesRadius()
    {
        var ow = Vector2Int.zero;
        var players = new List<AoiPlayer>
        {
            new AoiPlayer(1, new Vector2(0, 0), ow),
            new AoiPlayer(2, new Vector2(1000, 0), ow),
        };
        var s = new ReplicationSettings { showRadius = 10, hideRadius = 12 };
        var result = new HashSet<ulong>();
        AreaOfInterestSystem.VisibleFor(1, players, s, new HashSet<ulong>(), result);
        Assert.IsFalse(result.Contains(2));   // far overworld → not visible
    }
}
```

- [ ] **Step 2: Run, expect FAIL** (`Minifantasy.Aoi.Tests`).

- [ ] **Step 3: Implement** — in `VisibleFor`, inside the loop, replace the radius test so in-instance (regionKey != (0,0)) skips it:

```csharp
            if (p.regionKey != viewer.regionKey) continue;
            if (viewer.regionKey != Vector2Int.zero) { result.Add(p.id); continue; }  // in-instance: whole room
            float d2 = (p.pos - viewer.pos).sqrMagnitude;
            float r2 = prior.Contains(p.id) ? hide2 : show2;
            if (d2 <= r2) result.Add(p.id);
```

- [ ] **Step 4: Run, expect PASS.**

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Scripts/Net/Aoi/AreaOfInterestSystem.cs Assets/Tests/EditMode/Aoi/AttackVisibilityTests.cs
git commit -m "Aoi: in-instance visibility is region-only (same room always sees each other)"
```

## Task 2.7: Wire the hub + client to the new command/sim/snapshot

**Files:**
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs`
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs`
- Modify: `Assets/_Scripts/Net/PredictionSystem.cs`
- Modify: `Assets/_Scripts/Game.cs` (own/serialize `WeaponCatalog` ref; expose to hub)

- [ ] **Step 1: `SubmitInputTickRpc` carries `InputCommand`.** Replace its signature + body:

```csharp
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void SubmitInputTickRpc(InputCommand cmd, RpcParams p = default)
    {
        if (!registry.TryGet(p.Receive.SenderClientId, out var sp)) return;
        if (cmd.tick <= sp.lastProcessedTick) return;
        sp.weaponId = cmd.weaponId;
        sp.serverInputs ??= new CommandRingBuffer(Mathf.Max(8, Mathf.NextPowerOfTwo(
            Game.Instance != null ? Game.Instance.MovementCfg.inputBufferCapacity : 128)));
        sp.serverInputs.Store(cmd);
    }
```

- [ ] **Step 2: Server FixedUpdate calls `AttackSimSystem`.** Replace the `PlayerSimSystem.StepInstanceFixed(...)` call in `ReplicationHub.FixedUpdate` ([ReplicationHub.cs:82](Assets/_Scripts/Net/ReplicationHub.cs)) with:

```csharp
        var catalog = Game.Instance != null ? Game.Instance.WeaponCatalog : null;
        if (cfg != null) AttackSimSystem.StepInstanceFixed(registry, catalog, cfg, Time.fixedDeltaTime);
```

(Remove the in-instance body from `PlayerSimSystem.StepInstanceFixed` or have it early-return for in-instance players; keep its overworld grid step. Simplest: delete `PlayerSimSystem.StepInstanceFixed` and move overworld-only logic stays in `StepAll`.)

- [ ] **Step 3: Pack attack pose into snapshots.** In `SendSnapshots` ([ReplicationHub.cs:97-105](Assets/_Scripts/Net/ReplicationHub.cs)), when building each visible entry, set the attack block from the authoritative state:

```csharp
                var sp = registry.Players[id];
                byte flags = 0;
                if (sp.snap) flags |= SnapshotEntry.SnapBit;
                if (id == viewer && sp.inInstance) flags |= SnapshotEntry.InInstanceBit;
                var entry = new SnapshotEntry { id = id, x = sp.worldPos.x, y = sp.worldPos.y, flags = flags };
                if (sp.inInstance && AttackLogic.IsAttacking(sp.attackState.phase))
                {
                    entry.flags |= SnapshotEntry.AttackingBit;
                    entry.weaponId = sp.weaponId;
                    var st = sp.attackState;
                    entry.pose = AttackPose.Pack(st.phase, st.frameIndex, st.dirIndex);
                    entry.residual = AimQuant.Encode(st.lockedAim);
                    entry.selfExtra = (byte)((st.windupComplete ? 1 : 0) | (Mathf.Clamp((int)(st.cooldown * 20f), 0, 127) << 1));
                }
                entryScratch.Add(entry);
```

- [ ] **Step 4: Flush per-viewer events.** After computing `visibleNow` for a viewer and before/after sending the snapshot, gather pending events for visible attackers and send them. Add a scratch `List<AttackEvent>` field; in the viewer loop:

```csharp
            eventScratch.Clear();
            foreach (var id in visibleNow)
            {
                var sp2 = registry.Players[id];
                if (sp2.pendingEvents == null) continue;
                foreach (var ev in sp2.pendingEvents) eventScratch.Add(ev);
            }
            if (eventScratch.Count > 0)
                AttackEventRpc(eventScratch.ToArray(), new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = oneTarget } });
```

After the viewer loop, clear all queues: `foreach (var sp in registry.Players.Values) sp.pendingEvents?.Clear();` (alongside the existing `sp.snap = false`).

Add the RPC:

```csharp
    [ClientRpc]
    void AttackEventRpc(AttackEvent[] events, ClientRpcParams _ = default)
    {
        if (GhostManager.Instance != null) GhostManager.Instance.ApplyAttackEvents(events, NetworkManager.Singleton.LocalClientId);
    }
```

(`ApplyAttackEvents` is added in Phase 3; for Phase 2 a temporary empty method on `GhostManager` is fine.)

- [ ] **Step 5: Client builds `InputCommand` with raw move.** In `PredictionSystem.FixedTickInstance` (Task 1.4), switch from sending the effective move to sending the **raw** command, and stop relying on the server applying the lunge:

```csharp
        // Phase 2: send RAW move + attack input; the server derives the lunge via InstanceStep.
        var outCmd = new InputCommand
        {
            tick = Tick, rawMove = rawMove, weaponId = weaponId,
            attackBits = (byte)((attack.pressed ? InputCommand.Pressed : 0) | (attack.held ? InputCommand.Held : 0)
                              | (attack.released ? InputCommand.Released : 0) | (attack.feint ? InputCommand.Feint : 0)),
            aimAngle = AimQuant.Encode(attack.aimDir),
        };
        buffer.Store(new InputFrame { tick = Tick, input = rawMove, predictedPos = Pos });  // movement-path reconcile
        commandBuffer.Store(outCmd);   // full command for Phase 4 unified replay (CommandRingBuffer created in Activate)
        ReplicationHub.Instance.SubmitInputTickRpc(outCmd);
```

(Add a `byte weaponId` parameter to `FixedTickInstance`, passed from `LocalPlayer` = `currentWeaponId`. Quantize aim once and use the decoded value for the local `AttackIntent` too — see Step 6.)

- [ ] **Step 6: Determinism — predict from the quantized aim.** In `LocalPlayer`, when building the `AttackIntent` for the fixed step, set `aimDir = AimQuant.Decode(AimQuant.Encode(attackAim))` so local prediction uses the exact value the server receives. Track `currentWeaponId = catalog.IndexOf(currentAttack)` (set on equip/swap), pass it into `FixedTickInstance`. Wire `Game.WeaponCatalog` (serialized field on `Game`, exposed via a `public WeaponCatalog WeaponCatalog => weaponCatalog;`).

- [ ] **Step 7: Verify.** `refresh_unity scope=all` → console empty → `run_tests` EditMode all green → Play boots clean. **Manual host check (documented):** host + one client both in a room; the host's console logs `[attack] STRIKE ...` when the client attacks (server authoritative); movement+lunge still feel right on the owner.

- [ ] **Step 8: Commit.**

```bash
git add Assets/_Scripts/Net/ReplicationHub.cs Assets/_Scripts/Net/LocalPlayer.cs Assets/_Scripts/Net/PredictionSystem.cs Assets/_Scripts/Net/PlayerSimSystem.cs Assets/_Scripts/Game.cs
git commit -m "Net: owner sends InputCommand (raw); server derives lunge + emits attack pose/events"
```

---

# Phase 3 — Remote rendering

End state: a second player in the same room sees your full swing (correct weapon, direction, diagonal aim).

## Task 3.1: `GhostAttack` + drive remote `AttackView`

**Files:**
- Create: `Assets/_Scripts/Net/GhostAttack.cs` (or fields on the ghost)
- Modify: `Assets/_Scripts/Net/GhostManager.cs`

- [ ] **Step 1: Create `GhostAttack.cs`** — a tiny holder that reconstructs an `AttackState` for `AttackView`:

```csharp
using UnityEngine;

// Per-ghost replicated attack pose, fed by GhostManager from snapshots/events and rendered by the existing
// AttackView each frame. Remotes don't simulate; they render the authoritative pose.
public sealed class GhostAttack : MonoBehaviour
{
    [SerializeField] AttackView view;
    WeaponCatalog catalog;
    AttackState pose;
    AttackDefinition def;
    bool attacking;

    void Awake() { if (view == null) view = GetComponent<AttackView>(); }
    public void SetCatalog(WeaponCatalog c) => catalog = c;

    public void Apply(bool isAttacking, byte weaponId, byte poseByte, ushort residual)
    {
        attacking = isAttacking;
        if (!attacking) return;
        def = catalog != null ? catalog.Get(weaponId) : null;
        AttackPose.Unpack(poseByte, out var phase, out var frame, out var dir);
        pose.phase = phase; pose.frameIndex = frame; pose.dirIndex = dir;
        pose.residualDeg = Vector2.SignedAngle(def != null && def.directions != null && dir < def.directions.Length ? def.directions[dir].canonicalDir : Vector2.right, AimQuant.Decode(residual));
    }

    void LateUpdate()
    {
        if (view == null) return;
        view.Render(attacking ? pose : default, attacking ? def : null);
    }
}
```

- [ ] **Step 2: `GhostManager` applies the pose.** In `Apply` ([GhostManager.cs:38-60](Assets/_Scripts/Net/GhostManager.cs)), for non-self ghosts read the attack block and forward it; ensure each ghost has a `GhostAttack` (it's on `Ghost.prefab`) and set its catalog once. For self, leave `LocalPlayer` in charge (skip). Pseudocode inside the entry loop:

```csharp
            if (e.id != localId && ghosts.TryGetValue(e.id, out var gg) && gg.attack != null)
            {
                bool atk = (e.flags & SnapshotEntry.AttackingBit) != 0;
                gg.attack.Apply(atk, e.weaponId, e.pose, e.residual);
            }
```

(Add an `attack` field to the `Ghost` class caching `GetComponent<GhostAttack>()`; set `SetCatalog(Game.Instance.WeaponCatalog)` on spawn.)

- [ ] **Step 3: `ApplyAttackEvents`** — implement the real method (replaces the Phase 2 stub): route `Struck`/`Started`/`Feinted` to the attacker's `GhostAttack` for one-shot beats (v1: snap pose / log; SFX/VFX later).

```csharp
    public void ApplyAttackEvents(AttackEvent[] events, ulong localId)
    {
        for (int i = 0; i < events.Length; i++)
        {
            var ev = events[i];
            if (ev.attackerId == localId) continue;   // self handled by prediction
            // v1: events are a crispness/seam hook; pose comes from the snapshot. Hook SFX/VFX here later.
        }
    }
```

- [ ] **Step 4: `Ghost.prefab` wiring** (manual/MCP): add the `GhostAttack` component to `Ghost.prefab` and wire its `view` to the existing `AttackView`. Confirm `AttackView.Render` works when driven externally for a non-self ghost (it already only reads state + def).

- [ ] **Step 5: Verify.** `refresh_unity scope=all` → console empty → Play boots. **Manual host check (documented):** two clients in one room — each sees the other's windup→strike→follow with the correct weapon and diagonal aim; an idle player shows no rig; two players in different rooms see nothing of each other's attacks; overworld shows no attack.

- [ ] **Step 6: Commit** (code only; `Ghost.prefab` is shared — commit prefab separately/with care, not bundled with WIP):

```bash
git add Assets/_Scripts/Net/GhostAttack.cs Assets/_Scripts/Net/GhostManager.cs
git commit -m "Net: render remote attacks on ghosts from replicated pose + events"
```

---

# Phase 4 — Owner prediction / reconciliation / rollback

End state: the owner's own attack is predicted immediately, reconciled against the authoritative self-state, and rolled back+replayed on divergence (scaffolding that activates once the server rejects actions).

## Task 4.1: `AttackPrediction` ring + reconcile (unit-tested rollback)

**Files:**
- Create: `Assets/_Scripts/Net/AttackPrediction.cs`
- Test: place a pure rollback test in `Minifantasy.InstanceSim.Tests` (it can drive `InstanceStep` directly). `AttackPrediction` itself is net-coupled; the **convergence property** is what we unit-test, via `InstanceStep`.

- [ ] **Step 1: Failing rollback-convergence test** (in `InstanceStepTests.cs`): replaying the same commands from an authoritative state reproduces the authoritative trajectory; injecting a wrong start and then snapping+replaying converges.

```csharp
    [Test]
    public void Replay_FromAuthoritative_Converges()
    {
        var tl = Tl();
        var ctx = Ctx(tl);
        var cmds = new System.Collections.Generic.List<InstanceInput>();
        // server-authoritative run
        var sAtk = default(AttackState); Vector2 sPos = Vector2.zero;
        for (int i = 0; i < 20; i++)
        {
            var c = new InstanceInput { rawMove = new Vector2(1, 0), attack = new AttackIntent { aimDir = new Vector2(1, 0) } };
            cmds.Add(c);
            InstanceStep.Step(ref sAtk, ref sPos, c, ctx);
        }
        // client mispredicted tick 0 (e.g. attacked), then snaps to authoritative at tick 0 and replays 1..19
        var cAtk = default(AttackState); Vector2 cPos = Vector2.zero; // pretend corrected to authoritative-at-0 = default/zero
        for (int i = 1; i < 20; i++) InstanceStep.Step(ref cAtk, ref cPos, cmds[i], ctx);
        // re-derive authoritative-at-0 then replay 1..19 the same way:
        var aAtk = default(AttackState); Vector2 aPos = Vector2.zero; InstanceStep.Step(ref aAtk, ref aPos, cmds[0], ctx);
        for (int i = 1; i < 20; i++) InstanceStep.Step(ref aAtk, ref aPos, cmds[i], ctx);
        Assert.AreEqual(sPos, aPos);
        Assert.AreEqual(sAtk.phase, aAtk.phase);
    }
```

- [ ] **Step 2: Verify FAIL→PASS** (`Minifantasy.InstanceSim.Tests`).

- [ ] **Step 3: Create `AttackPrediction.cs`** — a per-tick predicted-state ring + reconcile that snaps the owner's attack state to authoritative and replays buffered commands via `InstanceStep`:

```csharp
using UnityEngine;

// Owner-side attack prediction: buffers the predicted AttackState per tick and, on each snapshot, snaps to the
// authoritative self-state at ackTick and replays buffered commands through InstanceStep when they diverge.
// Shares the tick counter + raw-command buffer ownership with PredictionSystem (Net references both pure cores).
public sealed class AttackPrediction
{
    readonly AttackState[] predicted;   // by tick & mask
    readonly uint mask;
    public AttackPrediction(int capacity)
    {
        capacity = Mathf.Max(8, Mathf.NextPowerOfTwo(capacity));
        predicted = new AttackState[capacity];
        mask = (uint)(capacity - 1);
    }
    public void Store(uint tick, AttackState s) => predicted[tick & mask] = s;
    public AttackState Get(uint tick) => predicted[tick & mask];

    // Returns true if a correction was applied (caller replays movement+attack together via InstanceStep).
    public bool Diverged(uint ackTick, in AttackState authoritative)
    {
        var p = predicted[ackTick & mask];
        return p.phase != authoritative.phase || p.frameIndex != authoritative.frameIndex || p.dirIndex != authoritative.dirIndex;
    }
}
```

- [ ] **Step 4: Commit.**

```bash
git add Assets/_Scripts/Net/AttackPrediction.cs Assets/Tests/EditMode/InstanceSim/InstanceStepTests.cs
git commit -m "Net: AttackPrediction ring + rollback-convergence test"
```

## Task 4.2: Reconcile the owner attack on snapshot (unified replay)

**Files:**
- Modify: `Assets/_Scripts/Net/PredictionSystem.cs` (unify the replay to re-run `InstanceStep`)
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs` (`OnSnapshot` reads the authoritative self attack block; drives reconcile)
- Modify: `Assets/_Scripts/Net/SnapshotEntry` consumer path

- [ ] **Step 1: Carry authoritative self-state to the owner.** In `LocalPlayer.OnSnapshot` ([LocalPlayer.cs:163](Assets/_Scripts/Net/LocalPlayer.cs)), when the self entry has `AttackingBit`/`InInstanceBit`, decode `weaponId/pose/selfExtra` into an authoritative `AttackState` (phase/frame/dir from `AttackPose`, `windupComplete`/`cooldown` from `selfExtra`, `lockedAim` from `residual`) and pass it to reconcile alongside the existing position reconcile.

- [ ] **Step 2: Unified replay in `PredictionSystem.Reconcile`/`ReplayFrom`.** Change `ReplayFrom` to re-run `InstanceStep` using the buffered **raw** commands (Phase 2 stores raw) and the corrected attack state, so the lunge is re-derived each replayed tick:

```csharp
    void ReplayInstanceFrom(Vector2 fromPos, AttackState fromAtk, uint ackTick, AttackTimeline tl, PhaseScales scales)
    {
        var cfg = Game.Instance.MovementCfg;
        Vector2 pos = fromPos; var atk = fromAtk;
        for (uint t = ackTick + 1; t <= Tick; t++)
        {
            if (!commandBuffer.TryGet(t, out var c)) break;   // commandBuffer = the raw InputCommand ring (owner-side)
            var ctx = new InstanceCtx { timeline = tl, scales = scales, dt = Time.fixedDeltaTime, speed = cfg.moveSpeed, walkable = Walkable };
            InstanceStep.Step(ref atk, ref pos, new InstanceInput { rawMove = c.rawMove, attack = ToIntent(c) }, ctx);
        }
        Pos = pos; prevPos = pos;
        // caller writes atk back into AttackSystem
    }
```

(Owner-side `commandBuffer` is a `CommandRingBuffer` filled in `FixedTickInstance`. `ToIntent` mirrors `AttackSimSystem.ToIntent`; factor it into a shared static if convenient — but keep it in Net to avoid asmdef coupling.)

- [ ] **Step 3: Drive reconcile from `LocalPlayer`.** On snapshot, if `attackPrediction.Diverged(ackTick, authoritativeSelf)` OR movement diverged, snap pos+attack to authoritative at `ackTick` and call `ReplayInstanceFrom`; write the resulting `AttackState` back via `attack.SetState`. Movement-only divergence keeps the existing cheap `ReplayFrom`.

- [ ] **Step 4: Verify.** `refresh_unity scope=all` → console empty → `run_tests` EditMode all green → Play boots. **Manual host check (documented):** owner attack stays immediate; with the server authoritative, the owner's attack and position remain consistent after snapshots (no visible snap in the common case); position stays server-authoritative.

- [ ] **Step 5: Commit.**

```bash
git add Assets/_Scripts/Net/PredictionSystem.cs Assets/_Scripts/Net/LocalPlayer.cs
git commit -m "Net: reconcile + rollback the owner attack against authoritative self-state"
```

---

## Self-review checklist (run after the last task)
- [ ] **Spec coverage:** every spec §5–§9 component maps to a task (InputCommand 2.1; InstanceStep/lunge 1.1–1.3; WeaponCatalog 2.2; ServerPlayer 2.3; AttackSimSystem + OnStrike hit seam 2.4; SnapshotEntry block + AttackEvent 2.5; AOI 2.6; hub wiring 2.7; remote render 3.1; predict/reconcile/rollback 4.1–4.2). ✔
- [ ] **No placeholders** in committed code; every TDD task shows test + impl.
- [ ] **Type consistency:** `InputCommand` fields/consts, `AttackEvent` kinds, `AttackPose.Pack/Unpack`, `AimQuant.Encode/Decode`, `InstanceStep.Step`, `LungeVelocity` signatures match across tasks.
- [ ] All EditMode assemblies green; Play boots clean; manual-host items listed for the user to run.

## Manual (host-required) verification summary
MCP can't drive the IMGUI Host button — after Phase 2/3/4 the user runs host + a second client to confirm: server `[attack] STRIKE` logs (2.7); a second player sees the full swing with correct weapon + diagonal aim in the same room, nothing across rooms, nothing in the overworld (3.1); owner attack stays immediate and position stays authoritative (4.2).
