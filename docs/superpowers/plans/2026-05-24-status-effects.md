# Status Effects Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A scalable, data-driven, server-authoritative + client-predicted status-effect framework for in-instance combat (HitStun, AttackCooldown, Poison-DOT, Freeze, Slow) plus a minimal HP pool, replacing the ad-hoc feint cooldown and the `AbilityGate` WIP.

**Architecture:** A pure, deterministic `StatusState` (per player) that `StatusLogic` ages each fixed tick and reduces to the existing `GateMod` the sim already consumes. Effects are authored in a `StatusCatalog` SO (byte-id'd, like `WeaponCatalog`). Server applies effects (feint → self-inflicted AttackCooldown; on-hit → damage + HitStun/Poison via a broadphase query); the owner predicts self-inflicted effects and adopts external ones from the snapshot (Approach 1 from the spec). HP is server-authoritative and replicated for display only.

**Tech Stack:** Unity (C#), Unity Netcode for GameObjects (custom snapshot RPCs, not NetworkVariable/NetworkTransform), NUnit EditMode tests, unity-mcp for compile/test/Play verification.

**Spec:** `docs/superpowers/specs/2026-05-24-status-effects-design.md`

**Assembly map (do not cross):**
- Pure **data** → `Assets/_Scripts/Combat/Core/` (assembly `Minifantasy.Combat`): `StatusKind`, `StackPolicy`, `ActiveEffect`, `StatusEffectDef`, `StatusState`. Holds *raw* gate fields (no `GateMod` ref).
- Pure **logic** → `Assets/_Scripts/InstanceSim/` (assembly `Minifantasy.InstanceSim`, refs `Minifantasy.Combat`): `StatusLogic` (reduces to `GateMod`, which lives here).
- **SO catalog** → `Assets/_Scripts/Combat/StatusCatalog.cs` (`Minifantasy.Combat`).
- **Editor builder** → `Assets/Editor/StatusCatalogBuilder.cs`.
- **Server/owner/wire/visual** → `Assets/_Scripts/Net/` + `Assets/_Scripts/Combat/` (default `Assembly-CSharp`).
- **Tests** → `Assets/Tests/EditMode/InstanceSim/` (`Minifantasy.InstanceSim.Tests`, already refs InstanceSim + Combat + Movement).

**Verification flow (per `unity-mcp-verification` memory — `execute_code` is broken on this machine):**
> After editing scripts: `mcp__unity-mcp__refresh_unity` with **`scope=all`** (required when adding `.cs` files) → poll the `editor_state` resource until `isCompiling=false` → `mcp__unity-mcp__read_console` (errors=true) clean → `mcp__unity-mcp__run_tests` (EditMode) for the relevant assembly → enter Play and screenshot for boot. The two-player host feel-test is **manual** (MCP cannot click the IMGUI Host button).

**Commit hygiene (per `feedback-committing-wip` + `feedback-commit-style` memory):** the repo carries unrelated WIP. Commit **only the files each task lists, by explicit path** — never `git add -A`/`.`. **No Claude attribution** in commit messages (no `Co-Authored-By`, no "Generated with"). New authored asset `Assets/_Combat/StatusCatalog.asset` is committed; regenerated player prefab stays untracked.

**Shared constants used across tasks (define in Task 1):**
- `StatusKind : byte { HitStun = 0, AttackCooldown = 1, Poison = 2, Freeze = 3, Slow = 4 }` — value == catalog index == wire `defId`.
- `StatusState.Cap = 8` (max simultaneous effects).
- Sim tick rate is 60 Hz (`Time.fixedDeltaTime = 1/60`); seconds→ticks = `Mathf.CeilToInt(seconds * 60f)`.

---

## Phase 1 — Pure status core + catalog (additive; `AbilityGate` untouched)

### Task 1: Status data types

**Files:**
- Create: `Assets/_Scripts/Combat/Core/StatusTypes.cs`

- [ ] **Step 1: Create the data types**

All pure data (no `GateMod`, no Netcode, no scene). One file (small, cohesive — mirrors `AttackTypes.cs`).

```csharp
// Assets/_Scripts/Combat/Core/StatusTypes.cs
// Pure status-effect data: the catalog-resolved def, one active instance, and the per-player collection.
// No GateMod/Netcode/scene refs (StatusLogic, in Minifantasy.InstanceSim, reduces these to a GateMod).

// Well-known effect ids: the value IS the catalog index AND the wire defId. Keep in sync with StatusCatalog order.
public enum StatusKind : byte { HitStun = 0, AttackCooldown = 1, Poison = 2, Freeze = 3, Slow = 4 }

// How a re-applied effect of the same kind combines with an existing instance.
public enum StackPolicy : byte { Refresh, Stack, Independent }

// One catalog entry, resolved to ticks/raw-gate fields for the deterministic sim.
public struct StatusEffectDef
{
    public byte id;
    public int durationTicks;     // 0 = no inherent lifetime (AttackCooldown supplies its own at apply-time)
    public bool blocksMove;
    public bool blocksAttack;
    public float moveScale;       // 1 = no slow
    public int periodTicks;       // 0 = no periodic effect
    public int amountPerTick;     // damage per period (× stacks)
    public StackPolicy policy;
    public byte maxStacks;
    public byte visualId;
}

// One live effect on a player.
public struct ActiveEffect
{
    public byte defId;
    public int remainingTicks;
    public byte stacks;
    public int sincePeriodTick;
    public uint appliedTick;
    public bool selfInflicted;    // true = owner-predicted (AttackCooldown); false = adopted from server
}

// Per-player active-effect collection. Plain data; StatusLogic does all the work. Fixed capacity, compacted
// on removal (swap-remove), so iteration is effects[0..count). A class (one instance per player/predictor;
// Approach 1 needs no per-frame copy), reused each tick — no steady-state allocation.
public sealed class StatusState
{
    public const int Cap = 8;
    public readonly ActiveEffect[] effects = new ActiveEffect[Cap];
    public int count;

    public void Clear() => count = 0;
}
```

- [ ] **Step 2: Compile-gate**

Run unity-mcp `refresh_unity` `scope=all`; poll `editor_state` until `isCompiling=false`; `read_console` (errors) — expect clean (pure additive types, no consumers yet).

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/Combat/Core/StatusTypes.cs" "Assets/_Scripts/Combat/Core/StatusTypes.cs.meta"
git commit -m "Combat: status-effect data types (StatusKind/StackPolicy/StatusEffectDef/ActiveEffect/StatusState)"
```

---

### Task 2: `StatusLogic` (pure reduce / apply / step) + tests

**Files:**
- Create: `Assets/_Scripts/InstanceSim/StatusLogic.cs`
- Create: `Assets/Tests/EditMode/InstanceSim/StatusLogicTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Assets/Tests/EditMode/InstanceSim/StatusLogicTests.cs
using NUnit.Framework;

// Pure status core: reduction to GateMod, stacking policies, deterministic ageing + periodic accrual.
public class StatusLogicTests
{
    // Minimal catalog indexed by StatusKind. Durations/periods in ticks.
    static StatusEffectDef[] Defs() => new[]
    {
        new StatusEffectDef { id = 0, durationTicks = 18, blocksMove = true,  blocksAttack = true,  moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 1 }, // HitStun
        new StatusEffectDef { id = 1, durationTicks = 0,  blocksMove = false, blocksAttack = true,  moveScale = 1f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 0 }, // AttackCooldown
        new StatusEffectDef { id = 2, durationTicks = 180,blocksMove = false, blocksAttack = false, moveScale = 1f, periodTicks = 30, amountPerTick = 5, policy = StackPolicy.Stack, maxStacks = 5, visualId = 2 }, // Poison
        new StatusEffectDef { id = 3, durationTicks = 90, blocksMove = true,  blocksAttack = false, moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 3 }, // Freeze
        new StatusEffectDef { id = 4, durationTicks = 120,blocksMove = false, blocksAttack = false, moveScale = 0.5f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 4 }, // Slow
    };

    [Test]
    public void Empty_ReducesToNone()
    {
        var g = StatusLogic.Reduce(new StatusState(), Defs());
        Assert.IsTrue(g.CanMove);
        Assert.IsTrue(g.CanAttack);
        Assert.AreEqual(1f, g.moveScale, 1e-4f);
    }

    [Test]
    public void HitStun_BlocksMoveAndAttack()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.HitStun], tick: 1, self: false);
        var g = StatusLogic.Reduce(s, Defs());
        Assert.IsFalse(g.CanMove);
        Assert.IsFalse(g.CanAttack);
    }

    [Test]
    public void OverlappingSlows_MultiplyMoveScale()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Slow], tick: 1, self: false);            // 0.5
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Freeze], tick: 1, self: false);          // 0 (root)
        var g = StatusLogic.Reduce(s, Defs());
        Assert.AreEqual(0f, g.moveScale, 1e-4f);   // 0.5 * 0 = 0
        Assert.IsFalse(g.CanMove);                 // Freeze blocksMove
    }

    [Test]
    public void Refresh_ResetsDuration_NoSecondInstance()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Freeze], tick: 1, self: false);
        s.effects[0].remainingTicks = 5;                       // age it down
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Freeze], tick: 10, self: false);
        Assert.AreEqual(1, s.count);                           // still one instance
        Assert.AreEqual(90, s.effects[0].remainingTicks);      // duration refreshed
    }

    [Test]
    public void Stack_IncrementsUpToMax_AndScalesPeriodicDamage()
    {
        var s = new StatusState();
        var defs = Defs();
        for (int i = 0; i < 7; i++) StatusLogic.Apply(s, defs[(int)StatusKind.Poison], tick: 1, self: false);
        Assert.AreEqual(1, s.count);                 // one Poison instance
        Assert.AreEqual(5, s.effects[0].stacks);     // capped at maxStacks=5
        // Advance 30 ticks (one period); expect 5 dmg/tick × 5 stacks = 25.
        int dmg = 0, last = 0;
        for (int t = 0; t < 30; t++) { StatusLogic.Step(s, defs, out last); dmg += last; }
        Assert.AreEqual(25, dmg);
    }

    [Test]
    public void Step_ExpiresAtZero()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.HitStun], tick: 1, self: false);   // 18 ticks
        for (int t = 0; t < 18; t++) StatusLogic.Step(s, defs, out _);
        Assert.AreEqual(0, s.count);                 // expired and removed
    }

    [Test]
    public void Step_GateActiveOnFinalTick()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.HitStun], tick: 1, self: false);
        s.effects[0].remainingTicks = 1;             // one tick left
        var g = StatusLogic.Step(s, defs, out _);    // gate is computed BEFORE decrement
        Assert.IsFalse(g.CanMove);                   // still blocked this tick
        Assert.AreEqual(0, s.count);                 // then expires
    }

    [Test]
    public void Apply_DurationOverride_UsedForAttackCooldown()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.AttackCooldown], tick: 1, self: true, durationOverride: 30);
        Assert.AreEqual(30, s.effects[0].remainingTicks);
        Assert.IsTrue(s.effects[0].selfInflicted);
    }

    [Test]
    public void ActiveMask_HasBitPerActiveKind()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.Poison], tick: 1, self: false);
        StatusLogic.Apply(s, defs[(int)StatusKind.Slow], tick: 1, self: false);
        byte mask = StatusLogic.ActiveMask(s);
        Assert.AreEqual((1 << (int)StatusKind.Poison) | (1 << (int)StatusKind.Slow), mask);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

