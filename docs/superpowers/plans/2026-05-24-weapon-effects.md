# Weapon Status Effects + 3-Layer Effect Visuals — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let each weapon carry on-hit status effects (Bleed/Fire DoT, Fear forced-flee) defined as reusable ScriptableObject assets, with three cosmetic visual layers (attacker swing overlay, victim hit one-shot, victim over-time tick), owner-predicted Fear, and a source-agnostic apply seam ready for future spells/abilities.

**Architecture:** Effects become `StatusEffectAsset` SO assets (gameplay + visual data); `StatusCatalog` holds an ordered array of them and still compiles the pure `StatusEffectDef[]` the deterministic `InstanceSim` consumes. Weapons reference effects via an array; `OnStrike` applies them through a pure `CombatEffects.ApplyEffect` seam. Fear adds a `fleeDir` to `ActiveEffect`, a forced-move override in `InstanceStep`, and is owner-predicted via one wired ushort. Visuals ride the already-replicated effect mask using their own SpriteRenderers.

**Tech Stack:** Unity (URP 2D), C#, Unity Netcode for GameObjects, NUnit EditMode tests, unity-mcp for Editor control.

**Spec:** `docs/superpowers/specs/2026-05-24-weapon-effects-design.md`

---

## Conventions for every task

- **Compile check (after editing/creating `.cs`):** `mcp__unity-mcp__refresh_unity` with `scope=all` (REQUIRED when new files are added — `scope=scripts` won't import them), then poll `mcpforunity://editor_state` until `isCompiling=false`, then `mcp__unity-mcp__read_console` and confirm **zero compile errors**. (`execute_code` is broken on this machine — do not use it.)
- **Run EditMode tests:** `mcp__unity-mcp__run_tests` with `mode=EditMode` (optionally `filter=<TestClass>`). Confirm pass count.
- **Play smoke test:** `mcp__unity-mcp__manage_editor` enter Play; `read_console`; screenshot; exit Play.
- **Host behaviour (2-player) cannot be automated** — MCP can't click the IMGUI Host button. Where a task says "host-verify", list what to check and hand it to the user; do not block the plan on it.
- **Commit** after each task with the message shown. No `git add -A` — add only the listed files (the repo carries unrelated WIP). No attribution lines in commit messages.
- **Do not edit `Assets/_Biomes/*.asset`** (hand-curated). Catalog/effect assets under `_Combat/` are tool-built and fine to edit.

---

# Phase 1 — Data model refactor (no behavior change)

Goal: effects become SO assets; weapons reference an effect array; the apply seam exists; existing combat behaves identically. After Phase 1, weapons apply only HitStun (as before) because no effect assets are assigned yet.

### Task 1.1: Add new effect kinds + forced-move/flee data fields

**Files:**
- Modify: `Assets/_Scripts/Combat/Core/StatusTypes.cs`

- [ ] **Step 1: Add the enum values, the ForcedMoveKind enum, and the new fields**

Replace the top of `StatusTypes.cs` (the `using`-less header + `StatusKind` line) and extend the two structs. The full updated file:

```csharp
// Pure status-effect data: the catalog-resolved def, one active instance, and the per-player collection.
// No GateMod/Netcode/scene refs (StatusLogic, in Minifantasy.InstanceSim, reduces these to a GateMod).
using UnityEngine;

// Well-known effect ids: the value IS the catalog index AND the wire defId. Keep in sync with StatusCatalog order.
public enum StatusKind : byte { HitStun = 0, AttackCooldown = 1, Poison = 2, Freeze = 3, Slow = 4, Bleed = 5, Fire = 6, Fear = 7 }

// How a re-applied effect of the same kind combines with an existing instance.
public enum StackPolicy : byte { Refresh, Stack, Independent }

// Forced movement an effect imposes (overrides WASD in InstanceStep). FleeFrozen = move along a direction frozen at apply.
public enum ForcedMoveKind : byte { None = 0, FleeFrozen = 1 }

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
    public ForcedMoveKind forcedMove;   // None unless this effect drives movement (Fear)
    public float forcedMoveScale;       // forced-move speed as a fraction of moveSpeed
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
    public Vector2 fleeDir;       // frozen flee direction for forced-move effects (quantized); zero otherwise
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

- [ ] **Step 2: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors (new fields default; existing code unaffected).

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/Combat/Core/StatusTypes.cs"
git commit -m "Combat: add Bleed/Fire/Fear kinds + ForcedMove/fleeDir status data fields"
```

### Task 1.2: `StatusLogic.Apply` carries a frozen forced direction

**Files:**
- Modify: `Assets/_Scripts/InstanceSim/StatusLogic.cs`
- Test: `Assets/Tests/EditMode/InstanceSim/StatusForcedMoveTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `Assets/Tests/EditMode/InstanceSim/StatusForcedMoveTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;

public class StatusForcedMoveTests
{
    static StatusEffectDef FearDef() => new StatusEffectDef
    {
        id = (byte)StatusKind.Fear, durationTicks = 60, blocksMove = true, blocksAttack = true,
        moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1,
        forcedMove = ForcedMoveKind.FleeFrozen, forcedMoveScale = 0.8f,
    };

    static StatusEffectDef[] Defs()
    {
        var d = new StatusEffectDef[8];
        d[(int)StatusKind.Fear] = FearDef();
        return d;
    }

    [Test]
    public void Apply_StoresFrozenFleeDir()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Fear], tick: 1, self: false, forcedDir: new Vector2(1, 0));
        Assert.AreEqual(1, s.count);
        Assert.AreEqual(new Vector2(1, 0), s.effects[0].fleeDir);
    }

    [Test]
    public void ActiveForcedMove_ReturnsStoredDirAndScale()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.Fear], tick: 1, self: false, forcedDir: new Vector2(0, -1));
        bool any = StatusLogic.ActiveForcedMove(s, defs, out var dir, out var scale);
        Assert.IsTrue(any);
        Assert.AreEqual(new Vector2(0, -1), dir);
        Assert.AreEqual(0.8f, scale, 1e-4f);
    }

    [Test]
    public void ActiveForcedMove_FalseWhenNoForcedEffect()
    {
        var s = new StatusState();
        Assert.IsFalse(StatusLogic.ActiveForcedMove(s, Defs(), out _, out _));
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `run_tests mode=EditMode filter=StatusForcedMoveTests`. Expected: FAIL (compile error: `Apply` has no `forcedDir` param; `ActiveForcedMove` undefined).

- [ ] **Step 3: Implement**

In `StatusLogic.cs`, change the `Apply` signature and add the `fleeDir` writes; add `ActiveForcedMove`. The updated `Apply`:

```csharp
    // Add or combine per stacking policy. durationOverride >= 0 wins (AttackCooldown passes the weapon's value).
    // forcedDir is the frozen flee direction stored on the instance (forced-move effects only).
    public static void Apply(StatusState s, in StatusEffectDef d, uint tick, bool self, float scale = 1f, int durationOverride = -1, Vector2 forcedDir = default)
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
                s.effects[i].fleeDir = forcedDir;
                return;
            }
        }
        if (s.count >= StatusState.Cap) { if (!ReplaceWeakest(s, dur)) return; }
        s.effects[s.count++] = new ActiveEffect
        {
            defId = d.id, remainingTicks = dur, stacks = 1, sincePeriodTick = 0, appliedTick = tick, selfInflicted = self, fleeDir = forcedDir,
        };
    }

    // The highest-priority active forced-move effect's frozen direction + speed scale (first match wins; v1 has one).
    public static bool ActiveForcedMove(StatusState s, StatusEffectDef[] defs, out Vector2 dir, out float scale)
    {
        for (int i = 0; i < s.count; i++)
        {
            var d = defs[s.effects[i].defId];
            if (d.forcedMove != ForcedMoveKind.None)
            {
                dir = s.effects[i].fleeDir;
                scale = d.forcedMoveScale;
                return true;
            }
        }
        dir = default; scale = 0f; return false;
    }
```

- [ ] **Step 4: Run to verify pass** — `run_tests mode=EditMode filter=StatusForcedMoveTests`. Expected: 3 PASS. Then `run_tests mode=EditMode filter=StatusLogicTests` — existing tests still PASS (the new optional param is source-compatible).

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/InstanceSim/StatusLogic.cs" "Assets/Tests/EditMode/InstanceSim/StatusForcedMoveTests.cs"
git commit -m "Combat: StatusLogic.Apply stores frozen fleeDir + ActiveForcedMove query"
```

### Task 1.3: Create the `StatusEffectAsset` ScriptableObject

**Files:**
- Create: `Assets/_Scripts/Combat/StatusEffectAsset.cs`

- [ ] **Step 1: Create the SO**

```csharp
using UnityEngine;

// One status effect as a reusable asset: gameplay (compiled to the pure StatusEffectDef the sim consumes) plus
// visual data (read ONLY by the View layer — never enters Minifantasy.InstanceSim). Weapons reference these;
// spells/abilities/items will too. Order in StatusCatalog MUST place index i at kind == (StatusKind)i.
[CreateAssetMenu(menuName = "Minifantasy/Status Effect", fileName = "Effect")]
public sealed class StatusEffectAsset : ScriptableObject
{
    [Header("Identity")]
    public StatusKind kind;            // canonical id == catalog index == wire defId == mask bit

    [Header("Gameplay (seconds; compiled to ticks)")]
    public float durationSeconds;
    public bool blocksMove;
    public bool blocksAttack;
    public float moveScale = 1f;       // 1 = none
    public float periodSeconds;        // 0 = no DOT
    public int amountPerTick;
    public StackPolicy policy = StackPolicy.Refresh;
    public byte maxStacks = 1;
    public ForcedMoveKind forcedMove = ForcedMoveKind.None;
    public float forcedMoveScale = 1f; // flee speed as a fraction of moveSpeed

    [Header("Visual (View layer only)")]
    public byte visualId;
    public Color tintColor = Color.white;   // rig tint while active (StatusView); white = no tint
    public Sprite[] hitFx;                   // one-shot on apply, on the victim (StatusFxView Layer B)
    public float hitFps = 14f;
    public Sprite[] tickFx;                  // over-time loop on the victim, pulses at periodSeconds (Layer C)
    public float tickFps = 10f;
    public Sprite[] attackOverlayFx;         // attacker swing overlay (AttackView Layer A)
    public float overlayFps = 14f;

    public StatusEffectDef ToDef() => new StatusEffectDef
    {
        id = (byte)kind,
        durationTicks = Mathf.CeilToInt(durationSeconds * 60f),
        blocksMove = blocksMove,
        blocksAttack = blocksAttack,
        moveScale = moveScale,
        periodTicks = Mathf.CeilToInt(periodSeconds * 60f),
        amountPerTick = amountPerTick,
        policy = policy,
        maxStacks = (byte)Mathf.Max(1, maxStacks),
        visualId = visualId,
        forcedMove = forcedMove,
        forcedMoveScale = forcedMoveScale,
    };
}
```

- [ ] **Step 2: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/Combat/StatusEffectAsset.cs"
git commit -m "Combat: StatusEffectAsset SO (gameplay + visual, compiles to StatusEffectDef)"
```

### Task 1.4: Refactor `StatusCatalog` to an asset array

**Files:**
- Modify: `Assets/_Scripts/Combat/StatusCatalog.cs`

- [ ] **Step 1: Replace the inline `Entry[]` with `StatusEffectAsset[]`**

Full updated file:

```csharp
using UnityEngine;

// Shared byte-id <-> status-effect map (the wire carries defId == StatusKind). Holds the effect ASSETS in
// StatusKind order; Defs compiles the pure StatusEffectDef[] the deterministic sim consumes; Visual() exposes
// the asset (sprites/tint) to the View layer. Wired on Game; built by Tools > Combat > Build Status Catalog.
[CreateAssetMenu(menuName = "Minifantasy/Status Catalog", fileName = "StatusCatalog")]
public sealed class StatusCatalog : ScriptableObject
{
    public int maxHp = 100;
    public StatusEffectAsset[] effects;   // ordered so effects[i].kind == (StatusKind)i

    StatusEffectDef[] _defs;
    public StatusEffectDef[] Defs => _defs ??= Build();

    StatusEffectDef[] Build()
    {
        int n = effects != null ? effects.Length : 0;
        var defs = new StatusEffectDef[n];
        for (int i = 0; i < n; i++) defs[i] = effects[i] != null ? effects[i].ToDef() : default;
        return defs;
    }

    // The effect asset for a defId/mask bit (View layer only). Null if out of range or unassigned.
    public StatusEffectAsset Visual(int defId) =>
        (effects != null && defId >= 0 && defId < effects.Length) ? effects[defId] : null;

    void OnValidate() => _defs = null;   // rebuild after Inspector edits
}
```

- [ ] **Step 2: Compile** — `refresh_unity scope=all` → `read_console`. Expected: errors ONLY from the old builder referencing `catalog.entries`/`StatusCatalog.Entry` (fixed next task). No other file references those (verified: only `StatusCatalogBuilder.cs` did).

- [ ] **Step 3: Commit** (builder fixed in the next task; this is an intermediate compile-broken commit only if needed — otherwise do Tasks 1.4 + 1.5 back-to-back and commit once)

Do Task 1.5 before committing/compiling clean.

### Task 1.5: Rewrite `StatusCatalogBuilder` to create effect assets + assemble

**Files:**
- Modify: `Assets/Editor/StatusCatalogBuilder.cs`

- [ ] **Step 1: Rewrite the builder**

It creates/updates one `StatusEffectAsset` per existing v1 kind under `Assets/_Combat/Effects/`, then assembles them into the catalog in `StatusKind` order. Re-runnable; preserves visual fields a human set in the Inspector by only overwriting gameplay fields on existing assets.

```csharp
using UnityEditor;
using UnityEngine;

// Tools > Combat > Build Status Catalog. Creates/updates the per-effect StatusEffectAsset files and assembles
// the shared StatusCatalog in StatusKind order. Re-running resets GAMEPLAY fields to these defaults but leaves
// VISUAL fields (sprites/tint) alone so hand-authored FX survive. Phase 1 ships the original 5 kinds; Bleed/Fire
// (Phase 2) and Fear (Phase 3) extend the Specs[] table below.
public static class StatusCatalogBuilder
{
    const string CatalogPath = "Assets/_Combat/StatusCatalog.asset";
    const string EffectDir = "Assets/_Combat/Effects";

    struct Spec
    {
        public StatusKind kind; public float dur; public bool bMove, bAtk; public float moveScale;
        public float period; public int perTick; public StackPolicy policy; public byte maxStacks; public byte vis;
        public ForcedMoveKind forced; public float forcedScale;
    }

    static Spec[] Specs() => new[]
    {
        new Spec { kind = StatusKind.HitStun,        dur = 0.3f, bMove = true,  bAtk = true,  moveScale = 0f,   policy = StackPolicy.Refresh, maxStacks = 1, vis = 1 },
        new Spec { kind = StatusKind.AttackCooldown, dur = 0f,   bMove = false, bAtk = true,  moveScale = 1f,   policy = StackPolicy.Refresh, maxStacks = 1, vis = 0 },
        new Spec { kind = StatusKind.Poison,         dur = 3f,   bMove = false, bAtk = false, moveScale = 1f,   period = 0.5f, perTick = 5, policy = StackPolicy.Stack,   maxStacks = 5, vis = 2 },
        new Spec { kind = StatusKind.Freeze,         dur = 1.5f, bMove = true,  bAtk = false, moveScale = 0f,   policy = StackPolicy.Refresh, maxStacks = 1, vis = 3 },
        new Spec { kind = StatusKind.Slow,           dur = 2f,   bMove = false, bAtk = false, moveScale = 0.5f, policy = StackPolicy.Refresh, maxStacks = 1, vis = 4 },
    };

    [MenuItem("Tools/Combat/Build Status Catalog")]
    public static void Build()
    {
        if (!AssetDatabase.IsValidFolder(EffectDir)) AssetDatabase.CreateFolder("Assets/_Combat", "Effects");
        var specs = Specs();
        var assets = new StatusEffectAsset[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            var sp = specs[i];
            string path = $"{EffectDir}/{sp.kind}.asset";
            var a = AssetDatabase.LoadAssetAtPath<StatusEffectAsset>(path);
            bool created = a == null;
            if (created) { a = ScriptableObject.CreateInstance<StatusEffectAsset>(); }
            a.kind = sp.kind;
            a.durationSeconds = sp.dur; a.blocksMove = sp.bMove; a.blocksAttack = sp.bAtk; a.moveScale = sp.moveScale;
            a.periodSeconds = sp.period; a.amountPerTick = sp.perTick; a.policy = sp.policy; a.maxStacks = sp.maxStacks;
            a.visualId = sp.vis; a.forcedMove = sp.forced; a.forcedMoveScale = sp.forcedScale <= 0f ? 1f : sp.forcedScale;
            if (created) AssetDatabase.CreateAsset(a, path); else EditorUtility.SetDirty(a);
            assets[i] = a;
        }

        var catalog = AssetDatabase.LoadAssetAtPath<StatusCatalog>(CatalogPath);
        bool catCreated = catalog == null;
        if (catCreated) catalog = ScriptableObject.CreateInstance<StatusCatalog>();
        catalog.maxHp = 100;
        catalog.effects = assets;
        if (catCreated) AssetDatabase.CreateAsset(catalog, CatalogPath); else EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        Debug.Log($"[StatusCatalog] {(catCreated ? "created" : "updated")} with {assets.Length} effects, maxHp={catalog.maxHp}");
    }
}
```

- [ ] **Step 2: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors.

- [ ] **Step 3: Run the builder** — `mcp__unity-mcp__execute_menu_item` with `Tools/Combat/Build Status Catalog`. Then `read_console` for the `[StatusCatalog] updated with 5 effects` log. Verify `Assets/_Combat/Effects/` now has `HitStun.asset`, `AttackCooldown.asset`, `Poison.asset`, `Freeze.asset`, `Slow.asset`, and `StatusCatalog.asset` references them.

- [ ] **Step 4: Verify Game still wired** — confirm `StatusCatalog.asset` GUID is unchanged (the builder loaded the existing asset), so `Game`'s reference still resolves. `read_console` after entering Play briefly: no "StatusCatalog null" LogError from `Game.Awake`.

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/Combat/StatusCatalog.cs" "Assets/Editor/StatusCatalogBuilder.cs" "Assets/_Combat/StatusCatalog.asset" "Assets/_Combat/Effects"
git commit -m "Combat: StatusCatalog holds StatusEffectAsset[]; builder creates per-effect assets"
```

### Task 1.6: Weapons reference an effect array; add the pure apply seam; reroute `OnStrike`

**Files:**
- Modify: `Assets/_Scripts/Combat/AttackDefinition.cs`
- Modify: `Assets/_Scripts/Combat/Core/AttackTimeline.cs`
- Create: `Assets/_Scripts/InstanceSim/CombatEffects.cs`
- Modify: `Assets/_Scripts/Net/AttackSimSystem.cs`

> **Note (refines spec):** `CombatEffects` lives in `InstanceSim` (not `Net`) because it operates only on `StatusState` + positions + `StatusEffectDef` — keeping it pure makes it unit-testable and still callable by a future spell system (which passes the target's `StatusState`).

- [ ] **Step 1: Replace `OnHitEffect` in `AttackTimeline.cs`**

Full updated file:

```csharp
using UnityEngine;

// One resolved on-hit effect application: which catalog effect (defId) and a per-weapon magnitude scale.
public struct EffectApply { public byte defId; public float scale; }

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
    public int feintCooldownTicks;
    public float aimSnapDegrees;
    public AnimationCurve lungeCurve;

    // On-hit (consumed by AttackSimSystem.OnStrike's broadphase query). HitStun is implicit (always applied).
    public int damage;
    public float hitRange;
    public float hitArcCos;
    public EffectApply[] onHit;   // non-HitStun effects to apply to each victim
    public int hitstunTicks;
    public int hitstunTapTicks;
}
```

- [ ] **Step 2: Update `AttackDefinition.cs` to the effect-asset array**

Replace the `using` line is already present. Replace the `[Header("On hit")]` block and add the `WeaponEffect` struct; update `BuildTimeline` to resolve `onHit`. The relevant edits:

Add near the top (after `using UnityEngine;`):

```csharp
// One authored on-hit effect on a weapon: the effect asset + a per-weapon magnitude scale (default 1).
[System.Serializable]
public struct WeaponEffect { public StatusEffectAsset effect; public float magnitudeScale; }
```

Replace the `[Header("On hit")]` field block:

```csharp
    [Header("On hit")]
    public int damage = 10;
    public float hitRange = 1.0f;
    [Range(0f, 180f)] public float hitArcDegrees = 90f;   // half-arc each side of the locked aim
    public WeaponEffect[] onHitEffects;   // non-HitStun effects (v1: author one); HitStun is always applied
    public float hitstunFullSeconds = 0.45f;
    public float hitstunTapSeconds = 0.2f;
