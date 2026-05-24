# Cursor-Aimed Attacks: Commitment & Feint Flow — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hold left-click to wind up a cursor-aimed attack (re-aimable), release to commit a locked-direction strike, right-click to feint into a cooldown — with fully data-driven per-frame timing and a deterministic hit window. Local-visual only (hit is *marked*, no damage).

**Architecture:** A pure, unit-tested combat core (picker + timed-frame state machine) in a new `Minifantasy.Combat` asmdef, mirroring the existing `Minifantasy.Movement` asmdef pattern. Game-coupled glue (`AttackSystem`, `AttackView`, `AttackDefinition`) lives in Assembly-CSharp: `AttackSystem` is owned/ticked by `LocalPlayer` exactly like `PredictionSystem`; `AttackView` renders a 3-layer rig authored on `Ghost.prefab`. Design spec: [2026-05-23-directional-attack-animation-design.md](../specs/2026-05-23-directional-attack-animation-design.md).

**Tech Stack:** Unity 2021.3+ (C#), Unity Test Framework (NUnit, EditMode), Input System, unity-mcp for compile/test/Play verification.

---

## Working notes (read before starting)

- **Repo carries unrelated WIP.** `Assets/_Scripts/Net/LocalPlayer.cs`, `Net/PredictionSystem.cs`, and `Assets/Scenes/SampleScene.unity` are modified (in-progress underworld movement). **Never `git add -A`.** Each task's commit lists exact files. For the tasks that touch `LocalPlayer.cs` or the scene (Tasks 11–12), confirm with Ryan before committing so you don't bundle his WIP.
- **New `Combat/` files are clean** — commit them freely per task.
- **unity-mcp verify chain** (the project's `execute_code` is broken): after writing/editing C#, run `refresh_unity scope=all` → poll the `editor_state` resource until `isCompiling == false` → `read_console` and confirm **no errors**. Use `scope=all` (not `scope=scripts`) when **adding new files** or Unity won't import them before compiling.
- **EditMode tests** run via the unity-mcp `run_tests` tool with `mode: EditMode` (optionally a name filter). The existing `Minifantasy.Movement.Tests` asmdef is the template.
- **Multiplayer/Play verification** can't drive the IMGUI Host button via MCP; use Unity Multiplayer Play Mode or a single local client where a Play session is needed.

## File structure

**New — pure core (`Assets/_Scripts/Combat/Core/`, asmdef `Minifantasy.Combat`)**
- `Minifantasy.Combat.asmdef` — leaf assembly, no game refs (only UnityEngine).
- `AttackTypes.cs` — `TimedFrame`, `DirectionEntry`, `PhaseScales`, `AttackPhase`, `AttackState`, `AttackIntent`.
- `AttackTimeline.cs` — `AttackTimeline` (the 4 phase lists + dirs + feintCooldown).
- `AttackDirections.cs` — `Pick(dirs, aim)` picker.
- `AttackLogic.cs` — `Step` state machine + `CurrentColumn`/`TimeToHit`/`IsAttacking`/`InHitWindow`.

**New — game glue (`Assets/_Scripts/Combat/`, Assembly-CSharp)**
- `AttackDefinition.cs` — ScriptableObject (sheets + columnsPerRow + 4 phase lists + feintCooldown + directions); builds/caches an `AttackTimeline`.
- `AttackSystem.cs` — plain class owned by `LocalPlayer`; ticks `AttackLogic.Step`; logs on hit entry.
- `AttackView.cs` — MonoBehaviour on `Ghost.prefab`; renders the rig from `AttackState`.

**New — editor & tests**
- `Assets/Editor/AttackDefinitionTool.cs` — populate `Sprite[]`/`columnsPerRow` + Cardinal/Diagonal preset + validation.
- `Assets/Tests/EditMode/Combat/Minifantasy.Combat.Tests.asmdef` — references `Minifantasy.Combat`.
- `Assets/Tests/EditMode/Combat/AttackDirectionsTests.cs`, `AttackLogicTests.cs`.

**Modified**
- `Assets/_Scripts/Player/PlayerView.cs` — exclude the `AttackRig` subtree from tinted body renderers; add `SetBodyVisible(bool)`.
- `Assets/_Scripts/Player/PlayerInput.cs` — LMB down/held/up + RMB edges + `cursorWorld`; drop double-click-to-move.
- `Assets/_Scripts/Net/LocalPlayer.cs` — own/tick `AttackSystem`; build `AttackIntent`; drive the self-ghost `AttackView`; serialized `currentAttack`; remove the double-click `SetTargetRpc` path.
- `Assets/_Prefabs/Ghost.prefab` — add `AttackRig` (pivot + `WeaponBack`/`Body`/`WeaponFront` SRs) + `AttackView`.
- `Assets/Scenes/SampleScene.unity` — assign the Slash `AttackDefinition` to `LocalPlayer.currentAttack`.

---

## Task 1: Combat core asmdef + data types

**Files:**
- Create: `Assets/_Scripts/Combat/Core/Minifantasy.Combat.asmdef`
- Create: `Assets/_Scripts/Combat/Core/AttackTypes.cs`
- Create: `Assets/_Scripts/Combat/Core/AttackTimeline.cs`

- [ ] **Step 1: Create the asmdef**

`Assets/_Scripts/Combat/Core/Minifantasy.Combat.asmdef`:
```json
{
    "name": "Minifantasy.Combat",
    "rootNamespace": "",
    "references": [],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 2: Create the data types**

`Assets/_Scripts/Combat/Core/AttackTypes.cs`:
```csharp
using System;
using UnityEngine;

// One timed sprite frame: which column of the sheet, and how long to show it (seconds, base — scaled at runtime).
[Serializable]
public struct TimedFrame
{
    public int column;
    public float duration;
}

// One authored direction: the canonical aim vector and which sheet row holds its frames.
[Serializable]
public struct DirectionEntry
{
    public Vector2 canonicalDir;
    public int row;
}

// Per-phase runtime time multipliers (the future stat hook: slows raise, buffs lower). 1.0 = base speed.
[Serializable]
public struct PhaseScales
{
    public float anticipation;
    public float hit;
    public float followThrough;
    public static PhaseScales One => new PhaseScales { anticipation = 1f, hit = 1f, followThrough = 1f };
}

public enum AttackPhase { Idle, Anticipation, TapWindup, Hit, FollowThrough }

// Pure attack state. Written by AttackLogic.Step; read by AttackView. No scene/SO refs.
[Serializable]
public struct AttackState
{
    public AttackPhase phase;
    public int dirIndex;        // index into AttackTimeline.directions
    public float residualDeg;   // tilt to aim exactly at the cursor
    public int frameIndex;      // index into the current phase's TimedFrame[]
    public float phaseElapsed;  // time accumulated on the current frame
    public bool windupComplete; // anticipation fully played (now holding) → release = full strike
    public float cooldown;      // feint lockout remaining
}

// One frame of player input, already reduced to edges + aim. The future server-replication seam.
public struct AttackIntent
{
    public bool pressed;   // LMB down edge
    public bool held;      // LMB level
    public bool released;  // LMB up edge
    public bool feint;     // RMB down edge
    public Vector2 aimDir; // cursorWorld - selfWorldPos (un-normalized ok)
}
```

`Assets/_Scripts/Combat/Core/AttackTimeline.cs`:
```csharp
using UnityEngine;

// Runtime, render-agnostic view of an attack's timing + directions. Built once from an AttackDefinition.
public sealed class AttackTimeline
{
    public TimedFrame[] anticipation;
    public TimedFrame[] tapAnticipation;
    public TimedFrame[] hit;
    public TimedFrame[] followThrough;
    public DirectionEntry[] directions;
    public Vector2[] dirs;        // cached directions[].canonicalDir for the picker (no per-tick alloc)
    public float feintCooldown;
}
```

- [ ] **Step 3: Compile**

Run unity-mcp `refresh_unity` with `scope=all`; poll `editor_state` until `isCompiling == false`; `read_console`.
Expected: no errors; a new assembly `Minifantasy.Combat` is present.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/Combat/Core/Minifantasy.Combat.asmdef Assets/_Scripts/Combat/Core/Minifantasy.Combat.asmdef.meta Assets/_Scripts/Combat/Core/AttackTypes.cs Assets/_Scripts/Combat/Core/AttackTypes.cs.meta Assets/_Scripts/Combat/Core/AttackTimeline.cs Assets/_Scripts/Combat/Core/AttackTimeline.cs.meta
git commit -m "Add combat core asmdef + attack data types"
```

---

## Task 2: Direction picker (TDD)

**Files:**
- Create: `Assets/_Scripts/Combat/Core/AttackDirections.cs`
- Create: `Assets/Tests/EditMode/Combat/Minifantasy.Combat.Tests.asmdef`
- Create: `Assets/Tests/EditMode/Combat/AttackDirectionsTests.cs`

- [ ] **Step 1: Create the test asmdef + failing tests**

`Assets/Tests/EditMode/Combat/Minifantasy.Combat.Tests.asmdef`:
```json
{
    "name": "Minifantasy.Combat.Tests",
    "rootNamespace": "",
    "references": [
        "Minifantasy.Combat",
        "UnityEngine.TestRunner",
        "UnityEditor.TestRunner"
    ],
    "includePlatforms": [
        "Editor"
    ],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "nunit.framework.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [
        "UNITY_INCLUDE_TESTS"
    ],
    "versionDefines": [],
    "noEngineReferences": false
}
```

`Assets/Tests/EditMode/Combat/AttackDirectionsTests.cs`:
```csharp
using NUnit.Framework;
using UnityEngine;

public class AttackDirectionsTests
{
    static readonly Vector2[] Cardinal = { new Vector2(1, 0), new Vector2(0, 1), new Vector2(-1, 0), new Vector2(0, -1) };
    static readonly Vector2[] Diagonal = { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(-1, -1), new Vector2(1, -1) };

    [Test]
    public void ExactCardinalHit_ZeroResidual()
    {
        var (i, r) = AttackDirections.Pick(Cardinal, new Vector2(0, 1));
        Assert.AreEqual(1, i);
        Assert.AreEqual(0f, r, 1e-3f);
    }

    [Test]
    public void NearCardinal_PicksNearest_SmallResidual()
    {
        var (i, r) = AttackDirections.Pick(Cardinal, new Vector2(0.3f, 1f)); // up-ish, slightly right
        Assert.AreEqual(1, i);                 // North
        Assert.Greater(r, 0f);                 // cursor is CW->CCW positive toward +x? residual rotates North onto aim
        Assert.Less(Mathf.Abs(r), 45f);
    }

    [Test]
    public void DiagonalSet_ExactHit_ZeroResidual()
    {
        var (i, r) = AttackDirections.Pick(Diagonal, new Vector2(1, 1));
        Assert.AreEqual(0, i);
        Assert.AreEqual(0f, r, 1e-3f);
    }

    [Test]
    public void FourSet_MaxResidual_AtMidpoint_IsAbout45()
    {
        // Aim exactly between East(0) and North(1): 45 degrees. Nearest is either; |residual| ~ 45.
        var (i, r) = AttackDirections.Pick(Cardinal, new Vector2(1, 1));
        Assert.IsTrue(i == 0 || i == 1);
        Assert.AreEqual(45f, Mathf.Abs(r), 0.5f);
    }

    [Test]
    public void ZeroAim_ReturnsZero()
    {
        var (i, r) = AttackDirections.Pick(Cardinal, Vector2.zero);
        Assert.AreEqual(0, i);
        Assert.AreEqual(0f, r, 1e-3f);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run unity-mcp `run_tests` (`mode: EditMode`, filter `AttackDirectionsTests`).
Expected: compile error / FAIL — `AttackDirections` does not exist.

- [ ] **Step 3: Implement the picker**

`Assets/_Scripts/Combat/Core/AttackDirections.cs`:
```csharp
using System.Collections.Generic;
using UnityEngine;

// Pure: choose the authored direction nearest the aim, and the residual rotation to point exactly at it.
public static class AttackDirections
{
    public static (int index, float residualDeg) Pick(IReadOnlyList<Vector2> dirs, Vector2 aim)
    {
        if (dirs == null || dirs.Count == 0 || aim.sqrMagnitude < 1e-8f) return (0, 0f);
        Vector2 a = aim.normalized;
        int best = 0;
        float bestDot = float.NegativeInfinity;
        for (int i = 0; i < dirs.Count; i++)
        {
            float d = Vector2.Dot(dirs[i].normalized, a);
            if (d > bestDot) { bestDot = d; best = i; }
        }
        float residual = Vector2.SignedAngle(dirs[best], aim); // CCW-positive; matches Unity Z-euler
        return (best, residual);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run unity-mcp `run_tests` (`mode: EditMode`, filter `AttackDirectionsTests`).
Expected: 5 PASS. (If `NearCardinal`'s residual sign assert fails, the sign convention is mirrored — flip to `Vector2.SignedAngle(aim, dirs[best])` and re-run; keep the picker and the rig rotation consistent.)

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Combat/Core/AttackDirections.cs Assets/_Scripts/Combat/Core/AttackDirections.cs.meta Assets/Tests/EditMode/Combat
git commit -m "Add attack direction picker + tests"
```

---

## Task 3: Attack state machine (TDD)

**Files:**
- Create: `Assets/_Scripts/Combat/Core/AttackLogic.cs`
- Create: `Assets/Tests/EditMode/Combat/AttackLogicTests.cs`

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/EditMode/Combat/AttackLogicTests.cs`:
```csharp
using NUnit.Framework;
using UnityEngine;

public class AttackLogicTests
{
    static AttackTimeline MakeTimeline() => new AttackTimeline
    {
        anticipation    = new[] { new TimedFrame { column = 0, duration = 0.1f }, new TimedFrame { column = 1, duration = 0.1f } },
        tapAnticipation = new[] { new TimedFrame { column = 0, duration = 0.1f } },
        hit             = new[] { new TimedFrame { column = 2, duration = 0.1f } },
        followThrough   = new[] { new TimedFrame { column = 3, duration = 0.1f } },
        directions      = new[] { new DirectionEntry { canonicalDir = new Vector2(1, 0), row = 0 },
                                  new DirectionEntry { canonicalDir = new Vector2(0, 1), row = 1 } },
        dirs            = new[] { new Vector2(1, 0), new Vector2(0, 1) },
        feintCooldown   = 0.5f,
    };

    static AttackIntent Press(Vector2 aim) => new AttackIntent { pressed = true, held = true, aimDir = aim };
    static AttackIntent Hold(Vector2 aim) => new AttackIntent { held = true, aimDir = aim };
    static AttackIntent Release(Vector2 aim) => new AttackIntent { released = true, held = false, aimDir = aim };
    static AttackIntent Feint(Vector2 aim) => new AttackIntent { held = true, feint = true, aimDir = aim };
    static AttackIntent Idle() => new AttackIntent { aimDir = new Vector2(1, 0) };

    [Test]
    public void Press_FromIdle_EntersAnticipation()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(0, 1)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.Anticipation, s.phase);
        Assert.AreEqual(1, s.dirIndex); // North
    }

    [Test]
    public void PressDuringCooldown_StaysIdle()
    {
        var tl = MakeTimeline();
        var s = new AttackState { phase = AttackPhase.Idle, cooldown = 0.3f };
        s = AttackLogic.Step(s, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.Idle, s.phase);
    }

    [Test]
    public void Cooldown_DecrementsInIdle()
    {
        var tl = MakeTimeline();
        var s = new AttackState { phase = AttackPhase.Idle, cooldown = 0.1f };
        s = AttackLogic.Step(s, Idle(), tl, PhaseScales.One, 0.04f);
        Assert.AreEqual(0.06f, s.cooldown, 1e-4f);
    }

    [Test]
    public void Anticipation_HoldsLastFrame_WhenComplete()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        for (int k = 0; k < 30; k++) s = AttackLogic.Step(s, Hold(new Vector2(1, 0)), tl, PhaseScales.One, 0.1f);
        Assert.AreEqual(AttackPhase.Anticipation, s.phase);
        Assert.IsTrue(s.windupComplete);
        Assert.AreEqual(1, AttackLogic.CurrentColumn(s, tl)); // last anticipation column
    }

    [Test]
    public void Anticipation_ReAims()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        Assert.AreEqual(0, s.dirIndex);
        s = AttackLogic.Step(s, Hold(new Vector2(0, 1)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(1, s.dirIndex); // re-aimed to North
    }

    [Test]
    public void Feint_EntersCooldown_AndIdle()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, Feint(new Vector2(1, 0)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.Idle, s.phase);
        Assert.AreEqual(0.5f, s.cooldown, 1e-4f);
    }

    [Test]
    public void ReleaseEarly_GoesToTapWindup()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f); // frame 0, not complete
        s = AttackLogic.Step(s, Release(new Vector2(1, 0)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.TapWindup, s.phase);
        Assert.AreEqual(0, s.frameIndex);
    }

    [Test]
    public void ReleaseAfterComplete_GoesStraightToHit()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        for (int k = 0; k < 30; k++) s = AttackLogic.Step(s, Hold(new Vector2(1, 0)), tl, PhaseScales.One, 0.1f);
        s = AttackLogic.Step(s, Release(new Vector2(1, 0)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.Hit, s.phase);
    }

    [Test]
    public void ButtonUp_NoReleasedEdge_StillCommits()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, new AttackIntent { held = false, aimDir = new Vector2(1, 0) }, tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.TapWindup, s.phase);
    }

    [Test]
    public void FullSequence_Tap_HitThenFollowThroughThenIdle()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, Release(new Vector2(1, 0)), tl, PhaseScales.One, 0f); // -> TapWindup
        bool sawHit = false;
        for (int k = 0; k < 20; k++)
        {
            s = AttackLogic.Step(s, Idle(), tl, PhaseScales.One, 0.1f);
            if (s.phase == AttackPhase.Hit) sawHit = true;
        }
        Assert.IsTrue(sawHit);
        Assert.AreEqual(AttackPhase.Idle, s.phase);
        Assert.AreEqual(0f, s.cooldown, 1e-4f); // completed attack: no cooldown
    }

    [Test]
    public void DirectionLocks_AfterCommit()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, Release(new Vector2(1, 0)), tl, PhaseScales.One, 0f); // commit, dirIndex=0
        s = AttackLogic.Step(s, new AttackIntent { aimDir = new Vector2(0, 1) }, tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(0, s.dirIndex); // did NOT re-aim to North
    }

    [Test]
    public void Scale_SlowsAnticipation()
    {
        var tl = MakeTimeline();
        var fast = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        fast = AttackLogic.Step(fast, Hold(new Vector2(1, 0)), tl, PhaseScales.One, 0.1f);
        Assert.AreEqual(1, fast.frameIndex); // advanced past frame 0 at scale 1

        var slow = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        var s2 = new PhaseScales { anticipation = 2f, hit = 1f, followThrough = 1f };
        slow = AttackLogic.Step(slow, Hold(new Vector2(1, 0)), tl, s2, 0.1f);
        Assert.AreEqual(0, slow.frameIndex); // 0.1s < 0.1*2, still on frame 0
    }

    [Test]
    public void TimeToHit_TapIsSumOfTapWindup_FullIsZero()
    {
        var tl = MakeTimeline();
        Assert.AreEqual(0.1f, AttackLogic.TimeToHit(tl, tapped: true, PhaseScales.One), 1e-4f);
        Assert.AreEqual(0.2f, AttackLogic.TimeToHit(tl, tapped: true, new PhaseScales { anticipation = 2f, hit = 1f, followThrough = 1f }), 1e-4f);
        Assert.AreEqual(0f, AttackLogic.TimeToHit(tl, tapped: false, PhaseScales.One), 1e-4f);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run unity-mcp `run_tests` (`mode: EditMode`, filter `AttackLogicTests`).
Expected: compile error / FAIL — `AttackLogic` does not exist.

- [ ] **Step 3: Implement the state machine**

`Assets/_Scripts/Combat/Core/AttackLogic.cs`:
```csharp
using UnityEngine;

// Pure, deterministic attack state machine over the timed-frame phase lists. No scene/SO access.
public static class AttackLogic
{
    public static bool IsAttacking(AttackPhase p) => p != AttackPhase.Idle;
    public static bool InHitWindow(AttackPhase p) => p == AttackPhase.Hit;

    public static AttackState Step(AttackState s, AttackIntent intent, AttackTimeline tl, PhaseScales scales, float dt)
    {
        switch (s.phase)
        {
            case AttackPhase.Idle:
                if (s.cooldown > 0f) s.cooldown = Mathf.Max(0f, s.cooldown - dt);
                if (intent.pressed && s.cooldown <= 0f && Has(tl.anticipation))
                {
                    s.phase = AttackPhase.Anticipation;
                    s.frameIndex = 0; s.phaseElapsed = 0f; s.windupComplete = false;
                    Aim(ref s, tl, intent.aimDir);
                }
                return s;

            case AttackPhase.Anticipation:
                Aim(ref s, tl, intent.aimDir);                      // re-aimable while holding
                if (intent.feint)
                    return new AttackState { phase = AttackPhase.Idle, cooldown = tl.feintCooldown };
                if (intent.released || !intent.held)                // commit; direction stays locked from here
                {
                    bool tap = !s.windupComplete && Has(tl.tapAnticipation);
                    s.phase = tap ? AttackPhase.TapWindup : AttackPhase.Hit;
                    s.frameIndex = 0; s.phaseElapsed = 0f;
                    return s;
                }
                if (!s.windupComplete && Advance(ref s, tl.anticipation, scales.anticipation, dt))
                {
                    s.windupComplete = true;
                    s.frameIndex = tl.anticipation.Length - 1;       // hold the wound-up frame
                }
                return s;

            case AttackPhase.TapWindup:
                if (Advance(ref s, tl.tapAnticipation, scales.anticipation, dt))
                    Enter(ref s, AttackPhase.Hit);
                return s;

            case AttackPhase.Hit:
                if (Advance(ref s, tl.hit, scales.hit, dt))
                    Enter(ref s, AttackPhase.FollowThrough);
                return s;

            case AttackPhase.FollowThrough:
                if (Advance(ref s, tl.followThrough, scales.followThrough, dt))
                    Enter(ref s, AttackPhase.Idle);
                return s;
        }
        return s;
    }

    public static int CurrentColumn(AttackState s, AttackTimeline tl)
    {
        switch (s.phase)
        {
            case AttackPhase.Anticipation: return Col(tl.anticipation, s.frameIndex);
            case AttackPhase.TapWindup:    return Col(tl.tapAnticipation, s.frameIndex);
            case AttackPhase.Hit:          return Col(tl.hit, s.frameIndex);
            case AttackPhase.FollowThrough:return Col(tl.followThrough, s.frameIndex);
            default:                       return Has(tl.anticipation) ? tl.anticipation[0].column : 0;
        }
    }

    public static float TimeToHit(AttackTimeline tl, bool tapped, PhaseScales scales)
    {
        if (!tapped) return 0f;
        float sum = 0f;
        if (tl.tapAnticipation != null)
            for (int i = 0; i < tl.tapAnticipation.Length; i++) sum += tl.tapAnticipation[i].duration;
        return sum * scales.anticipation;
    }

    static void Enter(ref AttackState s, AttackPhase phase) { s.phase = phase; s.frameIndex = 0; s.phaseElapsed = 0f; }

    static void Aim(ref AttackState s, AttackTimeline tl, Vector2 aim)
    {
        var (idx, residual) = AttackDirections.Pick(tl.dirs, aim);
        s.dirIndex = idx; s.residualDeg = residual;
    }

    // Advance through a timed list by dt*? ; returns true once the cursor runs past the last frame.
    static bool Advance(ref AttackState s, TimedFrame[] list, float scale, float dt)
    {
        if (!Has(list)) return true;
        s.phaseElapsed += dt;
        while (s.frameIndex < list.Length && s.phaseElapsed >= list[s.frameIndex].duration * scale)
        {
            s.phaseElapsed -= list[s.frameIndex].duration * scale;
            s.frameIndex++;
        }
        return s.frameIndex >= list.Length;
    }

    static int Col(TimedFrame[] list, int i) => Has(list) ? list[Mathf.Clamp(i, 0, list.Length - 1)].column : 0;
    static bool Has(TimedFrame[] list) => list != null && list.Length > 0;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run unity-mcp `run_tests` (`mode: EditMode`, filter `AttackLogicTests`).
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Combat/Core/AttackLogic.cs Assets/_Scripts/Combat/Core/AttackLogic.cs.meta Assets/Tests/EditMode/Combat/AttackLogicTests.cs Assets/Tests/EditMode/Combat/AttackLogicTests.cs.meta
git commit -m "Add attack state machine (anticipation/tap/hit/follow-through) + tests"
```

---

## Task 4: AttackDefinition ScriptableObject

**Files:**
- Create: `Assets/_Scripts/Combat/AttackDefinition.cs`

- [ ] **Step 1: Write the ScriptableObject**

`Assets/_Scripts/Combat/AttackDefinition.cs`:
```csharp
using UnityEngine;

// Authoring asset for one attack. Holds the three sliced sheets + columnsPerRow, the four timed-frame phase
// lists, the feint cooldown, and the per-direction vectors. Builds/caches a render-agnostic AttackTimeline.
[CreateAssetMenu(menuName = "Minifantasy/Attack Definition", fileName = "Attack")]
public sealed class AttackDefinition : ScriptableObject
{
    [Header("Sliced sheets (row-major)")]
    public Sprite[] bodyFrames;
    public Sprite[] weaponFrontFrames;
    public Sprite[] weaponBackFrames;
    public int columnsPerRow = 4;

    [Header("Phases — (column, duration seconds)")]
    public TimedFrame[] anticipation;
    public TimedFrame[] tapAnticipation;
    public TimedFrame[] hit;
    public TimedFrame[] followThrough;

    [Header("Rules")]
    public float feintCooldown = 0.5f;
    public DirectionEntry[] directions;
    public string attackId;

    AttackTimeline _timeline;
    public AttackTimeline Timeline => _timeline ??= BuildTimeline();

    public AttackTimeline BuildTimeline()
    {
        int n = directions != null ? directions.Length : 0;
        var dirs = new Vector2[n];
        for (int i = 0; i < n; i++) dirs[i] = directions[i].canonicalDir;
        return new AttackTimeline
        {
            anticipation = anticipation,
            tapAnticipation = tapAnticipation,
            hit = hit,
            followThrough = followThrough,
            directions = directions,
            dirs = dirs,
            feintCooldown = feintCooldown,
        };
    }

    void OnValidate() => _timeline = null; // rebuild after edits in the Inspector
}
```

- [ ] **Step 2: Compile**

`refresh_unity scope=all` → poll `isCompiling==false` → `read_console`.
Expected: no errors. `Minifantasy/Attack Definition` appears under Assets > Create.

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/Combat/AttackDefinition.cs Assets/_Scripts/Combat/AttackDefinition.cs.meta
git commit -m "Add AttackDefinition ScriptableObject + timeline builder"
```

---

## Task 5: AttackDefinition editor tool

**Files:**
- Create: `Assets/Editor/AttackDefinitionTool.cs`

- [ ] **Step 1: Write the editor tool**

`Assets/Editor/AttackDefinitionTool.cs`:
```csharp
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Inspector helpers for AttackDefinition: populate the three Sprite[] (row-major) + columnsPerRow from assigned
// sliced sheets, fill the 4 cardinal/diagonal direction vectors, and validate columns vs the phase lists.
[CustomEditor(typeof(AttackDefinition))]
public sealed class AttackDefinitionTool : Editor
{
    Texture2D bodySheet, frontSheet, backSheet;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var def = (AttackDefinition)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Populate frames from sliced sheets", EditorStyles.boldLabel);
        bodySheet = (Texture2D)EditorGUILayout.ObjectField("Body sheet", bodySheet, typeof(Texture2D), false);
        frontSheet = (Texture2D)EditorGUILayout.ObjectField("Weapon front (_f)", frontSheet, typeof(Texture2D), false);
        backSheet = (Texture2D)EditorGUILayout.ObjectField("Weapon back (_b)", backSheet, typeof(Texture2D), false);

        if (GUILayout.Button("Populate Sprite[] + columnsPerRow"))
        {
            Undo.RecordObject(def, "Populate attack frames");
            def.bodyFrames = LoadSprites(bodySheet);
            def.weaponFrontFrames = LoadSprites(frontSheet);
            def.weaponBackFrames = LoadSprites(backSheet);
            def.columnsPerRow = ColumnsOf(bodySheet);
            EditorUtility.SetDirty(def);
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Fill directions: Cardinal (E,N,W,S)"))
            SetDirs(def, new[] { new Vector2(1, 0), new Vector2(0, 1), new Vector2(-1, 0), new Vector2(0, -1) });
        if (GUILayout.Button("Fill directions: Diagonal (NE,NW,SW,SE)"))
            SetDirs(def, new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(-1, -1), new Vector2(1, -1) });

        EditorGUILayout.Space();
        if (GUILayout.Button("Validate"))
            Validate(def);
    }

    static Sprite[] LoadSprites(Texture2D tex)
    {
        if (tex == null) return new Sprite[0];
        string path = AssetDatabase.GetAssetPath(tex);
        var subs = AssetDatabase.LoadAllAssetsAtPath(path);
        var list = new List<Sprite>();
        foreach (var o in subs) if (o is Sprite s) list.Add(s);
        list.Sort((a, b) => SliceIndex(a.name).CompareTo(SliceIndex(b.name))); // SpriteGridSliceTool names "{base}_{idx}"
        return list.ToArray();
    }

    static int SliceIndex(string name)
    {
        int u = name.LastIndexOf('_');
        return (u >= 0 && int.TryParse(name.Substring(u + 1), out int n)) ? n : 0;
    }

    static int ColumnsOf(Texture2D tex) => tex == null ? 4 : Mathf.Max(1, tex.width / 32);

    static void SetDirs(AttackDefinition def, Vector2[] dirs)
    {
        Undo.RecordObject(def, "Fill directions");
        def.directions = new DirectionEntry[dirs.Length];
        for (int i = 0; i < dirs.Length; i++) def.directions[i] = new DirectionEntry { canonicalDir = dirs[i], row = i };
        EditorUtility.SetDirty(def);
        Debug.Log("[AttackDef] filled directions; confirm each entry's row matches the sheet.");
    }

    static void Validate(AttackDefinition def)
    {
        int rows = def.directions != null ? def.directions.Length : 0;
        int expected = def.columnsPerRow * rows;
        Check(def.bodyFrames != null && def.bodyFrames.Length == expected, $"bodyFrames len {def.bodyFrames?.Length} != {expected}");
        Check(def.weaponFrontFrames == null || def.weaponFrontFrames.Length == 0 || def.weaponFrontFrames.Length == expected, "weaponFrontFrames length mismatch");
        Check(def.weaponBackFrames == null || def.weaponBackFrames.Length == 0 || def.weaponBackFrames.Length == expected, "weaponBackFrames length mismatch");
        ValidateCols(def.anticipation, def.columnsPerRow, "anticipation");
        ValidateCols(def.tapAnticipation, def.columnsPerRow, "tapAnticipation");
        ValidateCols(def.hit, def.columnsPerRow, "hit");
        ValidateCols(def.followThrough, def.columnsPerRow, "followThrough");
        Debug.Log("[AttackDef] validation complete.");
    }

    static void ValidateCols(TimedFrame[] list, int cols, string name)
    {
        if (list == null) return;
        for (int i = 0; i < list.Length; i++)
            Check(list[i].column >= 0 && list[i].column < cols, $"{name}[{i}].column {list[i].column} out of range 0..{cols - 1}");
    }

    static void Check(bool ok, string msg) { if (!ok) Debug.LogError("[AttackDef] " + msg); }
}
```

- [ ] **Step 2: Compile**

`refresh_unity scope=all` → poll `isCompiling==false` → `read_console`.
Expected: no errors.

- [ ] **Step 3: Commit**

```bash
git add Assets/Editor/AttackDefinitionTool.cs Assets/Editor/AttackDefinitionTool.cs.meta
git commit -m "Add AttackDefinition editor tool (populate frames + presets + validate)"
```

---

## Task 6: Slice the Slash sheets + author the Slash asset

**Files:**
- Modify (import settings): the three Slash textures under `Assets/_Imported/Minifantasy_Weapons_v3.0/Minifantasy_Weapons_Assets/Slash_Attacks/`
- Create: `Assets/_Combat/Slash.asset`

- [ ] **Step 1: Slice the three Slash sheets to 32×32**

In the Project window select these three textures, then run **Tools > Sprites > Slice Selection 32x32**:
- `Slash_Attacks/_Characters/Slash_Character_human.png`
- `Slash_Attacks/Sword/Slash_sword_f.png`
- `Slash_Attacks/Sword/Slash_sword_b.png`

Verify in the console: `[GridSlice] Sliced 3/3 sheet(s), 48 frame(s)` (each is 128×128 = 4×4 = 16 frames).

- [ ] **Step 2: Create the Slash AttackDefinition**

Create folder `Assets/_Combat/`. Right-click > Create > **Minifantasy/Attack Definition**, name it `Slash`. In its Inspector tool:
- Assign Body sheet = `Slash_Character_human`, front = `Slash_sword_f`, back = `Slash_sword_b`; click **Populate Sprite[] + columnsPerRow** (sets `columnsPerRow = 4`, 16 sprites each).
- Click **Fill directions: Diagonal**. Then confirm each entry's `row` matches the sheet rows (open the sliced sheet; set `row` so `canonicalDir` lines up with that row's drawn facing — e.g. row showing the up-right swing → NE `(1,1)`).

- [ ] **Step 3: Author the phase lists (Slash = 4 columns: 0,1,2,3)**

Set on the `Slash` asset (starting point — tune later):
- `anticipation`: `[{column:0, duration:0.1}, {column:1, duration:0.1}]`
- `tapAnticipation`: `[{column:0, duration:0.08}]`
- `hit`: `[{column:2, duration:0.1}]`
- `followThrough`: `[{column:3, duration:0.12}]`
- `feintCooldown`: `0.5`, `attackId`: `slash`

Click **Validate**; the console must show no `[AttackDef]` errors.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Combat Assets/_Imported/Minifantasy_Weapons_v3.0/Minifantasy_Weapons_Assets/Slash_Attacks
git commit -m "Slice Slash sheets and author the Slash attack definition"
```

---

## Task 7: PlayerView — exclude the rig + body visibility toggle

**Files:**
- Modify: `Assets/_Scripts/Player/PlayerView.cs`

- [ ] **Step 1: Exclude the AttackRig subtree from cached body renderers**

In `Assets/_Scripts/Player/PlayerView.cs`, replace the body-caching loop in `Awake`:
```csharp
        var all = GetComponentsInChildren<SpriteRenderer>(true);
        var body = new System.Collections.Generic.List<SpriteRenderer>(all.Length);
        foreach (var sr in all)
            if (sr.gameObject.name != "Shadow" && !IsUnderAttackRig(sr.transform)) body.Add(sr);
        bodyRenderers = body.ToArray();
```

- [ ] **Step 2: Add the helper + the visibility toggle**

Add these methods to `PlayerView`:
```csharp
    static bool IsUnderAttackRig(Transform t)
    {
        for (var p = t; p != null; p = p.parent) if (p.name == "AttackRig") return true;
        return false;
    }

    // Lets AttackView hide the idle/walk body during an attack (the Animator keeps running underneath).
    public void SetBodyVisible(bool visible)
    {
        if (bodyRenderers == null) return;
        for (int i = 0; i < bodyRenderers.Length; i++)
            if (bodyRenderers[i] != null) bodyRenderers[i].enabled = visible;
    }
```

- [ ] **Step 3: Compile**

`refresh_unity scope=all` → poll `isCompiling==false` → `read_console`.
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/Player/PlayerView.cs
git commit -m "PlayerView: exclude AttackRig from body renderers; add SetBodyVisible"
```

---

## Task 8: AttackView component

**Files:**
- Create: `Assets/_Scripts/Combat/AttackView.cs`

- [ ] **Step 1: Write AttackView**

`Assets/_Scripts/Combat/AttackView.cs`:
```csharp
using UnityEngine;

// Visual for one player's attack. Driven externally (LocalPlayer calls Render for the self-ghost only in v1).
// Swaps the idle/walk body for the 3-layer attack rig, drives the frames, and rotates the pivot by the residual.
public sealed class AttackView : MonoBehaviour
{
    [SerializeField] PlayerView playerView;     // toggles the normal body
    [SerializeField] Transform pivot;           // AttackRig root (rotated by residual)
    [SerializeField] SpriteRenderer weaponBack;
    [SerializeField] SpriteRenderer body;
    [SerializeField] SpriteRenderer weaponFront;

    void Awake()
    {
        if (playerView == null) playerView = GetComponent<PlayerView>();
        SetRigActive(false);
    }

    public void Render(AttackState state, AttackDefinition def)
    {
        if (def == null || !AttackLogic.IsAttacking(state.phase))
        {
            SetRigActive(false);
            if (playerView != null) playerView.SetBodyVisible(true);
            return;
        }

        var tl = def.Timeline;
        int row = tl.directions[state.dirIndex].row;
        int i = row * def.columnsPerRow + AttackLogic.CurrentColumn(state, tl);

        if (playerView != null) playerView.SetBodyVisible(false);
        SetRigActive(true);
        SetFrame(body, def.bodyFrames, i);
        SetFrame(weaponFront, def.weaponFrontFrames, i);
        SetFrame(weaponBack, def.weaponBackFrames, i);
        if (pivot != null) pivot.localEulerAngles = new Vector3(0f, 0f, state.residualDeg);
        Tint();
    }

    void SetFrame(SpriteRenderer sr, Sprite[] frames, int i)
    {
        if (sr == null) return;
        if (frames != null && i >= 0 && i < frames.Length && frames[i] != null) { sr.sprite = frames[i]; sr.enabled = true; }
        else sr.enabled = false;
    }

    void SetRigActive(bool on)
    {
        if (pivot != null && pivot.gameObject.activeSelf != on) pivot.gameObject.SetActive(on);
    }

    void Tint()
    {
        var dn = Game.Instance != null ? Game.Instance.DayNight : null;
        if (dn == null) return;
        if (weaponBack != null) weaponBack.color = dn.tint;
        if (body != null) body.color = dn.tint;
        if (weaponFront != null) weaponFront.color = dn.tint;
    }
}
```

- [ ] **Step 2: Compile**

`refresh_unity scope=all` → poll `isCompiling==false` → `read_console`.
Expected: no errors. (Confirm `Game.Instance.DayNight.tint` matches [PlayerView.cs:53](Assets/_Scripts/Player/PlayerView.cs); it does.)

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/Combat/AttackView.cs Assets/_Scripts/Combat/AttackView.cs.meta
git commit -m "Add AttackView (3-layer rig render + pivot rotation)"
```

---

## Task 9: Add the AttackRig + AttackView to Ghost.prefab

**Files:**
- Modify: `Assets/_Prefabs/Ghost.prefab`

`Ghost.prefab` hierarchy today: root `Ghost` (Animator + PlayerView) → children `Shadow`, `Body` (SortingLayer 2, order 0), `Weapon` (order 10, empty), `Effects`.

- [ ] **Step 1: Build the rig**

Open `Assets/_Prefabs/Ghost.prefab` in Prefab Mode. Under the root `Ghost`, create an empty child named exactly **`AttackRig`** at local position (0,0,0). Under `AttackRig`, add three children, each with a `SpriteRenderer` (same Material as `Body` — sprite-lit/default `fba28ea...`, SortingLayer `2`):
- `WeaponBack` — sortingOrder **-1**
- `Body` — sortingOrder **0**
- `WeaponFront` — sortingOrder **1**

Leave their sprites empty. Set `AttackRig` **inactive** (unchecked) by default.

- [ ] **Step 2: Add + wire AttackView**

On the root `Ghost`, Add Component **AttackView**. Wire: `playerView` = the Ghost's PlayerView; `pivot` = `AttackRig`; `weaponBack`/`body`/`weaponFront` = the three rig SRs.

- [ ] **Step 3: Verify the static render in Play**

Temporarily, in `AttackView.Awake`, add `SetRigActive(true);` and in `Update()` set a fixed frame:
```csharp
    void Update() // TEMP verification only — delete after this task
    {
        if (body != null && _def != null) { /* assign _def in inspector temporarily */ }
    }
```
Simpler: enter Play, select a spawned Ghost, tick `AttackRig` active, and manually assign a Slash body/front sprite to the rig SRs in the Inspector. Confirm via an MCP screenshot that WeaponBack renders behind Body and WeaponFront in front, all tinted by day/night, and an empty `_b` frame is invisible (harmless). Revert any temp edits.

- [ ] **Step 4: Commit**

> WIP caveat: `Ghost.prefab` is otherwise clean; this is safe to commit alone.
```bash
git add Assets/_Prefabs/Ghost.prefab
git commit -m "Ghost.prefab: add 3-layer AttackRig + AttackView"
```

---

## Task 10: AttackSystem

**Files:**
- Create: `Assets/_Scripts/Combat/AttackSystem.cs`

- [ ] **Step 1: Write AttackSystem**

`Assets/_Scripts/Combat/AttackSystem.cs`:
```csharp
using UnityEngine;

// Owner-side attack logic, owned + ticked by LocalPlayer (mirrors PredictionSystem). Holds the AttackState and
// steps the pure AttackLogic each frame; emits a one-shot log when the hit window opens (future: damage).
public sealed class AttackSystem
{
    AttackState state;
    AttackPhase prevPhase;

    public AttackState State => state;
    public PhaseScales Scales = PhaseScales.One;  // future: stats write this

    public void Tick(float dt, AttackIntent intent, AttackDefinition def)
    {
        if (def == null) return;
        prevPhase = state.phase;
        state = AttackLogic.Step(state, intent, def.Timeline, Scales, dt);
        if (prevPhase != AttackPhase.Hit && state.phase == AttackPhase.Hit)
            Debug.Log($"[attack] hit window open ({def.attackId})"); // verification hook; replace with damage later
    }
}
```

- [ ] **Step 2: Compile**

`refresh_unity scope=all` → poll `isCompiling==false` → `read_console`.
Expected: no errors.

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/Combat/AttackSystem.cs Assets/_Scripts/Combat/AttackSystem.cs.meta
git commit -m "Add AttackSystem (ticks the pure state machine, logs hit entry)"
```

---

## Task 11: PlayerInput — attack edges + cursor; drop click-to-move

**Files:**
- Modify: `Assets/_Scripts/Player/PlayerInput.cs`

- [ ] **Step 1: Replace the Intent struct + Read**

Replace the whole body of `Assets/_Scripts/Player/PlayerInput.cs` with:
```csharp
using UnityEngine;
using UnityEngine.InputSystem;

// Logic helper: reads the owning client's devices into a per-frame Intent (movement + attack edges + cursor).
// No state. LocalPlayer turns this into movement input and an AttackIntent.
public sealed class PlayerInput
{
    public struct Intent
    {
        public Vector2 dir;        // raw 8-way WASD (each axis -1/0/1); zero while typing
        public bool lmbDown;       // attack press edge
        public bool lmbHeld;       // attack held
        public bool lmbUp;         // attack release edge
        public bool rmbDown;       // feint edge
        public Vector2 cursorWorld;// mouse position in world space
    }

    public Intent Read(Camera cam)
    {
        var result = new Intent();
        if (InputState.Typing) return result;          // command line open: no input

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed) result.dir.y += 1f;
            if (kb.sKey.isPressed) result.dir.y -= 1f;
            if (kb.aKey.isPressed) result.dir.x -= 1f;
            if (kb.dKey.isPressed) result.dir.x += 1f;
        }

        var mouse = Mouse.current;
        if (mouse != null)
        {
            result.lmbDown = mouse.leftButton.wasPressedThisFrame;
            result.lmbHeld = mouse.leftButton.isPressed;
            result.lmbUp = mouse.leftButton.wasReleasedThisFrame;
            result.rmbDown = mouse.rightButton.wasPressedThisFrame;
            if (cam != null)
            {
                Vector2 sp = mouse.position.ReadValue();
                Vector3 wp = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, Mathf.Abs(cam.transform.position.z)));
                result.cursorWorld = new Vector2(wp.x, wp.y);
            }
        }
        return result;
    }
}
```

- [ ] **Step 2: Compile (expect a known break in LocalPlayer)**

`refresh_unity scope=all` → `read_console`.
Expected: errors in `LocalPlayer.cs` referencing `intent.hasClickTarget`/`intent.clickWorld` (removed). These are fixed in Task 12. Do not commit yet.

- [ ] **Step 3: Commit (after Task 12 compiles clean)**

Deferred — commit `PlayerInput.cs` together with `LocalPlayer.cs` in Task 12 so the tree never has a broken intermediate commit.

---

## Task 12: LocalPlayer — own AttackSystem, wire input + view, drop click-to-move

**Files:**
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs`
- Modify: `Assets/Scenes/SampleScene.unity` (assign `currentAttack`)

> **WIP caveat:** `LocalPlayer.cs` and `SampleScene.unity` carry Ryan's in-progress underworld work. Confirm with Ryan before the commit so you don't bundle that WIP.

- [ ] **Step 1: Add fields + AttackView resolution**

In `LocalPlayer`, add near the other fields:
```csharp
    [SerializeField] AttackDefinition currentAttack;
    readonly AttackSystem attack = new();
    Transform attackViewGhost;
    AttackView attackView;
```

- [ ] **Step 2: Drive the attack in Update**

In `LocalPlayer.Update`, replace the click-to-move line:
```csharp
        if (intent.hasClickTarget) ReplicationHub.Instance.SetTargetRpc(intent.clickWorld);
```
with the attack drive:
```csharp
        if (SelfWorldPos.HasValue)
        {
            var ai = new AttackIntent
            {
                pressed = intent.lmbDown, held = intent.lmbHeld, released = intent.lmbUp,
                feint = intent.rmbDown, aimDir = intent.cursorWorld - SelfWorldPos.Value,
            };
            attack.Tick(Time.deltaTime, ai, currentAttack);
            ResolveAttackView();
            if (attackView != null) attackView.Render(attack.State, currentAttack);
        }
```

- [ ] **Step 3: Add the view resolver**

Add to `LocalPlayer`:
```csharp
    void ResolveAttackView()
    {
        var ghost = GhostManager.Instance != null ? GhostManager.Instance.SelfGhost : null;
        if (ghost == attackViewGhost) return;          // re-resolve only when the self-ghost changes
        attackViewGhost = ghost;
        attackView = ghost != null ? ghost.GetComponent<AttackView>() : null;
    }
```

- [ ] **Step 4: Compile**

`refresh_unity scope=all` → poll `isCompiling==false` → `read_console`.
Expected: no errors (the `intent.dir` movement path is unchanged; the removed `hasClickTarget` references are gone).

- [ ] **Step 5: Assign the attack asset**

Open `SampleScene`, select the `LocalPlayer` object, and set **Current Attack** = `Assets/_Combat/Slash.asset`. Save the scene.

- [ ] **Step 6: Commit (confirm with Ryan first)**

```bash
git add Assets/_Scripts/Player/PlayerInput.cs Assets/_Scripts/Net/LocalPlayer.cs Assets/Scenes/SampleScene.unity
git commit -m "Wire attacks: LMB hold/release + RMB feint via LocalPlayer; drop click-to-move"
```

---

## Task 13: Full-flow verification in Play

**Files:** none (verification only)

- [ ] **Step 1: Enter Play as a client/host**

Per the unity-mcp flow: `refresh_unity scope=all` → `isCompiling==false` → `read_console` clean → enter Play (Multiplayer Play Mode or a single local client so a self-ghost exists).

- [ ] **Step 2: Exercise the flow (screenshot each)**

- **Hold LMB** and move the cursor: the character winds up (anticipation columns 0→1, then holds 1) and the rig re-aims (row + tilt follow the cursor). No hit log yet.
- **Release after a full hold**: direction locks; the strike plays (column 2) — console shows `[attack] hit window open (slash)` once — then follow-through (column 3); returns to idle/walk.
- **Tap** LMB: the tap wind-up (column 0 @0.08s) plays, then the hit; total pre-hit time ≈ `TimeToHit(tap)` = 0.08s.
- **Hold then RMB**: snaps to idle; LMB does nothing for ~0.5s (`feintCooldown`), then attacks work again.
- **WASD** still moves the player throughout; **double-click no longer path-moves**.

- [ ] **Step 3: Confirm no regressions**

`read_console` is clean (no errors/exceptions) across the whole session; movement/prediction and day/night tint on the attack rig still look correct.

- [ ] **Step 4: Decide whole-rig vs weapon-only tilt**

If the up-to-45° lean of the whole attacking body looks wrong, switch `AttackView` to rotate only the weapon layers: parent `WeaponBack`/`WeaponFront` under a nested pivot and apply `residualDeg` there instead of on `AttackRig`. Re-verify. (No data/logic change.)

---

## Self-review (completed)

- **Spec coverage:** picker (T2), per-frame/per-phase timed data + `TimeToHit` (T1, T3, T4), tap-vs-full + feint + cooldown state machine (T3), `PhaseScales` stat hook (T1, T10), `AttackDefinition` + editor tool + Slash asset (T4–T6), 3-layer rig + `AttackView` (T7–T9), input edges + drop click-to-move (T11), `LocalPlayer` wiring + local-visual drive (T12), verification incl. tilt decision (T13). Hit = mark-only via `Debug.Log` (T10). All §1–§12 requirements map to a task.
- **Type consistency:** `AttackState`, `AttackIntent`, `AttackTimeline`, `TimedFrame`, `DirectionEntry`, `PhaseScales`, `AttackPhase` defined in T1 and used identically in T3/T4/T8/T10/T12; `AttackLogic.Step/CurrentColumn/TimeToHit/IsAttacking` signatures match across tests and callers; `AttackDefinition.Timeline`/`columnsPerRow`/frame arrays consistent T4↔T8.
- **No placeholders:** every code step has complete code; Unity-only tasks (slicing, prefab, scene) give exact menu paths, names, sorting orders, and the verify chain.