unity-mcp `run_tests` mode `EditMode`, filter `Minifantasy.InstanceSim.Tests`. Expected: FAIL/compile error — `StatusLogic` does not exist.

- [ ] **Step 3: Implement `StatusLogic`**

```csharp
// Assets/_Scripts/InstanceSim/StatusLogic.cs
using UnityEngine;

// Pure, deterministic status-effect engine. Reduces a StatusState to the GateMod the sim consumes, applies new
// effects per stacking policy, and ages effects one fixed tick at a time (one Step() call == one tick; integer
// counts, no dt). Shared by server sim, owner predict, and (forward) replay. No Time/random/Netcode/scene.
public static class StatusLogic
{
    // OR the block flags, multiply the moveScales — order-independent, so overlapping effects compose and
    // clearing one never re-enables what another still blocks (the same math AbilityGate used).
    public static GateMod Reduce(StatusState s, StatusEffectDef[] defs)
    {
        bool blockMove = false, blockAttack = false; float scale = 1f;
        for (int i = 0; i < s.count; i++)
        {
            var d = defs[s.effects[i].defId];
            blockMove |= d.blocksMove;
            blockAttack |= d.blocksAttack;
            scale *= Mathf.Clamp01(d.moveScale);
        }
        return new GateMod { blocksMove = blockMove, blocksAttack = blockAttack, moveScale = Mathf.Clamp01(scale) };
    }

    // Age every effect one tick: gate is read BEFORE decrement (an effect is active the tick it hits zero),
    // periodic damage accrues into periodicDamage (× stacks), expired effects are swap-removed. Returns the gate.
    public static GateMod Step(StatusState s, StatusEffectDef[] defs, out int periodicDamage)
    {
        GateMod g = Reduce(s, defs);
        periodicDamage = 0;
        for (int i = 0; i < s.count; )
        {
            ref var e = ref s.effects[i];
            var d = defs[e.defId];
            if (d.periodTicks > 0)
            {
                e.sincePeriodTick++;
                while (e.sincePeriodTick >= d.periodTicks)
                {
                    e.sincePeriodTick -= d.periodTicks;
                    periodicDamage += d.amountPerTick * e.stacks;
                }
            }
            if (d.durationTicks > 0 || e.remainingTicks > 0)   // 0-duration AttackCooldown uses an override > 0
            {
                e.remainingTicks--;
                if (e.remainingTicks <= 0) { RemoveAt(s, i); continue; }
            }
            i++;
        }
        return g;
    }

    // Add or combine per stacking policy. durationOverride >= 0 wins (AttackCooldown passes the weapon's value).
    public static void Apply(StatusState s, in StatusEffectDef d, uint tick, bool self, float scale = 1f, int durationOverride = -1)
    {
        int dur = durationOverride >= 0 ? durationOverride : Mathf.CeilToInt(d.durationTicks * scale);
        if (d.policy != StackPolicy.Independent)
        {
            for (int i = 0; i < s.count; i++)
            {
                if (s.effects[i].defId != d.id) continue;
                if (d.policy == StackPolicy.Stack)
                    s.effects[i].stacks = (byte)Mathf.Min(d.maxStacks, s.effects[i].stacks + 1);
                s.effects[i].remainingTicks = dur;
                s.effects[i].appliedTick = tick;
                return;
            }
        }
        if (s.count >= StatusState.Cap) { if (!ReplaceWeakest(s, dur)) return; }
        s.effects[s.count++] = new ActiveEffect
        {
            defId = d.id, remainingTicks = dur, stacks = 1, sincePeriodTick = 0, appliedTick = tick, selfInflicted = self,
        };
    }

    public static bool Remove(StatusState s, byte defId)
    {
        for (int i = 0; i < s.count; i++) if (s.effects[i].defId == defId) { RemoveAt(s, i); return true; }
        return false;
    }

    // One bit per active effect kind (defId), for the cosmetic remote wire.
    public static byte ActiveMask(StatusState s)
    {
        byte m = 0;
        for (int i = 0; i < s.count; i++) m |= (byte)(1 << s.effects[i].defId);
        return m;
    }

    static void RemoveAt(StatusState s, int i) { s.effects[i] = s.effects[--s.count]; }   // swap-remove

    // Over cap: replace the instance with the fewest remaining ticks if the newcomer would outlast it.
    static bool ReplaceWeakest(StatusState s, int newDur)
    {
        int weakest = 0;
        for (int i = 1; i < s.count; i++) if (s.effects[i].remainingTicks < s.effects[weakest].remainingTicks) weakest = i;
        if (s.effects[weakest].remainingTicks >= newDur) return false;
        RemoveAt(s, weakest);
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

unity-mcp `run_tests` EditMode `Minifantasy.InstanceSim.Tests`. Expected: PASS (all of `StatusLogicTests`).

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/InstanceSim/StatusLogic.cs" "Assets/_Scripts/InstanceSim/StatusLogic.cs.meta" "Assets/Tests/EditMode/InstanceSim/StatusLogicTests.cs" "Assets/Tests/EditMode/InstanceSim/StatusLogicTests.cs.meta"
git commit -m "InstanceSim: StatusLogic (reduce/apply/step) + unit tests"
```

---

### Task 3: `StatusCatalog` SO + builder + asset + `Game` wiring

**Files:**
- Create: `Assets/_Scripts/Combat/StatusCatalog.cs`
- Create: `Assets/Editor/StatusCatalogBuilder.cs`
- Modify: `Assets/_Scripts/Game.cs` (add `StatusCatalog` ref + property + Awake guard)

- [ ] **Step 1: Create the catalog SO**

Authored in seconds; `Defs` caches the tick-resolved array. Indexed by `StatusKind` order.

```csharp
// Assets/_Scripts/Combat/StatusCatalog.cs
using UnityEngine;

// Shared byte-id <-> status-effect map (the wire carries defId == StatusKind). Authored in seconds; Defs
// resolves to ticks (60 Hz) for the deterministic sim. Order MUST match StatusKind. Wired on Game; built by
// Tools > Combat > Build Status Catalog. Mirrors WeaponCatalog.
[CreateAssetMenu(menuName = "Minifantasy/Status Catalog", fileName = "StatusCatalog")]
public sealed class StatusCatalog : ScriptableObject
{
    [System.Serializable]
    public struct Entry
    {
        public string name;            // documentation only
        public float durationSeconds;  // 0 = supplied at apply-time (AttackCooldown)
        public bool blocksMove;
        public bool blocksAttack;
        public float moveScale;        // 1 = none
        public float periodSeconds;    // 0 = no DOT
        public int amountPerTick;
        public StackPolicy policy;
        public byte maxStacks;
        public byte visualId;
    }

    public int maxHp = 100;
    public Entry[] entries;

    StatusEffectDef[] _defs;
    public StatusEffectDef[] Defs => _defs ??= Build();

    StatusEffectDef[] Build()
    {
        int n = entries != null ? entries.Length : 0;
        var defs = new StatusEffectDef[n];
        for (int i = 0; i < n; i++)
        {
            var e = entries[i];
            defs[i] = new StatusEffectDef
            {
                id = (byte)i,
                durationTicks = Mathf.CeilToInt(e.durationSeconds * 60f),
                blocksMove = e.blocksMove,
                blocksAttack = e.blocksAttack,
                moveScale = e.moveScale,
                periodTicks = Mathf.CeilToInt(e.periodSeconds * 60f),
                amountPerTick = e.amountPerTick,
                policy = e.policy,
                maxStacks = (byte)Mathf.Max(1, e.maxStacks),
                visualId = e.visualId,
            };
        }
        return defs;
    }

    void OnValidate() => _defs = null;   // rebuild after Inspector edits
}
```

- [ ] **Step 2: Create the builder (seeds the 5 v1 effects in `StatusKind` order)**

```csharp
// Assets/Editor/StatusCatalogBuilder.cs
using UnityEditor;
using UnityEngine;

// One-shot: create/refresh the shared StatusCatalog asset with the v1 effects in StatusKind order.
// Run: Tools > Combat > Build Status Catalog. Re-running resets the 5 entries to these defaults (tune in Inspector after).
public static class StatusCatalogBuilder
{
    const string CatalogPath = "Assets/_Combat/StatusCatalog.asset";

    [MenuItem("Tools/Combat/Build Status Catalog")]
    public static void Build()
    {
        var catalog = AssetDatabase.LoadAssetAtPath<StatusCatalog>(CatalogPath);
        bool created = catalog == null;
        if (created) catalog = ScriptableObject.CreateInstance<StatusCatalog>();
        catalog.maxHp = 100;
        catalog.entries = new[]
        {
            new StatusCatalog.Entry { name = "HitStun",        durationSeconds = 0.3f, blocksMove = true,  blocksAttack = true,  moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 1 },
            new StatusCatalog.Entry { name = "AttackCooldown", durationSeconds = 0f,   blocksMove = false, blocksAttack = true,  moveScale = 1f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 0 },
            new StatusCatalog.Entry { name = "Poison",         durationSeconds = 3f,   blocksMove = false, blocksAttack = false, moveScale = 1f, periodSeconds = 0.5f, amountPerTick = 5, policy = StackPolicy.Stack, maxStacks = 5, visualId = 2 },
            new StatusCatalog.Entry { name = "Freeze",         durationSeconds = 1.5f, blocksMove = true,  blocksAttack = false, moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 3 },
            new StatusCatalog.Entry { name = "Slow",           durationSeconds = 2f,   blocksMove = false, blocksAttack = false, moveScale = 0.5f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 4 },
        };
        if (created) AssetDatabase.CreateAsset(catalog, CatalogPath); else EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        Debug.Log($"[StatusCatalog] {(created ? "created" : "updated")} with {catalog.entries.Length} effects, maxHp={catalog.maxHp}");
    }
}
```