```

In `BuildTimeline`, before the `return new AttackTimeline { ... }`, resolve the effects:

```csharp
        int em = onHitEffects != null ? onHitEffects.Length : 0;
        var applies = new System.Collections.Generic.List<EffectApply>(em);
        for (int i = 0; i < em; i++)
        {
            var e = onHitEffects[i];
            if (e.effect == null) continue;
            applies.Add(new EffectApply { defId = (byte)e.effect.kind, scale = e.magnitudeScale <= 0f ? 1f : e.magnitudeScale });
        }
```

And change the timeline initializer line `onHit = onHit,` to:

```csharp
            onHit = applies.ToArray(),
```

- [ ] **Step 3: Create the apply seam `Assets/_Scripts/InstanceSim/CombatEffects.cs`**

```csharp
using UnityEngine;

// Source-agnostic effect application — the seam weapons (now) and spells/abilities (later) funnel through.
// Pure: operates on a StatusState + positions + def. For forced-move effects it freezes a flee direction away
// from the source, quantized through AimQuant so the server and the owning predictor derive the identical vector.
public static class CombatEffects
{
    public static void ApplyEffect(StatusState target, Vector2 targetPos, Vector2 sourcePos,
                                   in StatusEffectDef def, uint tick, float scale = 1f, int durationOverride = -1)
    {
        Vector2 forcedDir = default;
        if (def.forcedMove == ForcedMoveKind.FleeFrozen)
        {
            Vector2 away = targetPos - sourcePos;
            if (away.sqrMagnitude <= 1e-6f) away = Vector2.right;   // overlapping: deterministic fallback
            forcedDir = AimQuant.Decode(AimQuant.Encode(away));
        }
        StatusLogic.Apply(target, def, tick, self: false, scale: scale, durationOverride: durationOverride, forcedDir: forcedDir);
    }
}
```

- [ ] **Step 4: Reroute `OnStrike` in `AttackSimSystem.cs`**

Replace the effect-application block (currently the `if (defs != null && tl.onHit != null) foreach (var oh in tl.onHit) { ... }` section, around lines 155-164) with HitStun-implicit + seam:

```csharp
            // Always apply charge-scaled HitStun (the base hurt).
            if (defs != null && (int)StatusKind.HitStun < defs.Length)
                StatusLogic.Apply(victim.status, defs[(int)StatusKind.HitStun], tick, self: false, durationOverride: hitstunTicks);
            // The weapon's on-hit effects, through the source-agnostic seam (attacker is the source).
            if (defs != null && tl.onHit != null)
                foreach (var oh in tl.onHit)
                {
                    if (oh.defId >= defs.Length) continue;
                    CombatEffects.ApplyEffect(victim.status, victim.worldPos, origin, defs[oh.defId], tick, scale: oh.scale);
                }
