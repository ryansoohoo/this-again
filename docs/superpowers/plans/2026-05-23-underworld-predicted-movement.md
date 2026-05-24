# Underworld Predicted Movement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add client-side prediction + server reconciliation for free analog movement in the underworld, on a raised global fixed/network tick, leaving the overworld grid movement untouched.

**Architecture:** A single pure deterministic `MovementStep` drives client prediction, client replay, and the server sim. The owner samples input on a fixed tick, predicts locally, stores `(tick,input,pos)` in a power-of-two ring buffer, and sends tick-stamped input. The server steps in-instance players from buffered inputs on `FixedUpdate`, returns its last-processed tick on the snapshot, and the client reconciles by replaying un-acked inputs and smoothing the correction. Only the local player is predicted; remote players stay interpolated ghosts; the overworld path is unchanged.

**Tech Stack:** Unity 6.3 (6000.3.7f1), Netcode for GameObjects 2.11, C#, Unity Test Framework (NUnit EditMode), Multiplayer Play Mode for host+client testing.

**Design spec:** `docs/superpowers/specs/2026-05-23-underworld-predicted-movement-design.md`

---

## Before you start

- **This repo usually has uncommitted WIP.** Commit or stash existing changes first so each task's commit stays clean. Use targeted `git add <paths>` (never `git add -A`).
- **Commit style:** no Claude/AI attribution in messages (no `Co-Authored-By`, no "Generated with").
- **Verification primitives used below:**
  - *Compile-check* = unity-mcp `refresh` (`scope=all`) → poll `editor_state.isCompiling` until false → `read_console` and confirm no errors. (`execute_code` is broken on this machine — do not use it.)
  - *Run EditMode tests* = Unity Test Runner (Window → General → Test Runner → EditMode → Run), or the unity-mcp `run_tests` tool with `mode: EditMode`.
  - *Play-test* = Multiplayer Play Mode: enable 1 virtual player, click Host in the main editor, Join in the virtual player. (MCP cannot drive the Host button.)

## File structure (decomposition)

**New — pure, in a dedicated testable asmdef `Minifantasy.Movement`:**
- `Assets/_Scripts/Net/Movement/Minifantasy.Movement.asmdef`
- `Assets/_Scripts/Net/Movement/InputRingBuffer.cs` — `InputFrame` struct + ring buffer.
- `Assets/_Scripts/Net/Movement/MovementStep.cs` — the deterministic kinematic step.
- `Assets/Tests/EditMode/Movement/Minifantasy.Movement.Tests.asmdef`
- `Assets/Tests/EditMode/Movement/InputRingBufferTests.cs`
- `Assets/Tests/EditMode/Movement/MovementStepTests.cs`

**New — Assembly-CSharp:**
- `Assets/_Scripts/Net/MovementSettings.cs` — prediction tunables (`JsonPref` "movement.json").
- `Assets/_Scripts/Net/PredictionSystem.cs` — client predict/store/send/reconcile; owns the self visual in-instance.

**Modified — Assembly-CSharp:**
- `Assets/_Scripts/Game.cs` — `Time.fixedDeltaTime`; own `MovementSettings`; expose `MovementCfg`.
- `Assets/_Scripts/Net/PlayerRegistry.cs` — `ServerPlayer`: server input ring + `lastProcessedTick` + `lastInput`.
- `Assets/_Scripts/Net/ReplicationHub.cs` — `SubmitInputTickRpc`; `ackTick` on snapshot; `FixedUpdate` server free-step.
- `Assets/_Scripts/Net/PlayerSimSystem.cs` — skip in-instance players in the per-frame step; add the fixed free-step.
- `Assets/_Scripts/Net/GhostManager.cs` — don't interpolate the self-ghost while in-instance.
- `Assets/_Scripts/Net/LocalPlayer.cs` — own `PredictionSystem`; `FixedUpdate`; route snapshot reconcile; `SelfWorldPos` from prediction in-instance.
- `Assets/_Scripts/Net/Aoi/ReplicationSettings.cs` — `snapshotHz` default 15 → 30.

**Scene (manual, no code):**
- `Assets/Scenes/SampleScene.unity` — set `NetworkManager.NetworkConfig.TickRate = 60`.

---

# Phase 1 — Pure foundations (TDD) + tick raise

### Task 1: `InputRingBuffer` (pure, TDD)

**Files:**
- Create: `Assets/_Scripts/Net/Movement/Minifantasy.Movement.asmdef`
- Create: `Assets/_Scripts/Net/Movement/InputRingBuffer.cs`
- Create: `Assets/Tests/EditMode/Movement/Minifantasy.Movement.Tests.asmdef`
- Test: `Assets/Tests/EditMode/Movement/InputRingBufferTests.cs`

- [ ] **Step 1: Create the runtime asmdef**