- [ ] **Step 3: Add the `StatusCatalog` ref to `Game`**

In `Assets/_Scripts/Game.cs`, find the existing `WeaponCatalog` serialized field + property (the attack-replication footgun guard). Add a sibling. Locate the field declaration (grep `WeaponCatalog`), add next to it:

```csharp
    [SerializeField] StatusCatalog statusCatalog;
    public StatusCatalog StatusCatalog => statusCatalog;
```

In `Game.Awake()`, next to the existing `if (weaponCatalog == null) Debug.LogError(...)` guard, add:

```csharp
        if (statusCatalog == null) Debug.LogError("[Game] StatusCatalog is unassigned — status effects (hitstun/cooldown/poison) will not work. Assign it on the Game component (GridManager prefab instance).");
```

- [ ] **Step 4: Build the asset + wire it (unity-mcp)**

1. `refresh_unity` `scope=all`; poll `editor_state` until `isCompiling=false`; `read_console` clean.
2. `execute_menu_item` `Tools/Combat/Build Status Catalog` → expect log `[StatusCatalog] created with 5 effects, maxHp=100` and the asset at `Assets/_Combat/StatusCatalog.asset`.
3. Wire it on the `Game` component of the `GridManager` scene instance (per the WeaponCatalog footgun pattern — `Game` lives on `GridManager.prefab`, instanced as a scene override): `manage_components` `set_property` on the `GridManager` instance, component `Game`, property `statusCatalog`, value = the `StatusCatalog.asset` reference; then `manage_scene` `save`.
4. Enter Play; `read_console` — expect **no** `[Game] StatusCatalog is unassigned` error. Screenshot boot.

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/Combat/StatusCatalog.cs" "Assets/_Scripts/Combat/StatusCatalog.cs.meta" "Assets/Editor/StatusCatalogBuilder.cs" "Assets/Editor/StatusCatalogBuilder.cs.meta" "Assets/_Combat/StatusCatalog.asset" "Assets/_Combat/StatusCatalog.asset.meta" "Assets/_Scripts/Game.cs"
git commit -m "Combat: StatusCatalog SO + builder + Game wiring (5 v1 effects, maxHp)"
```
(Also commit the scene file **only if** the `statusCatalog` reference is the sole change you made to it; otherwise wire via Inspector and tell Ryan to save+commit the scene himself — do not bundle unrelated scene WIP.)

---

## Phase 2 — Sim integration, feint-cooldown migration, remove `AbilityGate`

### Task 4: Drive the sim gate from `StatusState` (plumb through `InstanceStep` + both callers)

Swaps the gate **source** from the externally-supplied `ctx.gate` / `AbilityGate` to the per-player `StatusState`, stepped each tick. Feint cooldown still uses `AttackState.cooldown` here (migrated in Task 5). Ends compiling, tests green, Play boots.

**Files:**
- Modify: `Assets/_Scripts/InstanceSim/InstanceStep.cs`
- Modify: `Assets/_Scripts/Net/PlayerRegistry.cs` (`ServerPlayer`: add `status` + `hp`)
- Modify: `Assets/_Scripts/Net/AttackSimSystem.cs` (server caller)
- Modify: `Assets/_Scripts/Net/PredictionSystem.cs` (owner caller, both paths)
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs` (drop `currentGate` plumbing)
- Modify: `Assets/Tests/EditMode/InstanceSim/GateTests.cs` (new InstanceStep signature; gate-via-status)

- [ ] **Step 1: Rewrite `InstanceStep`**

`InstanceCtx` drops `gate`, gains `defs`. `Step` gains `StatusState status` + `out InstanceResult result`; it steps the status to get the gate and reports `feinted` (detected here) + `periodicDamage`.

```csharp
// Assets/_Scripts/InstanceSim/InstanceStep.cs
using System;
using UnityEngine;

public struct InstanceInput
{
    public Vector2 rawMove;
    public AttackIntent attack;
}

public struct InstanceCtx
{
    public AttackTimeline timeline;
    public PhaseScales scales;
    public float dt;
    public float speed;
    public Func<Vector2, bool> walkable;
    public StatusEffectDef[] defs;   // status catalog (gate is derived from the player's StatusState, not passed in)
}

public struct InstanceResult
{
    public bool feinted;        // a feint cancelled a windup this tick (caller emits the event / nothing client-side)
    public int periodicDamage;  // DOT accrued this tick (server applies to HP; predictor ignores)
}

public static class InstanceStep
{
    public static void Step(ref AttackState atk, StatusState status, ref Vector2 pos, in InstanceInput cmd, in InstanceCtx ctx, out InstanceResult result)
    {
        result = default;
        GateMod g = StatusLogic.Step(status, ctx.defs, out result.periodicDamage);  // age effects → effective gate
        g = GateMod.Quantize(g);                                                     // server == owner consume the wire value

        AttackPhase prev = atk.phase;
        atk = AttackLogic.Step(atk, cmd.attack, ctx.timeline, ctx.scales, ctx.dt, g.CanAttack);

        // Feint = a windup cancelled to Idle while attacking was allowed (a gated interrupt is not a feint).
        if (g.CanAttack && prev == AttackPhase.Anticipation && atk.phase == AttackPhase.Idle && cmd.attack.feint)
        {
            result.feinted = true;
            // tick 0: appliedTick on a self-inflicted cooldown is unused by reconcile (Task 11 keeps self-inflicted
            // effects by KIND, not tick). InstanceInput carries no tick field.
            StatusLogic.Apply(status, ctx.defs[(int)StatusKind.AttackCooldown], 0u, self: true,
                              durationOverride: ctx.timeline.feintCooldownTicks);
        }

        Vector2? lunge = AttackLogic.LungeVelocity(atk, ctx.timeline);
        Vector2 move = lunge ?? FreeMove(cmd.rawMove, g);
        pos = MovementStep.Step(pos, move, ctx.dt, ctx.speed, ctx.walkable);
    }

    public static Vector2 FreeMove(Vector2 rawMove, in GateMod gate)
    {
        if (!gate.CanMove) return Vector2.zero;
        Vector2 dir = rawMove.sqrMagnitude > 1f ? rawMove.normalized : rawMove;
        return dir * gate.moveScale;
    }
}
```

But `AttackState.cooldown` is still set by `AttackLogic` until Task 5. To avoid double cooldown (old field + new effect) for this one task, **also do Step 2 now**: make `AttackLogic` stop writing `cooldown`. (Tasks 4 and 5 both touch the feint path; doing the `AttackLogic` edit here keeps behavior single-sourced. `AttackState.cooldown` the *field* is removed in Task 5.)

- [ ] **Step 2: `AttackLogic` — stop writing/reading `cooldown` (keep the field for now)**

In `Assets/_Scripts/Combat/Core/AttackLogic.cs`:

Idle branch — remove the cooldown decrement + cooldown gate on starting (the gate's `canAttack` now blocks):
```csharp
            case AttackPhase.Idle:
                if (intent.pressed && Has(tl.anticipation))
                {
                    s.phase = AttackPhase.Anticipation;
                    s.frameIndex = 0; s.phaseElapsed = 0f; s.windupComplete = false;
                    Aim(ref s, tl, intent.aimDir);
                }
                return s;
```
Feint branch — return Idle with **no** cooldown:
```csharp
                if (intent.feint)
                    return new AttackState { phase = AttackPhase.Idle };
```
Gated branch (top of `Step`, `!canAttack`) — remove the cooldown decrement:
```csharp
        if (!canAttack)
        {
            if (s.phase != AttackPhase.Idle) return new AttackState { phase = AttackPhase.Idle };
            return s;
        }
```
(`AttackState.cooldown` is now never written; it stays 0. Field removed in Task 5.)

- [ ] **Step 3: Add `feintCooldownTicks` to `AttackTimeline`**

In `Assets/_Scripts/Combat/Core/AttackTimeline.cs` add a field:
```csharp
    public int feintCooldownTicks;   // feintCooldown converted to ticks (60 Hz); the AttackCooldown duration
```
In `Assets/_Scripts/Combat/AttackDefinition.cs` `BuildTimeline()`, set it alongside `feintCooldown`:
```csharp
            feintCooldown = feintCooldown,
            feintCooldownTicks = Mathf.CeilToInt(feintCooldown * 60f),
```

- [ ] **Step 4: `ServerPlayer` — add `status` + `hp`**

In `Assets/_Scripts/Net/PlayerRegistry.cs`, in `ServerPlayer`, add (keep `gate` for now — removed in Task 6):
```csharp
    public readonly StatusState status = new();   // active effects; reduces to the gate the sim consumes
    public int hp;                                // server-authoritative; set on instance enter (Task 8)
```

- [ ] **Step 5: `AttackSimSystem` — use `status`, new `InstanceStep` signature**

In `Assets/_Scripts/Net/AttackSimSystem.cs` `StepInstanceFixed`, the in-instance integration loop. Replace the gate line + the `InstanceStep.Step` call. The full inner `while`/`if (def != null)` block becomes:

```csharp
            Vector2 startPos = sp.worldPos;
            if (sp.serverInputs != null)
            {
                var def = catalog != null ? catalog.Get(sp.weaponId) : null;
                while (sp.serverInputs.TryGet(sp.lastProcessedTick + 1, out var c))
                {
                    if (def != null)
                    {
                        var ctx = new InstanceCtx { timeline = def.Timeline, scales = sp.attackScales, dt = dt, speed = cfg.moveSpeed, walkable = _walkAt, defs = statusDefs };
                        sp.prevAttackPhase = sp.attackState.phase;
                        var atk = sp.attackState; var pos = sp.worldPos;
                        InstanceStep.Step(ref atk, sp.status, ref pos, new InstanceInput { rawMove = c.rawMove, attack = ToIntent(c) }, ctx, out var res);
                        sp.attackState = atk; sp.worldPos = pos;
                        if (res.periodicDamage > 0) ApplyDamage(sp, res.periodicDamage);   // Task 8 adds ApplyDamage; until then, inline: sp.hp -= res.periodicDamage;
                        EmitTransitions(kv.Key, sp, def, c.tick, res.feinted);
                    }
                    else
                    {
                        // No weapon: still age status so slows/roots tick, and gate free-move.
                        var gate = StatusLogic.Step(sp.status, statusDefs, out int dmg);
                        if (dmg > 0) sp.hp -= dmg;
                        sp.worldPos = MovementStep.Step(sp.worldPos, InstanceStep.FreeMove(c.rawMove, GateMod.Quantize(gate)), dt, cfg.moveSpeed, _walkAt);
                    }
                    sp.lastInput = c.rawMove;
                    sp.lastProcessedTick++;
                }
            }
```

Add the `statusDefs` local at the top of `StepInstanceFixed` (after `_gm = gm;`):
```csharp
        var statusCatalog = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        var statusDefs = statusCatalog != null ? statusCatalog.Defs : System.Array.Empty<StatusEffectDef>();
```
Change the method signature to receive the status catalog (so the server doesn't depend on the `Game` singleton mid-loop more than today) — **or** keep reading `Game.Instance.StatusCatalog` as above. Keep it simple: read from `Game.Instance` (consistent with the existing `_gm`). Remove the old `var gate = GateMod.Quantize(sp.gate.Effective);` line entirely.

Update `EmitTransitions` to take the feint flag and use it instead of `cooldown`:
```csharp
    static void EmitTransitions(ulong id, ServerPlayer sp, AttackDefinition def, uint tick, bool feinted)
    {
        var prev = sp.prevAttackPhase; var now = sp.attackState.phase;
        if (prev == now && !feinted) return;
        sp.pendingEvents ??= new System.Collections.Generic.Queue<AttackEvent>();
        if (prev == AttackPhase.Idle && now == AttackPhase.Anticipation)
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Started, sp, tick));
        if (now == AttackPhase.Hit)
        {
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Struck, sp, tick));
            OnStrike(id, sp, def, tick);
        }
        if (feinted)
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Feinted, sp, tick));
    }
```
(`ApplyDamage` is added in Task 8; for this task either add a one-line `static void ApplyDamage(ServerPlayer sp, int dmg) => sp.hp = Mathf.Max(0, sp.hp - dmg);` stub or inline `sp.hp -= dmg`. Prefer the stub so Task 8 just adds the clamp+log.)

- [ ] **Step 6: `PredictionSystem` — derive gate from the predicted `StatusState`**

In `Assets/_Scripts/Net/PredictionSystem.cs`: add a field and accessor, change both tick methods.

Add field + property:
```csharp
    public StatusState Status { get; } = new();   // owner-predicted effects; reduces to the gate, adopted/merged on snapshot (Task 11)
```
Add `defs` helper:
```csharp
    static StatusEffectDef[] Defs() { var c = Game.Instance != null ? Game.Instance.StatusCatalog : null; return c != null ? c.Defs : System.Array.Empty<StatusEffectDef>(); }
```
Replace `FixedTick(GateMod gate, float dt)` — it no longer takes a gate; it steps status and derives it:
```csharp
    public void FixedTick(float dt)
    {
        var cfg = Game.Instance.MovementCfg;
        GateMod gate = GateMod.Quantize(StatusLogic.Step(Status, Defs(), out _));
        Vector2 raw = WasdInput.Read();
        Vector2 move = InstanceStep.FreeMove(raw, gate);
        Tick++;
        prevPos = Pos;
        Vector2 stepped = MovementStep.Step(Pos, move, dt, cfg.moveSpeed, Walkable);
        Pos = ResolveSelfCollision(stepped, prevPos);
        buffer.Store(new InputFrame { tick = Tick, input = move, predictedPos = Pos });
        ReplicationHub.Instance.SubmitInputTickRpc(new InputCommand { tick = Tick, rawMove = raw });
    }
```
Replace `FixedTickInstance(...)` — drop the `GateMod gate` parameter; the gate is derived inside `InstanceStep`:
```csharp
    public void FixedTickInstance(ref AttackState atk, AttackIntent attack, byte weaponId, AttackTimeline tl, PhaseScales scales, float dt)
    {
        var cfg = Game.Instance.MovementCfg;
        Vector2 rawMove = WasdInput.Read();
        ushort aimQ = AimQuant.Encode(attack.aimDir);
        attack.aimDir = AimQuant.Decode(aimQ);
        Tick++;
        prevPos = Pos;
        Vector2 p = Pos;
        var ctx = new InstanceCtx { timeline = tl, scales = scales, dt = dt, speed = cfg.moveSpeed, walkable = Walkable, defs = Defs() };
        InstanceStep.Step(ref atk, Status, ref p, new InstanceInput { rawMove = rawMove, attack = attack }, ctx, out _);
        Pos = ResolveSelfCollision(p, prevPos);
        GateMod gate = GateMod.Quantize(StatusLogic.Reduce(Status, Defs()));   // for the stored free-move fallback vector
        buffer.Store(new InputFrame { tick = Tick, input = AttackLogic.LungeVelocity(atk, tl) ?? InstanceStep.FreeMove(rawMove, gate), predictedPos = Pos });
        byte bits = (byte)((attack.pressed ? InputCommand.Pressed : 0) | (attack.held ? InputCommand.Held : 0)
                         | (attack.released ? InputCommand.Released : 0) | (attack.feint ? InputCommand.Feint : 0));
        ReplicationHub.Instance.SubmitInputTickRpc(new InputCommand { tick = Tick, rawMove = rawMove, attackBits = bits, aimAngle = aimQ, weaponId = weaponId });
    }
```
> **Determinism caution:** `InstanceStep.Step` already calls `StatusLogic.Step` (which decrements durations). In `FixedTickInstance` above, the extra `StatusLogic.Reduce` after the step is read-only (no decrement) — it just reads the post-step gate for the stored fallback vector. That is correct: the fallback vector is only used when there is no lunge, and `Reduce` does not mutate. Do **not** add a second `StatusLogic.Step` here.

Reset status on activate/deactivate — in `Activate`/`Deactivate`, add `Status.Clear();`:
```csharp
    public void Activate(Vector2 startPos)
    {
        ...
        Status.Clear();
        Active = true;
    }
    public void Deactivate() { Status.Clear(); Active = false; }
```

- [ ] **Step 7: `LocalPlayer` — drop `currentGate`**

In `Assets/_Scripts/Net/LocalPlayer.cs`:
- Remove the `GateMod currentGate = GateMod.None;` field.
- In `FixedUpdate`, the two `currentGate = GateMod.None;` assignments on enter/leave → delete (status reset lives in `PredictionSystem.Activate/Deactivate`).
- Update the two prediction calls:
  - `prediction.FixedTickInstance(ref atk, ai, ResolveWeaponId(), currentAttack.Timeline, attack.Scales, Time.fixedDeltaTime);`
  - `prediction.FixedTick(Time.fixedDeltaTime);`
- In `OnSnapshot`, remove the line `if ((entries[i].flags & SnapshotEntry.InInstanceBit) != 0) currentGate = GateMod.Unpack(entries[i].gate);` (the status block replaces this in Task 11; until then the owner predicts from its own `Status`).

- [ ] **Step 8: Migrate `GateTests` to the new signatures**

In `Assets/Tests/EditMode/InstanceSim/GateTests.cs`:
- Delete the four `AbilityGate` tests (`Empty_ReducesToNone`, `OverlappingSlows_Multiply`, `IndependentBlocks_Compose`, `ClearingOneSource_LeavesTheOther`) — these are now covered by `StatusLogicTests` (Task 2). Keep `None_IsFullyEnabled` + `PackUnpack_RoundTrips` (GateMod wire).
- In `GatedAttack_InterruptsInProgress`, remove the `Assert.AreEqual(0f, s.cooldown, 1e-4f);` line (field removed in Task 5; it is already never set after Task 4 Step 2).
- Replace the `InstanceCtx Ctx(...)` helper + the three InstanceStep gating tests so the gate comes from a `StatusState`:

```csharp
    static StatusEffectDef[] Defs() => new[]
    {
        new StatusEffectDef { id = 0, blocksMove = true, blocksAttack = true, moveScale = 0f, durationTicks = 100, policy = StackPolicy.Refresh, maxStacks = 1 }, // root+silence
        new StatusEffectDef { id = 1, moveScale = 0.5f, durationTicks = 100, policy = StackPolicy.Refresh, maxStacks = 1 },                                       // slow 0.5
        new StatusEffectDef { id = 2, blocksMove = true, blocksAttack = true, moveScale = 0f, durationTicks = 100, policy = StackPolicy.Refresh, maxStacks = 1 }, // paralyze
    };

    static InstanceCtx Ctx(AttackTimeline tl) => new InstanceCtx
    {
        timeline = tl, scales = PhaseScales.One, dt = 0.02f, speed = 4f, walkable = _ => true, defs = Defs(),
    };

    static InstanceInput Move(Vector2 m) => new InstanceInput { rawMove = m, attack = new AttackIntent { aimDir = new Vector2(1, 0) } };

    [Test]
    public void BlockedMove_DoesNotWalk()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero; var st = new StatusState();
        StatusLogic.Apply(st, Defs()[0], 0u, false);   // root
        InstanceStep.Step(ref atk, st, ref pos, Move(new Vector2(1, 0)), Ctx(tl), out _);
        Assert.AreEqual(0f, pos.x, 1e-4f);
    }

    [Test]
    public void MoveScale_HalvesDistance()
    {
        var tl = Tl();
        var fa = default(AttackState); Vector2 full = Vector2.zero;
        InstanceStep.Step(ref fa, new StatusState(), ref full, Move(new Vector2(1, 0)), Ctx(tl), out _);
        var ha = default(AttackState); Vector2 half = Vector2.zero; var st = new StatusState();
        StatusLogic.Apply(st, Defs()[1], 0u, false);   // slow 0.5
        InstanceStep.Step(ref ha, st, ref half, Move(new Vector2(1, 0)), Ctx(tl), out _);
        Assert.AreEqual(full.x * 0.5f, half.x, 1e-3f);
    }

    [Test]
    public void GatedAttack_InterruptsLunge_AndParalyzeRoots()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero; var none = new StatusState();
        InstanceStep.Step(ref atk, none, ref pos, new InstanceInput { attack = new AttackIntent { pressed = true, held = true, aimDir = new Vector2(1, 0) } }, Ctx(tl), out _);
        InstanceStep.Step(ref atk, none, ref pos, new InstanceInput { attack = new AttackIntent { released = true, aimDir = new Vector2(1, 0) } }, Ctx(tl), out _);
        for (int i = 0; i < 10 && atk.phase != AttackPhase.Hit; i++)
            InstanceStep.Step(ref atk, none, ref pos, Move(Vector2.zero), Ctx(tl), out _);
        Assert.AreEqual(AttackPhase.Hit, atk.phase);

        float before = pos.x;
        var st = new StatusState(); StatusLogic.Apply(st, Defs()[2], 0u, false);   // paralyze
        InstanceStep.Step(ref atk, st, ref pos, Move(Vector2.zero), Ctx(tl), out _);
        Assert.AreEqual(AttackPhase.Idle, atk.phase);
        Assert.AreEqual(before, pos.x, 1e-4f);
    }
```
Also delete the now-unused `using` of nothing special; `GateTests` still uses `UnityEngine`.

- [ ] **Step 9: Compile + tests + Play**

1. `refresh_unity` `scope=all`; poll; `read_console` clean (this is the big cross-assembly change — expect to fix stragglers: any remaining `currentGate`, `sp.gate` on the sim path, old `InstanceStep.Step`/`FixedTick`/`FixedTickInstance` call shapes). `DebugLocalGate`/`CommandBootstrap` still compile (they touch `sp.gate`, which still exists).
2. `run_tests` EditMode `Minifantasy.InstanceSim.Tests` → expect PASS (StatusLogicTests + migrated GateTests).
3. Enter Play; `read_console` clean; screenshot. (Single-player boot; the debug `gate` console command is now **inert** — expected, fixed in Task 6.)

- [ ] **Step 10: Commit**

```bash
git add "Assets/_Scripts/InstanceSim/InstanceStep.cs" "Assets/_Scripts/Combat/Core/AttackLogic.cs" "Assets/_Scripts/Combat/Core/AttackTimeline.cs" "Assets/_Scripts/Combat/AttackDefinition.cs" "Assets/_Scripts/Net/PlayerRegistry.cs" "Assets/_Scripts/Net/AttackSimSystem.cs" "Assets/_Scripts/Net/PredictionSystem.cs" "Assets/_Scripts/Net/LocalPlayer.cs" "Assets/Tests/EditMode/InstanceSim/GateTests.cs"
git commit -m "InstanceSim/Net: drive the action gate from per-player StatusState (server + owner predict)"
```

---

### Task 5: Remove `AttackState.cooldown`; finalize feint → AttackCooldown

The cooldown field is already unwritten (Task 4 Step 2) and the new effect is applied (Task 4 Step 1). This task deletes the dead field and its last readers.

**Files:**
- Modify: `Assets/_Scripts/Combat/Core/AttackTypes.cs` (remove field)
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs` (stop packing cooldown into `selfExtra`)
- Modify: `Assets/Tests/EditMode/Combat/AttackLogicTests.cs` (feint test)

- [ ] **Step 1: Update the feint unit test (red first)**

In `Assets/Tests/EditMode/Combat/AttackLogicTests.cs`, replace `Feint_EntersCooldown_AndIdle` (it asserts `s.cooldown == 0.5f`, which no longer exists) with a behavior test that the feint goes Idle and does **not** auto-start a new attack (the cooldown now lives in the status layer, tested in `StatusLogicTests`):

```csharp
    [Test]
    public void Feint_GoesIdle()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, Feint(new Vector2(1, 0)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.Idle, s.phase);
    }
```

- [ ] **Step 2: Remove the field**

In `Assets/_Scripts/Combat/Core/AttackTypes.cs`, delete:
```csharp
    public float cooldown;      // feint lockout remaining
```

- [ ] **Step 3: Fix the last reader (`ReplicationHub` `selfExtra`)**

`selfExtra` currently packs `windupComplete | cooldown<<1`. `cooldown` is gone, and the whole `selfExtra` field is **write-only on the wire — nothing on the client reads it** (verified: only written at `ReplicationHub.cs:116`, serialized at `SnapshotEntry.cs:33`, never deserialized/consumed). Task 10 removes `selfExtra` entirely. For now, just stop referencing `cooldown` so it compiles — in `Assets/_Scripts/Net/ReplicationHub.cs` replace the `entry.selfExtra = ...` line with:
```csharp
                    entry.selfExtra = (byte)(st.windupComplete ? 1 : 0);
```

- [ ] **Step 4: Compile + tests + Play**

1. `refresh_unity` `scope=all`; poll; `read_console` clean (grep-style: ensure no remaining `.cooldown` references — `mcp__unity-mcp__find_in_file` or rely on the compiler error list).
2. `run_tests` EditMode `Minifantasy.Combat.Tests` **and** `Minifantasy.InstanceSim.Tests` → PASS.
3. Play; console clean; screenshot.

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/Combat/Core/AttackTypes.cs" "Assets/_Scripts/Net/ReplicationHub.cs" "Assets/Tests/EditMode/Combat/AttackLogicTests.cs"
git commit -m "Combat: remove AttackState.cooldown (feint lockout is now the AttackCooldown status effect)"
```

---

### Task 6: Remove `AbilityGate`; repurpose the debug console command to apply effects

**Files:**
- Delete: `Assets/_Scripts/InstanceSim/AbilityGate.cs` (+ `.meta`)
- Modify: `Assets/_Scripts/Net/PlayerRegistry.cs` (remove `gate`)
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs` (remove `DebugLocalGate`; add `DebugLocalStatus`)
- Modify: `Assets/_Scripts/CommandBootstrap.cs` (`gate` → `effect` command)

- [ ] **Step 1: Remove the `AbilityGate` field + the stale wire pack**

In `Assets/_Scripts/Net/PlayerRegistry.cs`, delete:
```csharp
    public readonly AbilityGate gate = new();   // source-keyed move/attack gates; only the quantized Effective replicates
```
In `Assets/_Scripts/Net/ReplicationHub.cs`, delete the now-dead self-gate pack line (it references `sp.gate`):
```csharp
                if ((flags & SnapshotEntry.InInstanceBit) != 0) entry.gate = GateMod.Pack(sp.gate.Effective);
```
(`SnapshotEntry.gate` the field is removed in Task 10; leaving it unset = default 0 until then.)

- [ ] **Step 2: Replace `DebugLocalGate` with `DebugLocalStatus`**

In `Assets/_Scripts/Net/ReplicationHub.cs`:
```csharp
    // Debug seam (host/server only): the local player's authoritative StatusState, for console testing. Null on a
    // pure client — effects are server-authoritative.
    public StatusState DebugLocalStatus()
    {
        var nm = NetworkManager.Singleton;
        if (!IsServer || nm == null) return null;
        return registry.TryGet(nm.LocalClientId, out var sp) ? sp.status : null;
    }
```

- [ ] **Step 3: Rewrite the console command (`gate` → `effect`)**

In `Assets/_Scripts/CommandBootstrap.cs`, replace the whole `gate` command registration (the block using `GateSlow/GateRoot/...` and `DebugLocalGate()`) with an `effect` command that applies catalog effects to the host's own player. Remove the `GateSlow/GateRoot/GateSilence/GateStop` consts.

```csharp
        Register(new Command
        {
            Keyword = "effect", Scope = CommandScope.Instance, Arg = ArgMode.Optional,
            Description = "(debug) Apply/clear a status effect on yourself to test the framework.",
            Usage = "effect [hitstun|cooldown|poison|freeze|slow|list|clear]",
            Run = (args, _) =>
            {
                var hub = ReplicationHub.Instance;
                var status = hub != null ? hub.DebugLocalStatus() : null;
                if (status == null) return CommandResult.Bad("Effects are host-only for now — run this on the host, in a dungeon.");
                var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
                if (cat == null) return CommandResult.Bad("No StatusCatalog wired on Game.");
                string which = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "list";
                if (which == "list")
                {
                    var sb = new System.Text.StringBuilder("Active: ");
                    for (int i = 0; i < status.count; i++) sb.Append((StatusKind)status.effects[i].defId).Append('(').Append(status.effects[i].remainingTicks).Append("t x").Append(status.effects[i].stacks).Append(") ");
                    if (status.count == 0) sb.Append("none");
                    return CommandResult.Ok(sb.ToString(), keepOpen: true, output: OutputType.System);
                }
                if (which == "clear") { status.Clear(); return CommandResult.Ok("Effects cleared.", keepOpen: true, output: OutputType.System); }
                if (!System.Enum.TryParse<StatusKind>(MapName(which), true, out var kind))
                    return CommandResult.Bad("Usage: effect [hitstun|cooldown|poison|freeze|slow|list|clear]");
                int id = (int)kind;
                if (id >= cat.Defs.Length) return CommandResult.Bad("Catalog missing that effect — run Tools > Combat > Build Status Catalog.");
                int dur = kind == StatusKind.AttackCooldown ? 30 : -1;   // cooldown has no inherent duration
                StatusLogic.Apply(status, cat.Defs[id], 0u, self: kind == StatusKind.AttackCooldown, durationOverride: dur);
                return CommandResult.Ok($"Applied {kind}.", keepOpen: true, output: OutputType.System);
            }
        });
```
Add the small name mapper near the other helpers in `CommandBootstrap`:
```csharp
    static string MapName(string s) => s == "cooldown" ? "AttackCooldown" : s;
```
(If `CommandBootstrap` lives in the default assembly and can see `StatusKind`/`StatusLogic`/`StatusState` — it can: those are in `Minifantasy.Combat`/`Minifantasy.InstanceSim`, both auto-referenced by `Assembly-CSharp`. The **Commands framework** asmdef is the one you must not touch; `CommandBootstrap` is game glue in the default assembly, which is correct.)

- [ ] **Step 4: Compile + tests + Play**

1. `refresh_unity` `scope=all`; poll; `read_console` clean (ensure no remaining `AbilityGate` / `DebugLocalGate` / `sp.gate` references; the `GateTests` AbilityGate tests were already removed in Task 4).
2. `run_tests` EditMode all assemblies green.
3. Play; open the console; run `effect freeze` then `effect list` then `effect clear` on the host **in a dungeon** (enter an instance first). Expect `Applied Freeze.` / a populated `Active:` line / `Effects cleared.` Screenshot the console output.

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/InstanceSim/AbilityGate.cs" "Assets/_Scripts/InstanceSim/AbilityGate.cs.meta" "Assets/_Scripts/Net/PlayerRegistry.cs" "Assets/_Scripts/Net/ReplicationHub.cs" "Assets/_Scripts/CommandBootstrap.cs"
git commit -m "InstanceSim/Net: remove AbilityGate (superseded by StatusState); debug 'effect' console command"
```

---

## Phase 3 — Health + on-hit effects + hit query

### Task 7: On-hit data on attacks (damage + effect list + hit geometry)

**Files:**
- Modify: `Assets/_Scripts/Combat/Core/AttackTimeline.cs`
- Modify: `Assets/_Scripts/Combat/AttackDefinition.cs`

- [ ] **Step 1: Add the on-hit struct + fields to `AttackTimeline`**

```csharp
// add to AttackTimeline.cs (top-level, same file or AttackTypes.cs — keep with AttackTimeline)
[System.Serializable]
public struct OnHitEffect { public StatusKind kind; public int magnitude; }   // magnitude: stacks or override (kind-specific)
```
Add to the `AttackTimeline` class:
```csharp
    public int damage;            // HP removed per strike
    public float hitRange;        // broadphase radius from the attacker
    public float hitArcCos;       // cos(halfArc); a victim must be within this of lockedAim
    public OnHitEffect[] onHit;   // effects applied to each victim on a strike (e.g., HitStun, Poison)
```

- [ ] **Step 2: Author the fields on `AttackDefinition` + build them**

Add to `AttackDefinition` (under `[Header("Rules")]`):
```csharp
    [Header("On hit")]
    public int damage = 10;
    public float hitRange = 1.0f;
    [Range(0f, 180f)] public float hitArcDegrees = 90f;   // half-arc each side of aim
    public OnHitEffect[] onHit = new[] { new OnHitEffect { kind = StatusKind.HitStun, magnitude = 1 } };
```
In `BuildTimeline()`, add:
```csharp
            damage = damage,
            hitRange = hitRange,
            hitArcCos = Mathf.Cos(hitArcDegrees * Mathf.Deg2Rad),
            onHit = onHit,
```

- [ ] **Step 3: Compile-gate**

`refresh_unity` `scope=all`; poll; `read_console` clean. (Additive — no behavior yet; `OnStrike` consumes these in Task 9.) Existing `AttackDefinition` assets get the defaults (`damage=10`, one HitStun on-hit).

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Combat/Core/AttackTimeline.cs" "Assets/_Scripts/Combat/AttackDefinition.cs"
git commit -m "Combat: on-hit data on attacks (damage, hit range/arc, status effect list)"
```

---

### Task 8: HP pool (init on instance enter; DOT applied; clamp + log)

**Files:**
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs` (`EnterInstanceRpc`: init hp)
- Modify: `Assets/_Scripts/Net/AttackSimSystem.cs` (`ApplyDamage` clamp + death log; clear status on the no-weapon/normal paths already done in Task 4)

- [ ] **Step 1: Initialize HP on instance entry; clear effects on exit**

In `Assets/_Scripts/Net/ReplicationHub.cs` `EnterInstanceRpc`, after `sp.inInstance = true;` add:
```csharp
        var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        sp.hp = cat != null ? cat.maxHp : 100;
        sp.status.Clear();
```
In `LeaveInstanceRpc`, after `sp.inInstance = false;` add:
```csharp
        sp.status.Clear();
```
(Also reset `sp.attackState = default;` in both if not already — prevents stale combat state across runs. Check current code; add if missing.)

- [ ] **Step 2: Finalize `ApplyDamage` (clamp + one-shot death log)**

In `Assets/_Scripts/Net/AttackSimSystem.cs`, replace the Task-4 stub with:
```csharp
    static void ApplyDamage(ServerPlayer sp, int dmg)
    {
        if (dmg <= 0 || sp.hp <= 0) return;
        sp.hp = Mathf.Max(0, sp.hp - dmg);
        if (sp.hp == 0) Debug.Log("[combat] player reached 0 HP (no death/respawn yet)");
    }
```
Ensure the no-weapon branch (Task 4 Step 5) routes DOT through it too: change `if (dmg > 0) sp.hp -= dmg;` to `ApplyDamage(sp, dmg);`.

- [ ] **Step 3: Compile + Play smoke (DOT via console)**

1. `refresh_unity` `scope=all`; poll; `read_console` clean.
2. Play as host; enter a dungeon; console `effect poison`; watch the server log — with no HP readout yet (Task 14), verify via a temporary check: console `effect list` shows Poison ticking down. (Real HP-drain verification lands with the HUD in Task 14 + the manual host test.) Screenshot.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Net/ReplicationHub.cs" "Assets/_Scripts/Net/AttackSimSystem.cs"
git commit -m "Net: server HP pool (init on instance enter, DOT applied, clamp at 0)"
```

---

### Task 9: Broadphase hit query → damage + effects at `OnStrike`

**Files:**
- Modify: `Assets/_Scripts/Net/AttackSimSystem.cs` (`OnStrike` + registry access)

- [ ] **Step 1: Give `OnStrike` the registry + apply on-hit to victims**

`OnStrike` currently only logs. Make `StepInstanceFixed` stash the registry (mirror the existing `_gm` static) so `OnStrike` can sweep roommates. At the top of `StepInstanceFixed` add `_reg = reg;` and add a static field `static PlayerRegistry _reg;`.

Replace `OnStrike`:
```csharp
    // Broadphase hit query (PLACEHOLDER for the deferred pixel narrowphase): same-region players within the
    // weapon's range + forward arc (from lockedAim) take damage + the weapon's on-hit effects. Server-only.
    static void OnStrike(ulong id, ServerPlayer sp, AttackDefinition def, uint tick)
    {
        if (_reg == null) return;
        var tl = def.Timeline;
        var statusCat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        var defs = statusCat != null ? statusCat.Defs : null;
        Vector2 origin = sp.worldPos;
        Vector2 aim = sp.attackState.lockedAim.sqrMagnitude > 1e-6f ? sp.attackState.lockedAim.normalized : Vector2.right;
        float range2 = tl.hitRange * tl.hitRange;
        foreach (var kv in _reg.Players)
        {
            if (kv.Key == id) continue;
            var victim = kv.Value;
            if (!victim.inInstance || victim.regionKey != sp.regionKey) continue;
            Vector2 to = victim.worldPos - origin;
            if (to.sqrMagnitude > range2 || to.sqrMagnitude < 1e-6f) continue;
            if (Vector2.Dot(to.normalized, aim) < tl.hitArcCos) continue;     // outside the forward arc
            ApplyDamage(victim, tl.damage);
            if (defs != null && tl.onHit != null)
                foreach (var oh in tl.onHit)
                {
                    int idx = (int)oh.kind;
                    if (idx < defs.Length) StatusLogic.Apply(victim.status, defs[idx], tick, self: false,
                        durationOverride: oh.kind == StatusKind.AttackCooldown ? 30 : -1);
                }
            Debug.Log($"[attack] HIT {id} -> {kv.Key} dmg={tl.damage} hp={victim.hp} effects={(tl.onHit != null ? tl.onHit.Length : 0)}");
        }
    }
```

- [ ] **Step 2: Compile-gate**

`refresh_unity` `scope=all`; poll; `read_console` clean.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/Net/AttackSimSystem.cs"
git commit -m "Net: broadphase hit query at OnStrike -> damage + on-hit status effects (hitstun)"
```

(End-to-end hit→stun is only observable across two clients with rendering — verified in the Phase 5 manual host test.)

---

## Phase 4 — Replication + owner prediction/reconcile

### Task 10: `SnapshotEntry` wire redesign (status block + effect mask + HP)

**Files:**
- Modify: `Assets/_Scripts/Net/SnapshotEntry.cs`
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs` (`SendSnapshots` entry build)

- [ ] **Step 1: Rewrite `SnapshotEntry`**

Replace flags + the `gate`/`selfExtra` fields with: HP for any in-instance member, a full status block for the self entry, and an effect-type mask for remotes.

```csharp
// Assets/_Scripts/Net/SnapshotEntry.cs
using Unity.Netcode;

// One player in a client's snapshot. flags select optional blocks. Self entry (the viewer's own) carries the
// full status block (for owner prediction); remote in-instance entries carry a compact effect mask (cosmetic) +
// HP. Attack block (pose) unchanged. Animation/facing are derived on the client from interpolated motion.
public struct SnapshotEntry : INetworkSerializable
{
    public ulong id;
    public float x, y;
    public byte flags;             // bit0 snap; bit1 self; bit2 attacking; bit3 inInstance(member)

    // attack block (AttackingBit)
    public byte weaponId;
    public byte pose;
    public ushort residual;

    // in-instance member (InstanceBit)
    public ushort hp;
    public byte effectMask;        // remotes: one bit per active StatusKind

    // self block (SelfBit): authoritative active effects for owner prediction
    public byte effectCount;
    public byte[] effDefId;
    public ushort[] effRemaining;
    public byte[] effStacks;

    public const byte SnapBit = 1, SelfBit = 2, AttackingBit = 4, InstanceBit = 8;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref x);
        s.SerializeValue(ref y);
        s.SerializeValue(ref flags);
        if ((flags & InstanceBit) != 0) s.SerializeValue(ref hp);
        if ((flags & SelfBit) != 0)
        {
            s.SerializeValue(ref effectCount);
            if (s.IsReader) { effDefId = new byte[effectCount]; effRemaining = new ushort[effectCount]; effStacks = new byte[effectCount]; }
            for (int i = 0; i < effectCount; i++)
            {
                s.SerializeValue(ref effDefId[i]);
                s.SerializeValue(ref effRemaining[i]);
                s.SerializeValue(ref effStacks[i]);
            }
        }
        else if ((flags & InstanceBit) != 0)
        {
            s.SerializeValue(ref effectMask);
        }
        if ((flags & AttackingBit) != 0)
        {
            s.SerializeValue(ref weaponId);
            s.SerializeValue(ref pose);
            s.SerializeValue(ref residual);
        }
    }
}
```
> **Netcode note:** per-element `SerializeValue` in a loop (with reader-side array alloc) is the established pattern for variable-length blocks here; the existing snapshot already round-trips arrays of `SnapshotEntry`. The self block is only on one entry per snapshot (small).

- [ ] **Step 2: Build the new entry fields in `ReplicationHub.SendSnapshots`**

In the `foreach (var id in visibleNow)` loop, replace the flags/gate/attack lines with:

```csharp
                var sp = registry.Players[id];
                byte flags = 0;
                if (sp.snap) flags |= SnapshotEntry.SnapBit;
                bool self = id == viewer && sp.inInstance;
                if (self) flags |= SnapshotEntry.SelfBit;
                if (sp.inInstance) flags |= SnapshotEntry.InstanceBit;
                var entry = new SnapshotEntry { id = id, x = sp.worldPos.x, y = sp.worldPos.y, flags = flags };
                if (sp.inInstance) entry.hp = (ushort)Mathf.Max(0, sp.hp);
                if (self)
                {
                    int n = sp.status.count;
                    entry.effectCount = (byte)n;
                    entry.effDefId = new byte[n]; entry.effRemaining = new ushort[n]; entry.effStacks = new byte[n];
                    for (int k = 0; k < n; k++)
                    {
                        entry.effDefId[k] = sp.status.effects[k].defId;
                        entry.effRemaining[k] = (ushort)Mathf.Max(0, sp.status.effects[k].remainingTicks);
                        entry.effStacks[k] = sp.status.effects[k].stacks;
                    }
                }
                else if (sp.inInstance)
                {
                    entry.effectMask = StatusLogic.ActiveMask(sp.status);
                }
                if (sp.inInstance && AttackLogic.IsAttacking(sp.attackState.phase))
                {
                    var st = sp.attackState;
                    entry.flags |= SnapshotEntry.AttackingBit;
                    entry.weaponId = sp.weaponId;
                    entry.pose = AttackPose.Pack(st.phase, st.frameIndex, st.dirIndex);
                    entry.residual = AimQuant.Encode(st.lockedAim);
                }
                entryScratch.Add(entry);