```

(`origin` is already `sp.worldPos` earlier in `OnStrike`. Leave the `Debug.Log("[attack] HIT ...")` line.)

- [ ] **Step 5: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors.

- [ ] **Step 6: Run all EditMode tests** — `run_tests mode=EditMode`. Expected: all existing suites PASS (Combat, InstanceSim, Movement, Aoi, Commands) + StatusForcedMoveTests.

- [ ] **Step 7: Play smoke + host-verify note** — enter Play, `read_console` clean, exit. **Host-verify (hand to user):** in a 2-player host, a strike still applies HitStun (the `[attack] HIT … hitstun=…` log) and the victim flinches; no weapon yet applies an extra effect (all `onHitEffects` empty). Behaviour identical to before.

- [ ] **Step 8: Commit**

```bash
git add "Assets/_Scripts/Combat/AttackDefinition.cs" "Assets/_Scripts/Combat/Core/AttackTimeline.cs" "Assets/_Scripts/InstanceSim/CombatEffects.cs" "Assets/_Scripts/Net/AttackSimSystem.cs"
git commit -m "Combat: weapons reference effect-asset array; OnStrike applies via pure CombatEffects seam (HitStun implicit)"
```

---

# Phase 2 — Bleed + Fire (DoT reskins)

Goal: add the two damage-over-time effects as assets and assign them to weapons. Mechanics reuse the Poison path (already unit-tested), so no new sim logic.

### Task 2.1: Add Bleed + Fire to the catalog builder and rebuild

**Files:**
- Modify: `Assets/Editor/StatusCatalogBuilder.cs`

- [ ] **Step 1: Extend the `Specs()` table** — append after the `Slow` entry:

```csharp
        new Spec { kind = StatusKind.Bleed, dur = 4f,   bMove = false, bAtk = false, moveScale = 1f, period = 0.5f, perTick = 3, policy = StackPolicy.Stack,   maxStacks = 5, vis = 5 },
        new Spec { kind = StatusKind.Fire,  dur = 2.5f, bMove = false, bAtk = false, moveScale = 1f, period = 0.4f, perTick = 6, policy = StackPolicy.Refresh, maxStacks = 1, vis = 6 },