`Assets/_Scripts/Net/Movement/Minifantasy.Movement.asmdef`:
```json
{
    "name": "Minifantasy.Movement",
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

- [ ] **Step 2: Create the test asmdef**

`Assets/Tests/EditMode/Movement/Minifantasy.Movement.Tests.asmdef`:
```json
{
    "name": "Minifantasy.Movement.Tests",
    "rootNamespace": "",
    "references": [
        "Minifantasy.Movement",
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

- [ ] **Step 3: Write the failing tests**

`Assets/Tests/EditMode/Movement/InputRingBufferTests.cs`:
```csharp
using NUnit.Framework;
using UnityEngine;

public class InputRingBufferTests
{
    [Test]
    public void StoreThenGet_ReturnsSameFrame()
    {
        var buf = new InputRingBuffer(8);
        buf.Store(new InputFrame { tick = 3, input = new Vector2(1f, 0f), predictedPos = new Vector2(2f, 2f) });
        Assert.IsTrue(buf.TryGet(3, out var f));
        Assert.AreEqual(3u, f.tick);
        Assert.AreEqual(new Vector2(1f, 0f), f.input);
        Assert.AreEqual(new Vector2(2f, 2f), f.predictedPos);
    }

    [Test]
    public void TryGet_UnseenTick_ReturnsFalse()
    {
        var buf = new InputRingBuffer(8);
        buf.Store(new InputFrame { tick = 3, input = Vector2.one });
        Assert.IsFalse(buf.TryGet(4, out _));
    }

    [Test]
    public void LappedTick_ReturnsFalse()
    {
        var buf = new InputRingBuffer(8);          // capacity 8, mask 7
        buf.Store(new InputFrame { tick = 1, input = Vector2.one });
        buf.Store(new InputFrame { tick = 9, input = Vector2.zero });   // 9 & 7 == 1 -> overwrites slot of tick 1
        Assert.IsFalse(buf.TryGet(1, out _));
        Assert.IsTrue(buf.TryGet(9, out _));
    }

    [Test]
    public void NonPowerOfTwoCapacity_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => new InputRingBuffer(10));
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run EditMode tests. Expected: FAIL (compile error — `InputRingBuffer`/`InputFrame` not defined).

- [ ] **Step 5: Implement `InputRingBuffer`**

`Assets/_Scripts/Net/Movement/InputRingBuffer.cs`:
```csharp
using UnityEngine;

// One sampled input + the position it predicted, keyed by client tick. Plain data.
public struct InputFrame
{
    public uint tick;
    public Vector2 input;
    public Vector2 predictedPos;
}

// Fixed-capacity (power-of-two) ring of InputFrames, indexed by tick & mask. Zero per-frame allocation.
// A slot only "matches" if its stored tick equals the requested tick, so a lapped (overwritten) tick reads
// as absent. Used by the client for predict/replay; the server uses it to buffer received inputs.
public sealed class InputRingBuffer
{
    readonly InputFrame[] buf;
    readonly uint mask;

    public InputRingBuffer(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new System.ArgumentException("capacity must be a power of two >= 2", nameof(capacity));
        buf = new InputFrame[capacity];
        mask = (uint)(capacity - 1);
    }

    public void Store(InputFrame f) => buf[f.tick & mask] = f;

    public bool TryGet(uint tick, out InputFrame f)
    {
        f = buf[tick & mask];
        return f.tick == tick;
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run EditMode tests. Expected: PASS (4 tests). Note: tick `0` is the default slot value, so `TryGet(0,...)` returns true only after an explicit `Store` of tick 0 — callers below never reconcile against tick 0.

- [ ] **Step 7: Commit**

```bash
git add "Assets/_Scripts/Net/Movement/Minifantasy.Movement.asmdef" "Assets/_Scripts/Net/Movement/InputRingBuffer.cs" "Assets/Tests/EditMode/Movement/Minifantasy.Movement.Tests.asmdef" "Assets/Tests/EditMode/Movement/InputRingBufferTests.cs"
git commit -m "Add InputRingBuffer for client-side prediction history"
```

---

### Task 2: `MovementStep` (pure, TDD — the determinism keystone)

**Files:**
- Create: `Assets/_Scripts/Net/Movement/MovementStep.cs`
- Test: `Assets/Tests/EditMode/Movement/MovementStepTests.cs`

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/EditMode/Movement/MovementStepTests.cs`:
```csharp
using System;
using NUnit.Framework;
using UnityEngine;

public class MovementStepTests
{
    static readonly Func<Vector2, bool> Open = _ => true;

    [Test]
    public void MovesAtSpeedAlongInput()
    {
        var p = MovementStep.Step(Vector2.zero, new Vector2(1f, 0f), 0.1f, 4f, Open);
        Assert.AreEqual(0.4f, p.x, 1e-4f);
        Assert.AreEqual(0f, p.y, 1e-4f);
    }

    [Test]
    public void DiagonalInputIsNormalized()
    {
        var p = MovementStep.Step(Vector2.zero, new Vector2(1f, 1f), 0.25f, 4f, Open);
        Assert.AreEqual(1f, p.magnitude, 1e-4f);   // 4 * 0.25 = 1 unit total, not 1.41
    }

    [Test]
    public void ZeroInput_DoesNotMove()
    {
        var p = MovementStep.Step(new Vector2(3f, 3f), Vector2.zero, 0.1f, 4f, Open);
        Assert.AreEqual(new Vector2(3f, 3f), p);
    }

    [Test]
    public void Deterministic_SameInputsSamePath()
    {
        Vector2 a = Vector2.zero, b = Vector2.zero;
        var seq = new[] { new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), new Vector2(-1, 0) };
        foreach (var s in seq) { a = MovementStep.Step(a, s, 1f / 60f, 4f, Open); }
        foreach (var s in seq) { b = MovementStep.Step(b, s, 1f / 60f, 4f, Open); }
        Assert.AreEqual(a, b);
    }

    [Test]
    public void BlockedAhead_SlidesAlongWall()
    {
        // Wall: anything with x >= 1 is blocked. Moving +x is rejected; +y still allowed.
        Func<Vector2, bool> wall = pos => pos.x < 1f;
        var p = MovementStep.Step(new Vector2(0.9f, 0f), new Vector2(1f, 1f), 0.1f, 4f, wall);
        Assert.AreEqual(0.9f, p.x, 1e-4f);     // x move blocked
        Assert.Greater(p.y, 0f);               // y move slid through
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run EditMode tests. Expected: FAIL (compile error — `MovementStep` not defined).

- [ ] **Step 3: Implement `MovementStep`**

`Assets/_Scripts/Net/Movement/MovementStep.cs`:
```csharp
using System;
using UnityEngine;

// The single deterministic kinematic movement step, shared by client prediction, client replay, and the
// server sim. Pure: inputs in, position out. No Time.*, no randomness, no physics — this purity is what makes
// reconciliation's replay match the server. `isWalkableAt(worldPos)` is supplied identically by both sides
// (it wraps Game.WorldToCell + Game.IsWalkable). Collision is per-axis slide so walls don't fully stop you.
public static class MovementStep
{
    public static Vector2 Step(Vector2 pos, Vector2 input, float dt, float speed, Func<Vector2, bool> isWalkableAt)
    {
        Vector2 dir = input.sqrMagnitude > 1f ? input.normalized : input;
        Vector2 delta = dir * (speed * dt);
        if (delta == Vector2.zero) return pos;

        Vector2 next = pos;

        Vector2 tryX = new Vector2(pos.x + delta.x, next.y);
        if (delta.x != 0f && isWalkableAt(tryX)) next.x = tryX.x;

        Vector2 tryY = new Vector2(next.x, pos.y + delta.y);
        if (delta.y != 0f && isWalkableAt(tryY)) next.y = tryY.y;

        return next;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run EditMode tests. Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/Net/Movement/MovementStep.cs" "Assets/Tests/EditMode/Movement/MovementStepTests.cs"
git commit -m "Add deterministic MovementStep for prediction and server sim"
```

---

### Task 3: Raise the global tick

**Files:**
- Modify: `Assets/_Scripts/Game.cs:140-155` (`ConfigureApp`)
- Modify: `Assets/_Scripts/Net/Aoi/ReplicationSettings.cs:12`
- Scene: `Assets/Scenes/SampleScene.unity` (manual)

- [ ] **Step 1: Set the fixed timestep in `ConfigureApp`**

In `Assets/_Scripts/Game.cs`, inside `ConfigureApp()`, add after the `Application.targetFrameRate = 120;` line (currently line 143):
```csharp
        Time.fixedDeltaTime = 1f / 60f;               // 60 Hz sim/prediction tick (was 0.02 = 50 Hz)
```

- [ ] **Step 2: Raise the snapshot rate default**

In `Assets/_Scripts/Net/Aoi/ReplicationSettings.cs`, change line 12:
```csharp
    [Range(1, 30)] public int   snapshotHz = 30;     // server -> client snapshot send rate
```
(If you have a saved `replication.json` from a prior run, clear it via the Replication tuner's reset, or it will keep the old 15.)

- [ ] **Step 3: Set the network tick rate in the scene**

Open `Assets/Scenes/SampleScene.unity`, select the `NetworkManager` object, and set **NetworkConfig → Tick Rate = 60** (was 30). Save the scene.

- [ ] **Step 4: Compile-check + play-check**

Compile-check (refresh `scope=all` → `isCompiling` false → `read_console` clean). Then Play (host only) and confirm normal overworld movement still works and the console is clean.

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/Game.cs" "Assets/_Scripts/Net/Aoi/ReplicationSettings.cs" "Assets/Scenes/SampleScene.unity"
git commit -m "Raise global tick: 60 Hz fixed timestep, 60 Hz net tick, 30 Hz snapshots"
```

---

# Phase 2 — Server-authoritative free movement on the fixed tick

### Task 4: `MovementSettings` + Game wiring

**Files:**
- Create: `Assets/_Scripts/Net/MovementSettings.cs`
- Modify: `Assets/_Scripts/Game.cs` (settings field, JsonPref, accessor, load, save/reset)

- [ ] **Step 1: Create `MovementSettings`**

`Assets/_Scripts/Net/MovementSettings.cs`:
```csharp
using System;
using UnityEngine;

// Tunable knobs for free (underworld) movement + client prediction. Plain data, persisted via JsonPref
// ("movement.json"). moveSpeed is the single source of truth for the free path on BOTH client and server,
// so prediction and the authoritative sim integrate identically.
[Serializable]
public class MovementSettings
{
    [Min(0.1f)] public float moveSpeed = 4f;             // units/sec for free movement (matches the grid moveSpeed)
    [Min(0f)]   public float reconcileEpsilon = 0.05f;   // world-unit error below which no correction is applied
    [Min(0.01f)]public float correctionSmoothTime = 0.1f;// seconds to visually absorb a correction
    [Min(8)]    public int   inputBufferCapacity = 128;  // ring size; rounded up to a power of two at use
}
```

- [ ] **Step 2: Wire it into `Game`**

In `Assets/_Scripts/Game.cs`:

After the Replication header block (line 40), add:
```csharp
    [Header("Movement (free / prediction)")]
    [SerializeField] MovementSettings movement = new MovementSettings();
```

Next to `public ReplicationSettings ReplicationCfg => replication;` (line 62), add:
```csharp
    public MovementSettings MovementCfg => movement;
```

Next to `readonly JsonPref<ReplicationSettings> replicationPref = new("replication.json");` (line 96), add:
```csharp
    readonly JsonPref<MovementSettings> movementPref = new("movement.json");
```

In `Awake`, next to `replicationPref.Load(replication);` (line 106), add:
```csharp
        movementPref.Load(movement);
```

Next to the Replication save/reset methods (lines 178-179), add:
```csharp
    public void SaveMovementSettings()  { movementPref.Save(movement); Debug.Log("[Game] Movement settings saved."); }
    public void ResetMovementSettings() { movementPref.Reset(movement, new MovementSettings()); Debug.Log("[Game] Movement settings reset (defaults applied)."); }
```

- [ ] **Step 3: Compile-check**

Compile-check (refresh `scope=all` → `isCompiling` false → `read_console` clean).

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Net/MovementSettings.cs" "Assets/_Scripts/Game.cs"
git commit -m "Add MovementSettings (free movement + prediction knobs)"
```

---

### Task 5: Server input buffer + tick-stamped input RPC + snapshot ack

**Files:**
- Modify: `Assets/_Scripts/Net/PlayerRegistry.cs:7-16` (`ServerPlayer`)
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs` (`SubmitInputTickRpc`; `ackTick` on snapshot)

- [ ] **Step 1: Add server-side prediction fields to `ServerPlayer`**

In `Assets/_Scripts/Net/PlayerRegistry.cs`, add fields to `ServerPlayer` (after `public bool snap;`, line 15):
```csharp
    public uint lastProcessedTick;                 // highest contiguous client tick the server has simulated
    public Vector2 lastInput;                      // last applied free-move input (for reference/debug)
    public InputRingBuffer serverInputs;           // received tick-stamped inputs (free/in-instance only)
```

- [ ] **Step 2: Add the tick-stamped input RPC**

In `Assets/_Scripts/Net/ReplicationHub.cs`, after `SubmitInputRpc` (ends line 126), add:
```csharp
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void SubmitInputTickRpc(uint tick, Vector2 input, RpcParams p = default)
    {
        if (!registry.TryGet(p.Receive.SenderClientId, out var sp)) return;
        if (tick <= sp.lastProcessedTick) return;                       // already simulated; ignore late dup
        sp.serverInputs ??= new InputRingBuffer(Mathf.Max(8, Mathf.NextPowerOfTwo(
            Game.Instance != null ? Game.Instance.MovementCfg.inputBufferCapacity : 128)));
        sp.serverInputs.Store(new InputFrame { tick = tick, input = input });
    }
```

- [ ] **Step 3: Add `ackTick` to the snapshot RPC**

In `Assets/_Scripts/Net/ReplicationHub.cs`, change the `SnapshotClientRpc` signature (line 115) and the call site (line 108).

Signature:
```csharp
    [ClientRpc]
    void SnapshotClientRpc(SnapshotEntry[] entries, uint ackTick, ClientRpcParams _ = default)
    {
        if (GhostManager.Instance != null)
            GhostManager.Instance.Apply(entries, NetworkManager.Singleton.LocalClientId);
        if (LocalPlayer.Instance != null)
            LocalPlayer.Instance.OnSnapshot(entries, NetworkManager.Singleton.LocalClientId, ackTick);
    }
```

Call site (replace line 108 `SnapshotClientRpc(buf, p);`):
```csharp
            SnapshotClientRpc(buf, registry.Players[viewer].lastProcessedTick, p);
```

- [ ] **Step 4: Compile-check**

Compile-check. `LocalPlayer.OnSnapshot` does not exist yet — it is added in Task 8; to keep this task compiling, add a temporary stub now in `LocalPlayer` (it will be fleshed out in Task 8):
```csharp
    public void OnSnapshot(SnapshotEntry[] entries, ulong localId, uint ackTick) { }
```
Add that stub method to `Assets/_Scripts/Net/LocalPlayer.cs`, then compile-check (clean).

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/Net/PlayerRegistry.cs" "Assets/_Scripts/Net/ReplicationHub.cs" "Assets/_Scripts/Net/LocalPlayer.cs"
git commit -m "Add tick-stamped input RPC + server input buffer + snapshot ackTick"
```

---

### Task 6: Server free-step on FixedUpdate

**Files:**
- Modify: `Assets/_Scripts/Net/PlayerSimSystem.cs` (skip in-instance in `Step`; add `StepInstanceFixed`)
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs` (`FixedUpdate`)

- [ ] **Step 1: Skip in-instance players in the per-frame grid step**

In `Assets/_Scripts/Net/PlayerSimSystem.cs`, at the top of `Step` (after `var m = sp.motion;`, line 19), add:
```csharp
        if (sp.inInstance) return;   // in-instance players move via the fixed free-step, not the grid step
```

- [ ] **Step 2: Add the deterministic server free-step**

In `Assets/_Scripts/Net/PlayerSimSystem.cs`, add a method (and the `using` for the walkable delegate is already implicit — `Game` is global):
```csharp
    // Fixed-tick authoritative free movement for in-instance players. Drains every buffered input whose tick
    // is the next contiguous one, stepping deterministically with MovementStep (same function the client
    // predicts with). lastProcessedTick = the last tick actually applied (what the snapshot acks).
    public static void StepInstanceFixed(PlayerRegistry reg, MovementSettings cfg, float dt)
    {
        var gm = Game.Instance;
        if (gm == null) return;
        System.Func<Vector2, bool> walk = p => { var c = gm.WorldToCell(p); return gm.IsWalkable(c.x, c.y); };
        foreach (var sp in reg.Players.Values)
        {
            if (!sp.inInstance || sp.serverInputs == null) continue;
            while (sp.serverInputs.TryGet(sp.lastProcessedTick + 1, out var f))
            {
                sp.worldPos = MovementStep.Step(sp.worldPos, f.input, dt, cfg.moveSpeed, walk);
                sp.lastInput = f.input;
                sp.lastProcessedTick++;
            }
        }
    }
```

- [ ] **Step 3: Drive it from the Hub's FixedUpdate**

In `Assets/_Scripts/Net/ReplicationHub.cs`, add after `Update()` (line 76):
```csharp
    void FixedUpdate()
    {
        if (!IsServer) return;
        var cfg = Game.Instance != null ? Game.Instance.MovementCfg : null;
        if (cfg != null) PlayerSimSystem.StepInstanceFixed(registry, cfg, Time.fixedDeltaTime);
    }
```

- [ ] **Step 4: Compile-check + play-test (server-authoritative free move)**

Compile-check. Then play-test with Multiplayer Play Mode (Host + 1 virtual client). On a client, enter the dungeon (the `dungeon` debug command or walk into a structure site) and hold a movement key.

Expected: you move **freely** (not cell-snapped) inside the room, bounded by the water moat. It will still feel **floaty/laggy** (full round trip — prediction comes in Phase 3). The overworld still grid-steps. Console clean.

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/Net/PlayerSimSystem.cs" "Assets/_Scripts/Net/ReplicationHub.cs"
git commit -m "Server-authoritative free movement for in-instance players on fixed tick"
```

---

# Phase 3 — Client prediction + reconciliation + render

### Task 7: `PredictionSystem` predict + send; wire into `LocalPlayer`

**Files:**
- Create: `Assets/_Scripts/Net/PredictionSystem.cs`
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs` (own prediction; `FixedUpdate`; activation)

- [ ] **Step 1: Create `PredictionSystem` (predict + store + send; reconcile/render added in Task 8/9)**

`Assets/_Scripts/Net/PredictionSystem.cs`:
```csharp
using UnityEngine;
using UnityEngine.InputSystem;

// Client-side prediction for the local player while in the underworld. Runs on the fixed tick: sample input,
// step locally with the same MovementStep the server uses, store the frame in a ring buffer, and send the
// tick-stamped input. Reconcile() (Task 8) corrects against the server; RenderedPos (Task 9) drives the
// visual. Plain logic class, ticked by LocalPlayer; not a MonoBehaviour.
public sealed class PredictionSystem
{
    public bool Active { get; private set; }
    public Vector2 Pos { get; private set; }            // authoritative-predicted logical position
    public uint Tick { get; private set; }

    InputRingBuffer buffer;
    Vector2 smoothOffset;                                // decays to zero so corrections don't snap (Task 8)

    public Vector2 RenderedPos => Pos + smoothOffset;

    public void Activate(Vector2 startPos)
    {
        var cfg = Game.Instance.MovementCfg;
        buffer = new InputRingBuffer(Mathf.Max(8, Mathf.NextPowerOfTwo(cfg.inputBufferCapacity)));
        Pos = startPos;
        smoothOffset = Vector2.zero;
        Tick = 0;
        Active = true;
    }

    public void Deactivate() => Active = false;

    // Called from LocalPlayer.FixedUpdate while Active.
    public void FixedTick(float dt)
    {
        var cfg = Game.Instance.MovementCfg;
        Vector2 input = SampleInput();
        Tick++;
        Pos = MovementStep.Step(Pos, input, dt, cfg.moveSpeed, Walkable);
        buffer.Store(new InputFrame { tick = Tick, input = input, predictedPos = Pos });
        ReplicationHub.Instance.SubmitInputTickRpc(Tick, input);
    }

    static Vector2 SampleInput()
    {
        if (InputState.Typing) return Vector2.zero;
        var kb = Keyboard.current;
        if (kb == null) return Vector2.zero;
        Vector2 d = Vector2.zero;
        if (kb.wKey.isPressed) d.y += 1f;
        if (kb.sKey.isPressed) d.y -= 1f;
        if (kb.aKey.isPressed) d.x -= 1f;
        if (kb.dKey.isPressed) d.x += 1f;
        return d;
    }

    static bool Walkable(Vector2 p)
    {
        var gm = Game.Instance;
        var c = gm.WorldToCell(p);
        return gm.IsWalkable(c.x, c.y);
    }
}
```

- [ ] **Step 2: Own it from `LocalPlayer` and tick it**

In `Assets/_Scripts/Net/LocalPlayer.cs`:

Add a field near `lastSent` (line 12):
```csharp
    readonly PredictionSystem prediction = new();
    public PredictionSystem Prediction => prediction;
    bool wasInInstance;
```

Add `FixedUpdate` (after `Update`, line 69):
```csharp
    void FixedUpdate()
    {
        if (!Ready) return;
        bool inst = InInstance;
        if (inst && !wasInInstance && SelfWorldPos.HasValue) prediction.Activate(SelfWorldPos.Value);
        else if (!inst && wasInInstance) prediction.Deactivate();
        wasInInstance = inst;
        if (prediction.Active) prediction.FixedTick(Time.fixedDeltaTime);
    }
```

- [ ] **Step 3: Compile-check + play-test (local responsiveness)**

Compile-check. Play-test (Host + virtual client). Enter the dungeon and move.

Expected: your **local** input now moves you **immediately** (snappy) — but the position may slowly disagree with the server until reconciliation lands (Task 8). The other client still sees you via the (laggy) ghost. Console clean. (Note: `SelfWorldPos`/visual still come from the self-ghost until Task 9; you may see a slight double/offset between predicted logic and the ghost visual — expected at this checkpoint.)

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Net/PredictionSystem.cs" "Assets/_Scripts/Net/LocalPlayer.cs"
git commit -m "Add client prediction loop (sample, predict, send) on the fixed tick"
```

---

### Task 8: Reconciliation (replay + smoothing)

**Files:**
- Modify: `Assets/_Scripts/Net/PredictionSystem.cs` (`Reconcile`, smoothing decay)
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs` (`OnSnapshot` → reconcile)

- [ ] **Step 1: Add `Reconcile` + smoothing to `PredictionSystem`**

In `Assets/_Scripts/Net/PredictionSystem.cs`, add:
```csharp
    // Called when a snapshot arrives (Active only). authPos = the server's authoritative self position;
    // ackTick = the last client tick the server simulated. Compare our prediction at that tick; if it
    // diverged, snap the LOGICAL position to authoritative and replay every still-unacked input, then push
    // the pre-correction visual delta into smoothOffset so the on-screen correction eases instead of snapping.
    public void Reconcile(Vector2 authPos, uint ackTick)
    {
        if (!Active || ackTick == 0) return;
        if (!buffer.TryGet(ackTick, out var acked)) { HardSnap(authPos); return; }

        if (Vector2.Distance(acked.predictedPos, authPos) <= Game.Instance.MovementCfg.reconcileEpsilon)
            return;

        Vector2 before = RenderedPos;
        var cfg = Game.Instance.MovementCfg;
        Vector2 p = authPos;
        for (uint t = ackTick + 1; t <= Tick; t++)
        {
            if (!buffer.TryGet(t, out var f)) break;
            p = MovementStep.Step(p, f.input, Time.fixedDeltaTime, cfg.moveSpeed, Walkable);
            f.predictedPos = p;
            buffer.Store(f);
        }
        Pos = p;
        smoothOffset = before - Pos;   // keep the visual where it was; decay the gap to zero in Decay()
    }

    void HardSnap(Vector2 authPos) { Pos = authPos; smoothOffset = Vector2.zero; }

    // Called every frame from LocalPlayer.Update to ease smoothOffset toward zero over correctionSmoothTime.
    public void Decay(float dt)
    {
        if (smoothOffset == Vector2.zero) return;
        float t = Game.Instance.MovementCfg.correctionSmoothTime;
        smoothOffset = (t <= 0f) ? Vector2.zero : Vector2.Lerp(smoothOffset, Vector2.zero, Mathf.Clamp01(dt / t));
        if (smoothOffset.sqrMagnitude < 1e-6f) smoothOffset = Vector2.zero;
    }
```

- [ ] **Step 2: Route the snapshot into `Reconcile`**

In `Assets/_Scripts/Net/LocalPlayer.cs`, replace the Task-5 stub `OnSnapshot` with:
```csharp
    public void OnSnapshot(SnapshotEntry[] entries, ulong localId, uint ackTick)
    {
        if (!prediction.Active) return;
        for (int i = 0; i < entries.Length; i++)
            if (entries[i].id == localId)
            { prediction.Reconcile(new Vector2(entries[i].x, entries[i].y), ackTick); return; }
    }
```

Add the smoothing decay to `LocalPlayer.Update` (top of `Update`, after `if (!Ready) return;`, line 64):
```csharp
        if (prediction.Active) prediction.Decay(Time.deltaTime);
```

- [ ] **Step 3: Compile-check + play-test (reconciliation)**

Compile-check. Play-test (Host + virtual client). Move around the room, including into the walls.

Expected: movement stays snappy AND your position no longer drifts from the server — on a local connection corrections are invisible. Push into a wall: you stop cleanly (server agrees). Console clean.

To exercise correction smoothing, simulate latency: on the `NetworkManager` → Unity Transport, enable **Debug Simulator** (e.g., 120 ms delay, 5% loss) and repeat. Expected: still snappy locally; occasional corrections **ease** in rather than snapping; no rubber-banding.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Net/PredictionSystem.cs" "Assets/_Scripts/Net/LocalPlayer.cs"
git commit -m "Add reconciliation with input replay and smoothed correction"
```

---

### Task 9: Render integration — predicted self owns its visual in-instance

**Files:**
- Modify: `Assets/_Scripts/Net/GhostManager.cs` (don't interpolate the self-ghost in-instance)
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs` (`SelfWorldPos`/`CurrentCell` from prediction; drive the self-ghost transform)

- [ ] **Step 1: Stop interpolating the self-ghost while predicting**

In `Assets/_Scripts/Net/GhostManager.cs`, in `Apply` where the self entry updates the ghost (lines 53-54), guard the self entry so prediction owns it. Replace:
```csharp
            if (snap) { g.fromPos = pos; g.toPos = pos; g.t = 1f; g.tf.position = pos; }
            else { g.fromPos = CurrentPos(g); g.toPos = pos; g.t = 0f; g.dur = dur; }
```
with:
```csharp
            bool predictedSelf = e.id == localId && LocalPlayer.Instance != null && LocalPlayer.Instance.Prediction.Active;
            if (predictedSelf) { /* PredictionSystem drives this transform; ignore snapshot interp for self */ }
            else if (snap) { g.fromPos = pos; g.toPos = pos; g.t = 1f; g.tf.position = pos; }
            else { g.fromPos = CurrentPos(g); g.toPos = pos; g.t = 0f; g.dur = dur; }
```

And in `GhostManager.Update` (lines 78-82), skip the self-ghost while predicting:
```csharp
        bool predicting = LocalPlayer.Instance != null && LocalPlayer.Instance.Prediction.Active;
        foreach (var g in ghosts.Values)
        {
            if (predicting && g.tf == SelfGhost) continue;   // PredictionSystem positions the self-ghost
            if (g.dur > 1e-5f && g.t < 1f) g.t = Mathf.Min(1f, g.t + dt / g.dur);
            g.tf.position = CurrentPos(g);
        }
```

- [ ] **Step 2: Drive the self-ghost from prediction and report predicted position**

In `Assets/_Scripts/Net/LocalPlayer.cs`:

Change `SelfWorldPos` (lines 21-28) to prefer prediction:
```csharp
    public Vector2? SelfWorldPos
    {
        get
        {
            if (prediction.Active) return prediction.RenderedPos;
            var self = GhostManager.Instance != null ? GhostManager.Instance.SelfGhost : null;
            return self != null ? (Vector2?)(Vector2)self.position : null;
        }
    }
```

Change `CurrentCell` (lines 30-36) to use the predicted position when active:
```csharp
    public Vector2Int CurrentCell()
    {
        var gm = Game.Instance;
        if (gm == null) return Vector2Int.zero;
        if (prediction.Active) return gm.WorldToCell(prediction.RenderedPos);
        var self = GhostManager.Instance != null ? GhostManager.Instance.SelfGhost : null;
        return self != null ? gm.WorldToCell(self.position) : Vector2Int.zero;
    }
```

Position the self-ghost visual from prediction each frame — append to `LocalPlayer.Update` (after the `prediction.Decay` line from Task 8):
```csharp
        if (prediction.Active && GhostManager.Instance != null && GhostManager.Instance.SelfGhost != null)
            GhostManager.Instance.SelfGhost.position = prediction.RenderedPos;
```

- [ ] **Step 3: Compile-check + play-test (full feel)**

Compile-check. Play-test (Host + virtual client).

Expected:
- Underworld: your character (the self-ghost visual) moves **immediately** with input; the camera follows smoothly; `PlayerView` animates walk/idle/facing from the predicted motion.
- The other client sees you as a smooth interpolated ghost (slightly in the past) — unchanged.
- Enter/leave the dungeon: feel switches cleanly between predicted-free (underworld) and grid (overworld); the teleport doesn't streak.
- Overworld: identical to before. Console clean.

With the Transport Debug Simulator on (120 ms / 5% loss): owner stays snappy, corrections ease, remote ghost stays smooth.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Net/GhostManager.cs" "Assets/_Scripts/Net/LocalPlayer.cs"
git commit -m "Predicted self owns its visual + position in the underworld"
```

---

### Task 10 (optional polish): Movement tuner panel

**Files:**
- Modify: `Assets/_Scripts/UI/TunerPanels.cs` (add a "Movement" accordion: moveSpeed, reconcileEpsilon, correctionSmoothTime; Save/Reset via `Game.SaveMovementSettings`/`ResetMovementSettings`)

- [ ] **Step 1:** Mirror the existing "Replication" accordion in `TunerPanels.cs` for the three live-tunable `MovementSettings` floats, wired to `Game.Instance.MovementCfg` and the Save/Reset methods from Task 4. (Follow the file's existing accordion pattern exactly; `inputBufferCapacity` is not live-tunable — omit it.)

- [ ] **Step 2: Compile-check + commit**

```bash
git add "Assets/_Scripts/UI/TunerPanels.cs"
git commit -m "Add Movement tuner panel"
```

---

## Self-review (done while writing)

- **Spec coverage:** §3 components → Tasks 1-2 (`MovementStep`, `InputRingBuffer`), 4 (`MovementSettings`), 5 (`ackTick`, server buffer), 6 (`PlayerSimSystem` free-step), 7-8 (`PredictionSystem`), 9 (`GhostManager`/`LocalPlayer`). §6 tick raise → Task 3. §7 mode switch → Task 7 (activation on `InInstance`). All covered.
- **Type consistency:** `InputFrame{tick,input,predictedPos}`, `InputRingBuffer.Store/TryGet`, `MovementStep.Step(pos,input,dt,speed,Func<Vector2,bool>)`, `PredictionSystem.Activate/Deactivate/FixedTick/Reconcile/Decay/RenderedPos/Active/Pos/Tick`, `LocalPlayer.OnSnapshot/Prediction`, `ServerPlayer.lastProcessedTick/lastInput/serverInputs`, `SubmitInputTickRpc(uint,Vector2)`, `SnapshotClientRpc(SnapshotEntry[],uint,ClientRpcParams)`, `Game.MovementCfg` — consistent across tasks.
- **Known coupling:** Task 5 introduces a `LocalPlayer.OnSnapshot` stub so the Hub compiles before Task 8 fills it in — called out explicitly.
- **No placeholders:** every code step has complete code; networked tasks use play-test verification (NGO integration isn't unit-testable here).

## Notes / deferred (from the spec §12)

- Server jitter handling is "step only when the next contiguous input is buffered" (no extrapolation) — simplest correct choice for reconciliation; revisit if late inputs feel stuttery under bad networks.
- Input redundancy (sending a trailing window each tick) is **not** in v1 — one input per tick. Add if loss causes server stalls.
- `snapshotHz` is capped at 30 by `[Range(1,30)]`; sufficient because prediction drives owner feel. Widen only if combat needs fresher remote ghosts.
- Edge-triggered combat inputs (attack/feint/dodge) will need Update-buffered capture — out of scope (combat spec).