```
(The reused-by-length `snapshotBufByLen` buffers still work — entries are value structs with array refs; `CopyTo` copies the refs, fine for synchronous serialize.)

- [ ] **Step 3: Fix the `GhostManager.SelfInInstance` flag read**

`GhostManager.Apply` reads `(e.flags & SnapshotEntry.InInstanceBit)` for `SelfInInstance` — that constant was renamed to `SelfBit`. In `Assets/_Scripts/Net/GhostManager.cs` line ~72 change to:
```csharp
            if (e.id == localId) SelfInInstance = (e.flags & SnapshotEntry.SelfBit) != 0;
```

- [ ] **Step 4: Compile + Play**

1. `refresh_unity` `scope=all`; poll; `read_console` clean (LocalPlayer.OnSnapshot no longer reads `entries[i].gate` after Task 4 Step 7 — confirm no `.gate`/`.selfExtra` references remain).
2. Play host; enter dungeon; console clean; screenshot. (No visible change yet; the owner doesn't consume the block until Task 11.)

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/Net/SnapshotEntry.cs" "Assets/_Scripts/Net/ReplicationHub.cs" "Assets/_Scripts/Net/GhostManager.cs"
git commit -m "Net: snapshot wire carries status block (self) + effect mask (remote) + HP"
```