```

- [ ] **Step 2: Compile + rebuild** — `refresh_unity scope=all` → `read_console` clean → `execute_menu_item Tools/Combat/Build Status Catalog` → `read_console` for `updated with 7 effects`. Verify `Bleed.asset` + `Fire.asset` exist and the catalog lists 7.

- [ ] **Step 3: Commit**

```bash
git add "Assets/Editor/StatusCatalogBuilder.cs" "Assets/_Combat/StatusCatalog.asset" "Assets/_Combat/Effects"
git commit -m "Combat: add Bleed (stacking) + Fire (refresh) DoT effect assets"
```

### Task 2.2: Assign Bleed to one weapon, Fire to another

**Files:**
- Modify: `Assets/_Combat/Dagger.asset` (Bleed), `Assets/_Combat/Axe.asset` (Fire) — via the Inspector/MCP, not hand-edited YAML.

- [ ] **Step 1: Assign** — use `mcp__unity-mcp__manage_asset` (or open the asset) to set `Dagger.onHitEffects` = one element `{ effect = Effects/Bleed.asset, magnitudeScale = 1 }`, and `Axe.onHitEffects` = `{ effect = Effects/Fire.asset, magnitudeScale = 1 }`. (Reference each effect asset by GUID.)

- [ ] **Step 2: Rebuild the weapon catalog if needed** — if `WeaponCatalog` caches defs, run `Tools > Combat > Build Weapon Catalog`. Then `refresh_unity scope=all` → `read_console` clean.

- [ ] **Step 3: Play smoke** — enter Play, confirm no errors, exit.

- [ ] **Step 4: Host-verify note (hand to user):** hitting a target with the Dagger logs periodic Bleed damage (HP ticks down 5 times over ~4s, stacking on repeated hits); the Axe applies Fire (faster, refreshes). No visuals yet — verify via the HP HUD + `[attack] HIT` / HP logs.

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Combat/Dagger.asset" "Assets/_Combat/Axe.asset"
git commit -m "Combat: assign Bleed to Dagger, Fire to Axe (data-only)"
```

---

# Phase 3 — Fear (forced flee) + owner prediction

Goal: Fear forces the victim to flee a frozen direction and can't attack; the owner predicts the flee (so it's smooth), via one wired ushort + buffering the applied move.

### Task 3.1: `InstanceStep` applies the forced-move override and reports the move

**Files:**
- Modify: `Assets/_Scripts/InstanceSim/InstanceStep.cs`
- Test: `Assets/Tests/EditMode/InstanceSim/InstanceStepTests.cs`

- [ ] **Step 1: Add the failing test** — append to `InstanceStepTests.cs`:

```csharp
    [Test]
    public void Feared_FleesFrozenDir_OverridesWasd_AndReportsMoveApplied()
    {
        var tl = Tl();
        var defs = new StatusEffectDef[8];
        defs[(int)StatusKind.Fear] = new StatusEffectDef
        {
            id = (byte)StatusKind.Fear, durationTicks = 60, blocksMove = true, blocksAttack = true,
            moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1,
            forcedMove = ForcedMoveKind.FleeFrozen, forcedMoveScale = 0.5f,
        };
        var ctx = new InstanceCtx { timeline = tl, scales = PhaseScales.One, dt = 0.02f, speed = 4f, walkable = _ => true, defs = defs };
        var st = new StatusState();
        StatusLogic.Apply(st, defs[(int)StatusKind.Fear], tick: 1, self: false, forcedDir: new Vector2(1, 0));
        var atk = default(AttackState); Vector2 pos = Vector2.zero;
        InstanceStep.Step(ref atk, st, ref pos, new InstanceInput { rawMove = new Vector2(0, 1), attack = new AttackIntent { aimDir = new Vector2(1, 0) } }, ctx, out var res);
        Assert.Greater(pos.x, 0f);                 // fled east (frozen dir), not north (rawMove)
        Assert.AreEqual(0f, pos.y, 1e-4f);
        Assert.Greater(res.moveApplied.x, 0f);     // reported the applied flee vector
        Assert.AreEqual(0f, res.moveApplied.y, 1e-4f);
    }
```

- [ ] **Step 2: Run to verify it fails** — `run_tests mode=EditMode filter=InstanceStepTests`. Expected: FAIL (compile error: `InstanceResult` has no `moveApplied`).

- [ ] **Step 3: Implement** — in `InstanceStep.cs`, add `moveApplied` to `InstanceResult` and the override in `Step`:

Add the field to `InstanceResult`:

```csharp
public struct InstanceResult
{
    public bool feinted;        // a feint cancelled a windup this tick (caller emits the event; nothing client-side)
    public int periodicDamage;  // DOT accrued this tick (server applies to HP; predictor ignores)
    public Vector2 moveApplied; // the exact pre-collision move vector used (owner buffers this for reconcile replay)
}
```

Replace the tail of `Step` (the lunge/move/MovementStep lines) with:

```csharp
        Vector2? lunge = AttackLogic.LungeVelocity(atk, ctx.timeline);  // null = idle, zero = rooted windup, vec = lunge
        Vector2 move = lunge ?? FreeMove(cmd.rawMove, g);               // lunge overrides WASD; gate scales/blocks WASD only
        if (StatusLogic.ActiveForcedMove(status, ctx.defs, out var fdir, out var fscale) && fdir.sqrMagnitude > 1e-6f)
            move = fdir * fscale;                                       // forced flee (Fear) overrides everything
        result.moveApplied = move;
        pos = MovementStep.Step(pos, move, ctx.dt, ctx.speed, ctx.walkable);
```

- [ ] **Step 4: Run to verify pass** — `run_tests mode=EditMode filter=InstanceStepTests` (new + existing PASS). Then `run_tests mode=EditMode` (full suite green).

- [ ] **Step 5: Commit**

```bash
git add "Assets/_Scripts/InstanceSim/InstanceStep.cs" "Assets/Tests/EditMode/InstanceSim/InstanceStepTests.cs"
git commit -m "Combat: InstanceStep forced-move (Fear flee) override + reports moveApplied"
```

### Task 3.2: Add Fear to the catalog; `OnStrike` already routes it through the seam

**Files:**
- Modify: `Assets/Editor/StatusCatalogBuilder.cs`

- [ ] **Step 1: Append Fear to `Specs()`** (after `Fire`):

```csharp
        new Spec { kind = StatusKind.Fear, dur = 1.2f, bMove = true, bAtk = true, moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1, vis = 7, forced = ForcedMoveKind.FleeFrozen, forcedScale = 0.8f },
```

- [ ] **Step 2: Compile + rebuild** — `refresh_unity scope=all` → `read_console` clean → `execute_menu_item Tools/Combat/Build Status Catalog` → `read_console` for `updated with 8 effects`. Verify `Fear.asset` exists with `forcedMove = FleeFrozen`.

- [ ] **Step 3: Commit**

```bash
git add "Assets/Editor/StatusCatalogBuilder.cs" "Assets/_Combat/StatusCatalog.asset" "Assets/_Combat/Effects"
git commit -m "Combat: add Fear effect asset (FleeFrozen, blocks attack)"
```

### Task 3.3: Wire `selfFleeAngle` on the self status block

**Files:**
- Modify: `Assets/_Scripts/Net/SnapshotEntry.cs`
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs`

- [ ] **Step 1: Add the field + serialization in `SnapshotEntry.cs`** — add next to the existing self status block fields (`effDefId`/`effRemaining`/`effStacks`/`effectCount`):

```csharp
    public ushort selfFleeAngle;   // quantized flee dir for the active forced-move effect (self block); 0xFFFF = none
```

In the `NetworkSerialize`/serialize method, where the self block fields are written (guarded by `SelfBit`), add:

```csharp
            s.SerializeValue(ref selfFleeAngle);
```

(Place it adjacent to the existing `effectCount`/`effDefId` serialization so reader and writer stay in lockstep.)

- [ ] **Step 2: Populate it in `ReplicationHub.cs`** — where the self block is filled (near `entry.effectMask = StatusLogic.ActiveMask(sp.status);` and the per-effect arrays), set the flee angle from the server's forced-move effect:

```csharp
            entry.selfFleeAngle = 0xFFFF;
            if (StatusLogic.ActiveForcedMove(sp.status, statusDefs, out var fdir, out _))
                entry.selfFleeAngle = AimQuant.Encode(fdir);
```

(Use the same `statusDefs` already resolved in that method; if not in scope, fetch `Game.Instance.StatusCatalog.Defs`.)

- [ ] **Step 3: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Net/SnapshotEntry.cs" "Assets/_Scripts/Net/ReplicationHub.cs"
git commit -m "Net: carry quantized selfFleeAngle on the self status block (for Fear prediction)"
```

### Task 3.4: Owner adopts `fleeDir` and buffers the real applied move

**Files:**
- Modify: `Assets/_Scripts/Net/PredictionSystem.cs`
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs`

- [ ] **Step 1: `AdoptExternal` takes + applies the flee angle** — change the signature and set `fleeDir` on the adopted forced-move effect. Updated method header + the re-add loop:

```csharp
    public void AdoptExternal(byte[] defIds, ushort[] remaining, byte[] stacks, int n, ushort selfFleeAngle)
    {
        Vector2 fleeDir = selfFleeAngle == 0xFFFF ? default : AimQuant.Decode(selfFleeAngle);
        var defs = Defs();
        for (int i = 0; i < Status.count; )
        {
            if (!SelfPredicted(Status.effects[i].defId)) Status.effects[i] = Status.effects[--Status.count];
            else i++;
        }
        for (int k = 0; k < n; k++)
        {
            if (SelfPredicted(defIds[k])) continue;
            if (Status.count >= StatusState.Cap) break;
            bool forced = defIds[k] < defs.Length && defs[defIds[k]].forcedMove != ForcedMoveKind.None;
            Status.effects[Status.count++] = new ActiveEffect
            {
                defId = defIds[k], remainingTicks = remaining[k], stacks = stacks[k],
                sincePeriodTick = 0, appliedTick = 0, selfInflicted = false,
                fleeDir = forced ? fleeDir : default,
            };
        }
    }
```

- [ ] **Step 2: Buffer the real applied move in `FixedTickInstance`** — replace the `buffer.Store(...)` line that re-derives `AttackLogic.LungeVelocity(atk, tl) ?? InstanceStep.FreeMove(rawMove, gate)`. Capture the `InstanceResult` and store `moveApplied`:

Change the `InstanceStep.Step(..., out _)` call to `out var res`, then replace the buffer store + drop the now-unused local gate recompute:

```csharp
        InstanceStep.Step(ref atk, Status, ref p, new InstanceInput { rawMove = rawMove, attack = attack }, ctx, out var res);
        Pos = ResolveSelfCollision(p, prevPos);
        buffer.Store(new InputFrame { tick = Tick, input = res.moveApplied, predictedPos = Pos });
```

(Remove the line `GateMod gate = GateMod.Quantize(StatusLogic.Reduce(Status, Defs()));` and its comment — `res.moveApplied` now supplies the exact vector, including the fear flee, so reconcile replay reproduces the path.)

- [ ] **Step 3: Update the `AdoptExternal` caller in `LocalPlayer.cs`** — pass the new field:

```csharp
                    prediction.AdoptExternal(e.effDefId, e.effRemaining, e.effStacks, e.effectCount, e.selfFleeAngle);
```

- [ ] **Step 4: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors.

- [ ] **Step 5: Run full EditMode suite** — `run_tests mode=EditMode`. Expected: all green (pure logic unaffected; this is Net glue).

- [ ] **Step 6: Commit**

```bash
git add "Assets/_Scripts/Net/PredictionSystem.cs" "Assets/_Scripts/Net/LocalPlayer.cs"
git commit -m "Net: owner adopts fleeDir + buffers InstanceStep.moveApplied (predicted Fear)"
```

### Task 3.5: Assign Fear to a weapon + host-verify

**Files:**
- Modify: `Assets/_Combat/Spear.asset` (Fear) — via Inspector/MCP.

- [ ] **Step 1: Assign** — set `Spear.onHitEffects` = `{ effect = Effects/Fear.asset, magnitudeScale = 1 }` (by GUID), rebuild the weapon catalog if it caches, `refresh_unity scope=all` → `read_console` clean.

- [ ] **Step 2: Play smoke** — enter Play, no errors, exit.

- [ ] **Step 3: Host-verify note (hand to user):** with two players, hitting the victim with the Spear makes the victim flee directly away from the attacker for ~1.2s, unable to attack; **on the victim's own client the flee is smooth (predicted), not rubber-banding**; the attacker/remotes see the victim flee too (server-driven). Tune `Fear.forcedMoveScale`/duration in the Inspector if needed.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Combat/Spear.asset"
git commit -m "Combat: assign Fear to Spear"
```

---

# Phase 4 — Visuals (3 layers)

Goal: themed FX overlay on the attacker's swing (Layer A), a one-shot status-hit on the victim (Layer B), an over-time tick FX following the victim (Layer C), plus data-driven tint. All cosmetic, driven by the existing mask.

### Task 4.1: Import + slice the FX spritesheets

**Files:**
- Modify (import settings): selected sheets under `Assets/_Imported/PixeLike2_AssetPack/fx/` and `fx/themed/`.

Sheets needed for v1 (themes chosen per effect): Fire→`themed/fx_fire_*`, Bleed→`fx_red_*`, Fear→`fx_purple_*` (or `themed/fx_eldritch_*`), plus existing Poison→`fx_green_*`, Freeze→`themed/fx_frost_*`, Slow→`fx_gray_*`. For each, the **burn** sheet = tick FX, **impact** = hit FX, **slash** = attack overlay.

- [ ] **Step 1: Confirm slicing** — `fx_fire_burn.png` is already sliced (16 sprites, 4×4). For each other needed sheet, set Texture Type = Sprite (2D), Sprite Mode = **Multiple**, Pixels-Per-Unit = 100, Filter = Point, Compression = None, then slice **Grid By Cell Count 4×4** (or Automatic to match `fx_fire_burn`), pivot Center. Use `mcp__unity-mcp__manage_texture` (or the Sprite Editor) per sheet. Slice at minimum: `fx_fire_burn/impact/slash`, `fx_red_burn/impact/slash`, `fx_purple_burn/impact/slash`, `fx_green_burn/impact`, `fx_frost_burn/impact`, `fx_gray_burn`.

- [ ] **Step 2: Compile/refresh** — `refresh_unity scope=all` → `read_console` clean (import only).

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Imported/PixeLike2_AssetPack/fx"
git commit -m "Art: slice PixeLike2 fx sheets (burn/impact/slash) for status effects"
```

### Task 4.2: `StatusFxView` — victim hit one-shot + over-time tick (Layers B, C)

**Files:**
- Create: `Assets/_Scripts/Combat/StatusFxView.cs`

- [ ] **Step 1: Create the component**

```csharp
using UnityEngine;

// Visual: the victim's status FX, driven each frame by GhostManager (remotes) / LocalPlayer (self) with the
// active-effect mask. Two own child renderers so it never touches the body/weapon SpriteRenderer.color:
//   - HitFxBody: plays an effect's hitFx ONCE on the mask bit's rising edge (0->1).
//   - TickFxBody: while a bit is set, loops the highest-priority effect's tickFx at its authored period.
// Resolves defId -> StatusEffectAsset via Game.Instance.StatusCatalog.Visual.
public sealed class StatusFxView : MonoBehaviour
{
    [SerializeField] SpriteRenderer hitRenderer;    // own child "HitFxBody"
    [SerializeField] SpriteRenderer tickRenderer;   // own child "TickFxBody"

    byte prevMask;
    int hitDefId = -1; float hitElapsed;
    int tickDefId = -1; float tickElapsed;

    void Awake()
    {
        if (hitRenderer != null) hitRenderer.enabled = false;
        if (tickRenderer != null) tickRenderer.enabled = false;
    }

    // Priority for which single effect's tick FX shows when several are active (higher first).
    static readonly StatusKind[] Priority = { StatusKind.Fire, StatusKind.Bleed, StatusKind.Poison, StatusKind.Freeze, StatusKind.Slow, StatusKind.Fear };

    public void Render(byte mask)
    {
        var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        if (cat == null) return;

        // --- Layer B: one-shot on any bit that just turned on ---
        byte rising = (byte)(mask & ~prevMask);
        if (rising != 0)
        {
            for (int k = 0; k < Priority.Length; k++)
            {
                int id = (int)Priority[k];
                if ((rising & (1 << id)) == 0) continue;
                var fx = cat.Visual(id);
                if (fx != null && fx.hitFx != null && fx.hitFx.Length > 0) { hitDefId = id; hitElapsed = 0f; }
                break;
            }
        }
        prevMask = mask;
        DriveOneShot(cat);

        // --- Layer C: looping tick FX for the highest-priority active effect ---
        int activeTick = -1;
        for (int k = 0; k < Priority.Length; k++)
        {
            int id = (int)Priority[k];
            if ((mask & (1 << id)) == 0) continue;
            var fx = cat.Visual(id);
            if (fx != null && fx.tickFx != null && fx.tickFx.Length > 0) { activeTick = id; break; }
        }
        DriveTick(cat, activeTick);
    }

    void DriveOneShot(StatusCatalog cat)
    {
        if (hitRenderer == null) return;
        if (hitDefId < 0) { if (hitRenderer.enabled) hitRenderer.enabled = false; return; }
        var fx = cat.Visual(hitDefId);
        if (fx == null || fx.hitFx == null || fx.hitFx.Length == 0) { hitDefId = -1; hitRenderer.enabled = false; return; }
        hitElapsed += Time.deltaTime;
        int frame = (int)(hitElapsed * fx.hitFps);
        if (frame >= fx.hitFx.Length) { hitDefId = -1; hitRenderer.enabled = false; return; }   // one-shot done
        hitRenderer.enabled = true;
        hitRenderer.sprite = fx.hitFx[frame];
    }

    void DriveTick(StatusCatalog cat, int defId)
    {
        if (tickRenderer == null) return;
        if (defId != tickDefId) { tickDefId = defId; tickElapsed = 0f; }
        if (defId < 0) { if (tickRenderer.enabled) tickRenderer.enabled = false; return; }
        var fx = cat.Visual(defId);
        if (fx == null || fx.tickFx == null || fx.tickFx.Length == 0) { tickRenderer.enabled = false; return; }
        tickElapsed += Time.deltaTime;
        tickRenderer.enabled = true;
        int frame = Mathf.Clamp((int)(tickElapsed * fx.tickFps), 0, fx.tickFx.Length - 1);
        tickRenderer.sprite = fx.tickFx[frame];
        if (frame >= fx.tickFx.Length - 1) tickElapsed = 0f;   // loop (≈ one pulse per period when tickFps*period ≈ frames)
    }
}
```

- [ ] **Step 2: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/Combat/StatusFxView.cs"
git commit -m "Combat: StatusFxView — victim hit one-shot + over-time tick FX (own renderers)"
```

### Task 4.3: `AttackView` — themed swing overlay (Layer A)

**Files:**
- Modify: `Assets/_Scripts/Combat/AttackView.cs`

- [ ] **Step 1: Add the overlay layer + drive it by phase/progress**

Add a serialized field and a phase-driven overlay. Add near the other `[SerializeField]`s:

```csharp
    [SerializeField] SpriteRenderer overlay;   // effect FX layered over the swing (own renderer; above weaponFront)
```

In `Render(AttackState state, AttackDefinition def)`, after the existing `DriveFx(state);` call, add:

```csharp
        DriveOverlay(state, def);
```

And when the rig is set inactive (the early `if (def == null || !AttackLogic.IsAttacking(...))` branch) ensure the overlay clears — add `if (overlay != null) overlay.enabled = false;` inside that branch before `return;`.

Add the method:

```csharp
    // Layer A: if the weapon has a primary on-hit effect with an attackOverlayFx, play it by attack progress,
    // rotated toward the locked aim. Own renderer, so it never writes the weapon/body color.
    void DriveOverlay(AttackState state, AttackDefinition def)
    {
        if (overlay == null) return;
        var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        StatusEffectAsset fx = null;
        if (cat != null && def.onHitEffects != null)
            for (int i = 0; i < def.onHitEffects.Length; i++)
                if (def.onHitEffects[i].effect != null) { fx = def.onHitEffects[i].effect; break; }   // primary
        if (fx == null || fx.attackOverlayFx == null || fx.attackOverlayFx.Length == 0) { overlay.enabled = false; return; }

        float p = AttackLogic.PhaseProgress01(state, def.Timeline);   // 0..1 across the whole swing
        int frame = Mathf.Clamp((int)(p * fx.attackOverlayFx.Length), 0, fx.attackOverlayFx.Length - 1);
        overlay.enabled = true;
        overlay.sprite = fx.attackOverlayFx[frame];
        float rot = def.rotateToAim ? state.residualDeg : 0f;
        overlay.transform.localEulerAngles = new Vector3(0f, 0f, rot);
        var dn = Game.Instance != null ? Game.Instance.DayNight : null;
        if (dn != null) overlay.color = dn.tint;
    }
```

- [ ] **Step 2: Add `PhaseProgress01` to `AttackLogic`** — a normalized 0..1 progress across anticipation→hit→follow-through. In `Assets/_Scripts/Combat/Core/AttackLogic.cs`, add:

```csharp
    // Coarse 0..1 progress across the whole swing for FX overlays: anticipation = 0..0.5, hit = 0.5..0.75,
    // follow-through = 0.75..1 (frame-fraction within each phase). Idle = 0.
    public static float PhaseProgress01(in AttackState s, AttackTimeline tl)
    {
        switch (s.phase)
        {
            case AttackPhase.Anticipation:
            case AttackPhase.TapWindup: return 0.25f * FrameFrac(s, tl.anticipation);
            case AttackPhase.Hit:          return 0.5f + 0.25f * FrameFrac(s, tl.hit);
            case AttackPhase.FollowThrough:return 0.75f + 0.25f * FrameFrac(s, tl.followThrough);
            default: return 0f;
        }
    }

    static float FrameFrac(in AttackState s, TimedFrame[] phase)
    {
        if (phase == null || phase.Length == 0) return 0f;
        return Mathf.Clamp01((s.frameIndex + 0.5f) / phase.Length);
    }
```

(If `AttackLogic` lacks `using UnityEngine;`, add it for `Mathf`.)

- [ ] **Step 3: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Combat/AttackView.cs" "Assets/_Scripts/Combat/Core/AttackLogic.cs"
git commit -m "Combat: AttackView themed swing overlay (Layer A) driven by phase progress"
```

### Task 4.4: `StatusView` — data-driven tint

**Files:**
- Modify: `Assets/_Scripts/Combat/StatusView.cs`

- [ ] **Step 1: Replace the hardcoded color switch with catalog tint**

```csharp
using UnityEngine;

// Visual: tints the player rig by the highest-priority active tinting effect, read from the catalog (data-driven).
// Driven each frame with the active-effect mask. When no tinting effect is active it must NOT touch the color
// (PlayerView/AttackView own the day/night tint). HitStun has no tint (DmgView shows the hurt sprite).
public sealed class StatusView : MonoBehaviour
{
    SpriteRenderer[] sprites;
    static readonly StatusKind[] Priority = { StatusKind.Freeze, StatusKind.Fire, StatusKind.Poison, StatusKind.Bleed, StatusKind.Slow, StatusKind.Fear };

    void Awake() => sprites = GetComponentsInChildren<SpriteRenderer>(true);

    public void Render(byte mask)
    {
        if (sprites == null) return;
        var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        if (cat == null) return;
        for (int k = 0; k < Priority.Length; k++)
        {
            int id = (int)Priority[k];
            if ((mask & (1 << id)) == 0) continue;
            var fx = cat.Visual(id);
            if (fx == null || fx.tintColor == Color.white) continue;   // white = no tint
            for (int i = 0; i < sprites.Length; i++) if (sprites[i] != null) sprites[i].color = fx.tintColor;
            return;
        }
        // no tinting effect active — leave color alone (do NOT write white)
    }
}
```

> **Footgun reminder:** `StatusFxView`/overlay use their OWN renderers; `StatusView` still writes the shared body/weapon renderers — keep its early-return-when-no-tint behavior so it never clobbers the day/night tint (see spec Constraints).

- [ ] **Step 2: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors. Existing freeze/poison/slow tints now come from the assets — set their `tintColor` in Task 4.7.

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Scripts/Combat/StatusView.cs"
git commit -m "Combat: StatusView tint is data-driven from the effect asset"
```

### Task 4.5: Setup tool — add the rig renderers + wire components on `Ghost.prefab`

**Files:**
- Create: `Assets/Editor/EffectFxSetupTool.cs`

- [ ] **Step 1: Create a re-runnable tool** that adds three child SpriteRenderers and wires the views. It mirrors `DmgViewSetupTool` (uses `PrefabUtility.LoadPrefabContents` + `SaveAsPrefabAsset` + `SerializedObject`). It creates: `AttackRig/OverlayFx` (above weaponFront), `HitFxBody`, `TickFxBody` (rig-root children); copies sorting/material from `Body`; assigns `AttackView.overlay`, `StatusFxView.hitRenderer`/`tickRenderer`; adds `StatusFxView` if missing.

```csharp
using UnityEditor;
using UnityEngine;

// Tools > Minifantasy > Setup Effect FX. Re-runnable: adds OverlayFx (in AttackRig), HitFxBody, TickFxBody to
// Ghost.prefab and wires AttackView.overlay + StatusFxView.hitRenderer/tickRenderer. Excluded from PlayerView
// body-toggling by NOT being named like the body layers.
public static class EffectFxSetupTool
{
    const string PrefabPath = "Assets/_Prefabs/Ghost.prefab";   // confirm the actual Ghost.prefab path before running

    [MenuItem("Tools/Minifantasy/Setup Effect FX")]
    public static void Setup()
    {
        var root = PrefabUtility.LoadPrefabContents(PrefabPath);
        try
        {
            var body = FindByName(root.transform, "Body");
            if (body == null) { Debug.LogError("[EffectFx] no Body renderer found"); return; }
            var bodySr = body.GetComponent<SpriteRenderer>();

            var attackView = root.GetComponent<AttackView>();
            var rig = FindByName(root.transform, "AttackRig");
            var overlay = EnsureChild(rig != null ? rig : root.transform, "OverlayFx", bodySr, sortingBump: 2);
            var hit = EnsureChild(root.transform, "HitFxBody", bodySr, sortingBump: 3);
            var tick = EnsureChild(root.transform, "TickFxBody", bodySr, sortingBump: 1);

            var fxView = root.GetComponent<StatusFxView>();
            if (fxView == null) fxView = root.AddComponent<StatusFxView>();

            Wire(attackView, "overlay", overlay);
            Wire(fxView, "hitRenderer", hit);
            Wire(fxView, "tickRenderer", tick);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Debug.Log("[EffectFx] Ghost.prefab wired: OverlayFx, HitFxBody, TickFxBody + StatusFxView");
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    static Transform FindByName(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t) { var r = FindByName(c, name); if (r != null) return r; }
        return null;
    }

    static SpriteRenderer EnsureChild(Transform parent, string name, SpriteRenderer copyFrom, int sortingBump)
    {
        var existing = FindByName(parent, name);
        SpriteRenderer sr;
        if (existing != null) sr = existing.GetComponent<SpriteRenderer>();
        else
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            sr = go.AddComponent<SpriteRenderer>();
        }
        if (copyFrom != null)
        {
            sr.sharedMaterial = copyFrom.sharedMaterial;
            sr.sortingLayerID = copyFrom.sortingLayerID;
            sr.sortingOrder = copyFrom.sortingOrder + sortingBump;
        }
        sr.enabled = false;
        return sr;
    }

    static void Wire(Object target, string field, SpriteRenderer value)
    {
        if (target == null) { Debug.LogError($"[EffectFx] missing component for field {field}"); return; }
        var so = new SerializedObject(target);
        var p = so.FindProperty(field);
        if (p == null) { Debug.LogError($"[EffectFx] no serialized field '{field}' on {target.GetType().Name}"); return; }
        p.objectReferenceValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
}
```