---

### Task 11: Owner adopts external effects on snapshot (keeps self-predicted)

**Files:**
- Modify: `Assets/_Scripts/Net/PredictionSystem.cs` (adopt method)
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs` (`OnSnapshot` calls adopt; expose self HP)

- [ ] **Step 1: Add `AdoptExternal` to `PredictionSystem`**

The owner predicts its own `Status` forward (Task 4). On each snapshot, re-sync **external** effect kinds to the authoritative block while keeping **self-predicted** kinds (AttackCooldown) untouched.

```csharp
    // Self-predicted kinds are owner-authoritative-by-construction (derived from our own input). Everything else
    // is server-driven; re-sync those to the authoritative block each snapshot.
    static bool SelfPredicted(byte defId) => defId == (byte)StatusKind.AttackCooldown;

    // Replace all external effects with the authoritative set; keep self-predicted instances as-is.
    public void AdoptExternal(byte[] defIds, ushort[] remaining, byte[] stacks, int n)
    {
        // Remove current external effects (compact in place).
        for (int i = 0; i < Status.count; )
        {
            if (!SelfPredicted(Status.effects[i].defId)) Status.effects[i] = Status.effects[--Status.count];
            else i++;
        }
        // Add the authoritative external effects.
        for (int k = 0; k < n; k++)
        {
            if (SelfPredicted(defIds[k])) continue;               // server's copy of our cooldown — keep our prediction
            if (Status.count >= StatusState.Cap) break;
            Status.effects[Status.count++] = new ActiveEffect
            {
                defId = defIds[k], remainingTicks = remaining[k], stacks = stacks[k],
                sincePeriodTick = 0, appliedTick = 0, selfInflicted = false,
            };
        }
    }