- [ ] **Step 2: Confirm the prefab path** — `Ghost.prefab` is the rig the game spawns (per memory: AttackView/StatusView/DmgView live on it). Verify its path with Glob `Assets/**/Ghost.prefab` and fix `PrefabPath` if different before running.

- [ ] **Step 3: Compile + run** — `refresh_unity scope=all` → `read_console` clean → `execute_menu_item Tools/Minifantasy/Setup Effect FX` → `read_console` for the success log. Confirm no errors and the three children exist on the prefab.

- [ ] **Step 4: Commit**

```bash
git add "Assets/Editor/EffectFxSetupTool.cs" "Assets/_Prefabs/Ghost.prefab"
git commit -m "Combat: setup tool adds OverlayFx/HitFxBody/TickFxBody + StatusFxView to Ghost.prefab"
```

### Task 4.6: Drive `StatusFxView` for remotes + self

**Files:**
- Modify: `Assets/_Scripts/Net/GhostManager.cs`
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs`

- [ ] **Step 1: GhostManager** — cache + drive the new view. Where it caches `statusView`/`dmgView` (around line 21/59) add a `StatusFxView statusFxView` field to the `Ghost` struct and fetch it in the `new Ghost { ... }` initializer: `statusFxView = go.GetComponent<StatusFxView>()`. Where it renders (around lines 138-139, after StatusView, before/after DmgView) add:

```csharp
                if (g.statusFxView != null) g.statusFxView.Render(g.effectMask);
```

- [ ] **Step 2: LocalPlayer** — drive self. Where it renders self `sv.Render(mask)`/`dv.Render(mask)` (lines 112-113) add:

```csharp
            var fx = selfGhost.GetComponent<StatusFxView>(); if (fx != null) fx.Render(mask);
```

- [ ] **Step 3: Compile** — `refresh_unity scope=all` → `read_console`. Expected: zero errors.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Scripts/Net/GhostManager.cs" "Assets/_Scripts/Net/LocalPlayer.cs"
git commit -m "Net: drive StatusFxView from the effect mask (remotes + self)"
```

### Task 4.7: Author the visual fields on the effect assets

**Files:**
- Modify: `Assets/_Combat/Effects/*.asset` (Inspector/MCP — assign sprites + tint).

- [ ] **Step 1: Assign per effect** — for each effect set `tintColor`, `hitFx` (impact sheet sprites), `tickFx` (burn sheet sprites), `attackOverlayFx` (slash sheet sprites), using the sliced sheets from Task 4.1:
  - Fire: orange tint; `fx_fire_impact`/`fx_fire_burn`/`fx_fire_slash`.
  - Bleed: red tint; `fx_red_impact`/`fx_red_burn`/`fx_red_slash`.
  - Fear: purple tint; `fx_purple_impact`/`fx_purple_burn`/`fx_purple_slash`.
  - Poison: green tint; `fx_green_impact`/`fx_green_burn` (no overlay needed).
  - Freeze: blue tint; `fx_frost_impact`/`fx_frost_burn`.
  - Slow: dim/gray tint; `fx_gray_burn`.
  - HitStun/AttackCooldown: leave visuals empty (tint stays white = no tint).
  Assign sprite arrays in row-major (frame) order. Use `manage_asset`/Inspector; do not hand-edit YAML.

- [ ] **Step 2: Refresh + Play smoke** — `refresh_unity scope=all` → `read_console` clean → enter Play, confirm no errors, exit.

- [ ] **Step 3: Host-verify note (hand to user):**
  - **Layer A:** attacking with the Fire Axe / Bleed Dagger / Fear Spear shows the themed FX overlaid on the swing across anticipation→hit→follow-through, rotated to aim.
  - **Layer B:** on a successful hit the victim shows a one-shot impact burst of the effect's theme.
  - **Layer C:** while the DoT/fear is active a looping FX follows the victim and pulses ~once per tick.
  - **Tint:** the victim rig tints to the effect color (Freeze blue, Fire orange, etc.); clears when it expires; HitStun shows the hurt sprite (no tint), unchanged.

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Combat/Effects"
git commit -m "Combat: author effect FX sprites + tints (Fire/Bleed/Fear/Poison/Freeze/Slow)"
```

---

## Self-Review (completed by plan author)

**Spec coverage:**
- Per-effect SO asset → Task 1.3/1.5. ✅
- `StatusCatalog` array + `Defs`/`Visual` → Task 1.4. ✅
- New kinds Bleed/Fire/Fear → 1.1 (enum) + 2.1/3.2 (assets). ✅
- Fear forced-flee sim → 1.1/1.2 (data+Apply) + 3.1 (InstanceStep). ✅
- Owner-predicted Fear (wire + buffer) → 3.3/3.4. ✅
- Source-agnostic apply seam → 1.6 (`CombatEffects`). ✅
- Per-weapon effect **array** → 1.6 (`onHitEffects`). ✅
- Layer A overlay → 4.3; Layer B/C `StatusFxView` → 4.2/4.6; data-driven tint → 4.4. ✅
- Rig renderers pre-placed via tool → 4.5 (honors scene-objects preference). ✅
- Mask stays a byte (8 kinds) — no widening needed; respected. ✅
- Tests: StatusLogic forced-move (1.2), InstanceStep flee+moveApplied (3.1); DoT reuses tested Poison path. ✅

**Placeholder scan:** No TBD/TODO; every code step has full code. FX sprite *assignment* (4.7) and texture *slicing* (4.1) are Inspector/asset actions with explicit per-effect lists, not code placeholders.

**Type consistency:** `StatusEffectAsset.ToDef()`, `StatusCatalog.Visual`, `EffectApply{defId,scale}`, `WeaponEffect{effect,magnitudeScale}`, `AdoptExternal(...,ushort selfFleeAngle)`, `InstanceResult.moveApplied`, `StatusLogic.Apply(...,Vector2 forcedDir)`, `ActiveForcedMove`, `AttackLogic.PhaseProgress01` — all defined before first use and referenced consistently.

**Known integration risks to verify during execution (not blockers):**
- `ReplicationHub` self-block fill site + `SnapshotEntry` self-block field names (`effDefId`/`effRemaining`/`effStacks`/`effectCount`) — confirm exact names when editing 3.3 (read the file first).
- `Ghost.prefab` path + child names (`Body`, `AttackRig`, `weaponFront`) — confirm in 4.5 before running the tool.
- Whether `WeaponCatalog` caches timelines such that reassigning `onHitEffects` needs a rebuild — check in 2.2.