```

- [ ] **Step 2: Call it from `LocalPlayer.OnSnapshot`; expose self HP**

In `Assets/_Scripts/Net/LocalPlayer.cs` `OnSnapshot`, inside the `if (entries[i].id == localId)` block, before `Reconcile(...)`:
```csharp
                var e = entries[i];
                if ((e.flags & SnapshotEntry.SelfBit) != 0)
                {
                    prediction.AdoptExternal(e.effDefId, e.effRemaining, e.effStacks, e.effectCount);
                    SelfHp = e.hp;
                }
                bool snap = (e.flags & SnapshotEntry.SnapBit) != 0;
                prediction.Reconcile(new Vector2(e.x, e.y), ackTick, snap);
                return;
```
Add the property to `LocalPlayer`:
```csharp
    public ushort SelfHp { get; private set; }
```

- [ ] **Step 3: Compile + Play**

1. `refresh_unity` `scope=all`; poll; `read_console` clean.
2. Play host; enter dungeon; `effect freeze` on host; confirm (a) you can't move while frozen and (b) it auto-clears after ~1.5s — this exercises the predicted-gate path under an adopted effect (host is also server, so adoption is trivially correct; the real cross-client adoption is the manual test). Screenshot.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Net/PredictionSystem.cs" "Assets/_Scripts/Net/LocalPlayer.cs"
git commit -m "Net: owner adopts external status effects on snapshot, keeps self-predicted cooldown"
```

---

### Task 12: `GhostManager` exposes remote HP + effect mask (for views)

**Files:**
- Modify: `Assets/_Scripts/Net/GhostManager.cs`

- [ ] **Step 1: Store + expose remote HP/mask**

Add to the private `Ghost` class:
```csharp
        public ushort hp;
        public byte effectMask;
```
In `Apply`, in the `if (e.id != localId)` block (remotes), add:
```csharp
                g.hp = e.hp;
                g.effectMask = e.effectMask;
```
Add accessors for the views (Task 13/14):
```csharp
    public byte RemoteEffectMask(ulong id) => ghosts.TryGetValue(id, out var g) ? g.effectMask : (byte)0;
    public ushort RemoteHp(ulong id) => ghosts.TryGetValue(id, out var g) ? g.hp : (ushort)0;
    public System.Collections.Generic.IEnumerable<ulong> GhostIds => ghosts.Keys;
```

- [ ] **Step 2: Compile-gate + commit**

`refresh_unity` `scope=all`; poll; `read_console` clean.
```bash
git add "Assets/_Scripts/Net/GhostManager.cs"
git commit -m "Net: GhostManager stores + exposes remote HP and active-effect mask"
```

---

## Phase 5 — Visuals + manual verification

### Task 13: `StatusView` — tint per active effect

**Files:**
- Create: `Assets/_Scripts/Combat/StatusView.cs`
- Modify: `Assets/_Scripts/Net/GhostManager.cs` (drive remotes)
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs` (drive self)

- [ ] **Step 1: Create `StatusView`**

Visual-only: tints the ghost's `SpriteRenderer`(s) by the highest-priority active effect. Data-driven by `visualId` via a simple switch (v1 — no new child objects, so the player-prefab build tool needs no change).

```csharp
// Assets/_Scripts/Combat/StatusView.cs
using UnityEngine;

// Visual: tints the player rig by active status effects. Driven each frame by GhostManager (remotes) or
// LocalPlayer (self) with the active-effect mask. No logic, no new GameObjects (tint only).
public sealed class StatusView : MonoBehaviour
{
    SpriteRenderer[] sprites;
    Color baseColor = Color.white;

    void Awake() => sprites = GetComponentsInChildren<SpriteRenderer>(true);

    // mask: one bit per StatusKind (see StatusLogic.ActiveMask).
    public void Render(byte mask)
    {
        Color c = baseColor;
        if ((mask & (1 << (int)StatusKind.Freeze)) != 0) c = new Color(0.6f, 0.8f, 1f);       // blue
        else if ((mask & (1 << (int)StatusKind.Poison)) != 0) c = new Color(0.6f, 1f, 0.6f);  // green
        else if ((mask & (1 << (int)StatusKind.HitStun)) != 0) c = new Color(1f, 0.7f, 0.7f); // red flash
        else if ((mask & (1 << (int)StatusKind.Slow)) != 0) c = new Color(0.8f, 0.8f, 0.95f); // dim
        if (sprites == null) return;
        for (int i = 0; i < sprites.Length; i++) if (sprites[i] != null) sprites[i].color = c;
    }
}
```

- [ ] **Step 2: Add `StatusView` to the Ghost + Player prefabs**

The remote rig is `Ghost.prefab`; the self rig is the spawned player. Add a `StatusView` component to `Assets/_Prefabs/Ghost.prefab` (via `manage_gameobject`/`manage_components add_component` on the prefab) and to the player rig. (The player prefab is tool-generated + untracked — if a `StatusView` must be on it, add it in `PlayerPrefabBuildTool` and re-run **Tools > Minifantasy > Build Player Prefab**, then confirm "Body sprite: True" per the prefab-tool memory. The self ghost is actually `Ghost.prefab` too in this architecture — confirm which rig `GhostManager` instantiates for self; v1 it is `ghostPrefab`. So adding `StatusView` to `Ghost.prefab` covers both self and remotes.)

- [ ] **Step 3: Drive it**

In `GhostManager`: cache `StatusView` on the `Ghost` (like `attackView`) and call it in `Update` for remotes:
```csharp
        // in Ghost class: public StatusView statusView;
        // in Apply, where attackView is resolved on creation: g.statusView = go.GetComponent<StatusView>();
        // in Update, for remotes (the !isSelf branch already exists): if (g.statusView != null) g.statusView.Render(g.effectMask);
```
In `LocalPlayer.Update`, drive the self ghost's `StatusView` from the predicted mask:
```csharp
        if (prediction.Active && GhostManager.Instance != null && GhostManager.Instance.SelfGhost != null)
        {
            var sv = GhostManager.Instance.SelfGhost.GetComponent<StatusView>();
            if (sv != null) sv.Render(StatusLogic.ActiveMask(prediction.Status));
        }
```

- [ ] **Step 4: Compile + Play**

1. `refresh_unity` `scope=all`; poll; `read_console` clean.
2. Play host; enter dungeon; `effect freeze` → self turns blue + can't move; `effect poison` → green; `effect clear` → white. Screenshot each.

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/Combat/StatusView.cs" "Assets/_Scripts/Combat/StatusView.cs.meta" "Assets/_Scripts/Net/GhostManager.cs" "Assets/_Scripts/Net/LocalPlayer.cs" "Assets/_Prefabs/Ghost.prefab"
git commit -m "Combat: StatusView effect tint (self + remotes), data-driven by visual mask"
```
(Commit `Ghost.prefab` only if it is tracked and the `StatusView` add is your only change to it; otherwise note it for Ryan.)

---

### Task 14: Health HUD readout

**Files:**
- Create: `Assets/_Scripts/UI/HealthHud.cs`
- Modify: scene (add a HUD object) — or reuse an existing HUD per the scene-objects preference

- [ ] **Step 1: Create the HUD (self HP number; uGUI Text)**

Per the `unity-ui-text-stack` memory, TMP Essentials are NOT imported — use built-in `UnityEngine.UI.Text` with `LegacyRuntime.ttf`, not TMP.

```csharp
// Assets/_Scripts/UI/HealthHud.cs
using UnityEngine;
using UnityEngine.UI;

// Visual: shows the local player's HP (server-authoritative, from the snapshot). Reads LocalPlayer.SelfHp.
public sealed class HealthHud : MonoBehaviour
{
    [SerializeField] Text label;

    void Update()
    {
        if (label == null || LocalPlayer.Instance == null) return;
        bool inInstance = LocalPlayer.Instance.InInstance;
        label.enabled = inInstance;
        if (inInstance) label.text = $"HP {LocalPlayer.Instance.SelfHp}";
    }
}
```

- [ ] **Step 2: Add the HUD object to the scene**

Per the scene-objects preference (pre-place, don't runtime-instantiate): add a `Canvas` (if none) → `Text` (LegacyRuntime.ttf) → an empty GO with `HealthHud` referencing the `Text`. Use `manage_gameobject`/`manage_ui`/`manage_components`; `manage_scene save`. Confirm the `Text` uses `LegacyRuntime.ttf` (not a missing TMP font).

- [ ] **Step 3: Compile + Play**

`refresh_unity` `scope=all`; poll; `read_console` clean. Play host; enter dungeon → "HP 100" shows; `effect poison` → number ticks down over ~3s; leave dungeon → label hides. Screenshot.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/UI/HealthHud.cs" "Assets/_Scripts/UI/HealthHud.cs.meta"
git commit -m "UI: local HP readout (reads server-authoritative SelfHp)"
```
(Scene file: save + commit only if the HUD additions are isolated; otherwise hand off to Ryan to avoid bundling scene WIP.)

---

### Task 15: Manual two-player host verification

**Files:** none (verification only).

- [ ] **Step 1: Build/run two clients (host + join)**

MCP cannot click the IMGUI Host button — run this manually (host in the Editor, a second built/Editor client joins via Relay). Both enter the **same** underworld room.

- [ ] **Step 2: Verify the checklist**

- [ ] **Hit → stun + damage:** Player A attacks into Player B at close range within the aim arc. B flashes red, cannot move/attack for ~0.3s, then recovers. B's HP drops by the weapon `damage` on both screens.
- [ ] **Feint cooldown (predicted):** A holds then feints (RMB). A cannot start a new attack until ~0.5s passes — and this is **instant on A's screen** (no wait-for-server rubberband).
- [ ] **Poison DOT:** apply a poison weapon's on-hit (or `effect poison` on the host) → HP ticks down on cadence on both screens; stacks increase damage.
- [ ] **Freeze / Slow:** `effect freeze` → rooted + blue on both screens; `effect slow` → visibly slower movement; the owner predicts the reduced speed (no constant rubberband), small eased correction only on the transition.
- [ ] **Remote tints:** B sees A's effect tints and vice-versa (driven by the replicated `effectMask`).
- [ ] **Overworld unaffected:** leave the dungeon — no effects, no HP HUD, movement normal.
- [ ] **No regressions:** attack windup/lunge, collision push-apart, and movement prediction still feel as before.

- [ ] **Step 3: Record results**

Note any divergence (esp. the Slow reconcile residual called out in the spec). File follow-ups; do not "fix" by faking. If all pass, the feature is complete.

---

## Self-Review

**1. Spec coverage**
- Data-driven catalog + pure `StatusState`/`StatusLogic` replacing `AbilityGate` → Tasks 1, 2, 3, 6. ✓
- `InstanceStep` steps status + derives gate + applies self-inflicted feint cooldown → Task 4. ✓
- Feint cooldown migrated; `AttackState.cooldown` + wire bits deleted → Tasks 4, 5. ✓
- HP pool (server-auth, init on enter, clamp, no death) → Task 8. ✓
- On-hit effect lists + broadphase hit query → damage + hitstun → Tasks 7, 9. ✓
- Poison DOT into HP → Tasks 2 (logic), 8 (apply), 9 (on-hit) / `effect poison` (debug). ✓
- Replication: self status block + remote mask + HP → Task 10. ✓
- Owner prediction + reconcile (adopt external / keep self) → Tasks 4 (predict forward), 11 (adopt). ✓
- Thin tint visuals (scene-authored, no runtime Instantiate) + HP HUD → Tasks 13, 14. ✓
- Debug `effect` command → Task 6. ✓
- EditMode unit tests + determinism (StatusLogic deterministic ageing/stacking) → Task 2; integration gate tests → Task 4. ✓
- Per-weapon feint cooldown preserved (duration override) → Tasks 3/4 (`feintCooldownTicks`). ✓
- Effect cap 8 + lowest-remaining overflow → Task 1 (Cap) + Task 2 (`ReplaceWeakest`). ✓

*Gap noted & resolved:* the spec's §9.2 mentions "store a copy of StatusState per frame" for replay; this plan uses the simpler Approach-1 path (status evolves forward; reconcile adopts external by kind, position replays stored vectors) — no per-frame status copy needed because v1 external effects mostly zero movement and self-inflicted effects are owner-authoritative-by-construction. Documented in Task 4 Step 6 and Task 11. This is a simplification *within* Approach 1, not a scope change; full per-frame rollback remains the deferred Approach 2.

**2. Placeholder scan**
- The one pseudo-line (`cmd.tickPlaceholder()`) in Task 4 Step 1 is explicitly flagged with the exact replacement (`0u`) in the following note. No other TBD/TODO/"handle edge cases" present; every code step shows complete code.

**3. Type consistency**
- `StatusKind`/`StackPolicy`/`StatusEffectDef`/`ActiveEffect`/`StatusState` defined in Task 1, used identically in Tasks 2, 4, 6, 9, 10, 11, 13.
- `StatusLogic.Reduce/Step/Apply/ActiveMask/Remove` signatures match across Tasks 2, 4, 6, 9, 11, 13 (e.g. `Apply(StatusState, in StatusEffectDef, uint, bool, float=1, int=-1)`).
- `InstanceStep.Step(ref AttackState, StatusState, ref Vector2, in InstanceInput, in InstanceCtx, out InstanceResult)` consistent in Task 4 (def), AttackSimSystem, PredictionSystem, GateTests.
- `InstanceCtx` has `defs` (not `gate`) everywhere after Task 4; `InstanceResult { feinted, periodicDamage }` consistent.
- `SnapshotEntry` flags `SnapBit/SelfBit/AttackingBit/InstanceBit` + fields (`hp/effectMask/effectCount/effDefId/effRemaining/effStacks`) consistent across Task 10 (def), ReplicationHub, GhostManager (`SelfBit` rename fixed in Task 10 Step 3), LocalPlayer (Task 11).
- `StatusCatalog.Defs`/`maxHp`/`Entry` consistent across Tasks 3, 4, 6, 8, 9.
- `AttackTimeline.feintCooldownTicks` (Task 4) and `damage/hitRange/hitArcCos/onHit` (Task 7) consistent with consumers in InstanceStep / OnStrike.
- `LocalPlayer.SelfHp` (Task 11) consumed by `HealthHud` (Task 14); `GhostManager.RemoteHp/RemoteEffectMask` (Task 12) available for views.

Fixes applied inline: none required beyond the flagged `tickPlaceholder` note.

---

## Execution Handoff

(Provided after save — see chat.)
