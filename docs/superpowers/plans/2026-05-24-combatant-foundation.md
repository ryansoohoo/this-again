# Base Hittable Character (Combatant) Foundation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decouple the hittable/combat state from the player/networking state by extracting a `Combatant` base class, generalize the hit + collision sweeps to operate on all combatants, and introduce the shared `CharacterDef` + deterministic stat-modifier data model — so future AI plugs into the same combat path. First enemy authored as data: **Goblin**.

**Architecture:** `ServerPlayer : Combatant` (inheritance, minimal churn — existing `sp.worldPos/hp/status` accesses stay valid). The registry holds all combatants (`Combatants`) while keeping `Players` for networking/AOI/input. `OnStrike` + the collision broadphase sweep `Combatants` in a region, gated by a pure `CombatRules.CanHit` faction rule (the "underworld-only" gate is the existing `inInstance`/region check). HP/stats come from a `CharacterDef` ScriptableObject via a pure `Stats` resolver (`(base + Σadd) × Πmul`); `maxHp` migrates off `StatusCatalog`.

**Tech Stack:** Unity 6000.3.7f1, C#, Netcode for GameObjects. Pure types live in the `Minifantasy.Combat` asmdef (`Assets/_Scripts/Combat/Core/`) and are unit-tested via NUnit EditMode tests (`Minifantasy.Combat.Tests`). Verification is via the unity-mcp bridge (`refresh_unity` → `read_console` → `run_tests`/`get_test_job` → `manage_editor` play), **not** a CLI — `execute_code` is broken on this machine.

**Spec:** `docs/superpowers/specs/2026-05-24-combatant-foundation-design.md`

---

## Conventions for every task

- **Verification is MCP, not CLI.** After writing/editing `.cs` files: `refresh_unity` (mode=force, compile=request, wait_for_ready=true). **Use `scope=all` whenever you CREATED a file** (so Unity imports it and generates its `.meta`; `scope=scripts` will not). Then `read_console` (types error) — **0 entries = clean compile**. For pure-logic tasks, also `run_tests` (mode=EditMode) → `get_test_job` (job_id, wait_timeout=60, include_failed_tests=true).
- **A new `.cs` file only gets its `.meta` after `refresh_unity scope=all`.** Do the refresh BEFORE `git add` or the `.cs.meta` add will fail.
- **Commit style:** match the repo — `"Combat: <description>"`. **No Claude attribution** (no `Co-Authored-By`, no "Generated with"). **Add specific files by name — never `git add -A`/`.`.**
- **WIP caution:** this feature edits shared files (`Game.cs`, `ReplicationHub.cs`, `AttackSimSystem.cs`, `PlayerRegistry.cs`, `StatusCatalog.cs`). The tree is clean at plan time; if any of these has unrelated uncommitted changes at execution time, confirm with Ryan before staging it (don't bundle his WIP into a feature commit).
- **Play-mode checks:** the FIRST play after a recompile can log phantom reload errors — `read_console action=clear`, re-enter play, read again; a real error recurs, a transient one shows zero on the clean replay. The play transition can stall when Unity is unfocused, but `Awake` still runs — wait ~8s then read console + `find_gameobjects`.

---

## Task 1: `Stats` stat-modifier layer (pure, Core)

The deterministic base-stat + modifier resolver. Pure, in the tested `Minifantasy.Combat` asmdef.

**Files:**
- Create: `Assets/_Scripts/Combat/Core/Stats.cs`
- Test: `Assets/Tests/EditMode/Combat/StatsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Assets/Tests/EditMode/Combat/StatsTests.cs`:

```csharp
using NUnit.Framework;

public class StatsTests
{
    static Stats WithMaxHp(float v) { var s = new Stats(); s.SetBase(StatKind.MaxHp, v); return s; }

    [Test]
    public void BaseOnly()
    {
        Assert.AreEqual(100, WithMaxHp(100).GetInt(StatKind.MaxHp));
    }

    [Test]
    public void AddModifier()
    {
        var s = WithMaxHp(100);
        s.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Add, value = 20, sourceId = 1 });
        Assert.AreEqual(120, s.GetInt(StatKind.MaxHp));
    }

    [Test]
    public void MulModifier()
    {
        var s = WithMaxHp(100);
        s.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Mul, value = 1.5f, sourceId = 1 });
        Assert.AreEqual(150, s.GetInt(StatKind.MaxHp));
    }

    [Test]
    public void AddThenMul_OrderIndependent()
    {
        var a = WithMaxHp(100);
        a.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Add, value = 50, sourceId = 1 });
        a.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Mul, value = 2f, sourceId = 2 });

        var b = WithMaxHp(100);
        b.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Mul, value = 2f, sourceId = 2 });
        b.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Add, value = 50, sourceId = 1 });

        Assert.AreEqual(300, a.GetInt(StatKind.MaxHp));                       // (100 + 50) * 2
        Assert.AreEqual(a.GetInt(StatKind.MaxHp), b.GetInt(StatKind.MaxHp));  // order-independent
    }

    [Test]
    public void RemoveBySource()
    {
        var s = WithMaxHp(100);
        s.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Add, value = 20, sourceId = 7 });
        s.RemoveBySource(7);
        Assert.AreEqual(100, s.GetInt(StatKind.MaxHp));
    }
}
```

- [ ] **Step 2: Verify it fails (red)**

Run: `refresh_unity` (scope=all, force, compile) → `read_console` (types error).
Expected: compile error — `The type or namespace name 'Stats' could not be found` (and `StatKind`, `StatModifier`, `ModOp`). This proves the test exists before the implementation.

- [ ] **Step 3: Write the implementation**

Create `Assets/_Scripts/Combat/Core/Stats.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

// Character stat identity. The value is an index into the base-stat array — add a new stat here AND bump
// StatKinds.Count. Only MaxHp exists in v1; Damage/Defense/MoveSpeed/Mass come with the stats/items features.
public enum StatKind : byte { MaxHp = 0 }

public static class StatKinds { public const int Count = 1; }   // == number of StatKind values

public enum ModOp : byte { Add, Mul }

// One runtime stat modifier (item / level / long-lived buff). sourceId lets a source remove exactly its own mods.
public struct StatModifier { public StatKind stat; public ModOp op; public float value; public int sourceId; }

// Per-combatant resolved stats: base values (seeded from CharacterDef) + a list of modifiers. Resolution is pure
// and order-independent — effective = (base + Σadd) × Πmul — so it is deterministic regardless of insertion order.
// This is the PERSISTENT base-stat layer; transient combat effects (hitstun/slow/DoT) live in StatusState, not here.
public sealed class Stats
{
    readonly float[] _base = new float[StatKinds.Count];
    readonly List<StatModifier> _mods = new();

    public Stats() { }

    public void SetBase(StatKind stat, float value) => _base[(int)stat] = value;

    public void AddModifier(StatModifier m) => _mods.Add(m);

    public void RemoveBySource(int sourceId)
    {
        for (int i = _mods.Count - 1; i >= 0; i--)
            if (_mods[i].sourceId == sourceId) _mods.RemoveAt(i);
    }

    public float Get(StatKind stat)
    {
        float add = 0f, mul = 1f;
        for (int i = 0; i < _mods.Count; i++)
        {
            var m = _mods[i];
            if (m.stat != stat) continue;
            if (m.op == ModOp.Add) add += m.value; else mul *= m.value;
        }
        return (_base[(int)stat] + add) * mul;
    }

    public int GetInt(StatKind stat) => Mathf.RoundToInt(Get(stat));
}
```

- [ ] **Step 4: Verify it passes (green)**

Run: `refresh_unity` (scope=all, force, compile) → `read_console` (0 errors) → `run_tests` (mode=EditMode) → `get_test_job` (wait_timeout=60, include_failed_tests=true).
Expected: clean compile; all 5 `StatsTests` pass; the rest of the EditMode suite stays green.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Combat/Core/Stats.cs Assets/_Scripts/Combat/Core/Stats.cs.meta Assets/Tests/EditMode/Combat/StatsTests.cs Assets/Tests/EditMode/Combat/StatsTests.cs.meta
git commit -m "Combat: add Stats stat-modifier layer (deterministic base + add/mul)"
```

---

## Task 2: `Faction` + `CombatRules.CanHit` (pure, Core)

The friend/foe gate. Pure, unit-tested; reusable by future client hit prediction.

**Files:**
- Create: `Assets/_Scripts/Combat/Core/Faction.cs`
- Create: `Assets/_Scripts/Combat/Core/CombatRules.cs`
- Test: `Assets/Tests/EditMode/Combat/CombatRulesTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Assets/Tests/EditMode/Combat/CombatRulesTests.cs`:

```csharp
using NUnit.Framework;

public class CombatRulesTests
{
    [Test] public void PlayerCanHitPlayer()    => Assert.IsTrue(CombatRules.CanHit(Faction.Player, Faction.Player));
    [Test] public void PlayerCanHitEnemy()     => Assert.IsTrue(CombatRules.CanHit(Faction.Player, Faction.Enemy));
    [Test] public void EnemyCanHitPlayer()     => Assert.IsTrue(CombatRules.CanHit(Faction.Enemy, Faction.Player));
    [Test] public void EnemyCannotHitEnemy()   => Assert.IsFalse(CombatRules.CanHit(Faction.Enemy, Faction.Enemy));
}
```

- [ ] **Step 2: Verify it fails (red)**

Run: `refresh_unity` (scope=all, force, compile) → `read_console`.
Expected: compile error — `'Faction'` / `'CombatRules'` could not be found.

- [ ] **Step 3: Write the implementation**

Create `Assets/_Scripts/Combat/Core/Faction.cs`:

```csharp
// Which side a combatant fights for. Drives CombatRules.CanHit (friend/foe). Player and Enemy now; Neutral later.
public enum Faction : byte { Player = 0, Enemy = 1 }
```

Create `Assets/_Scripts/Combat/Core/CombatRules.cs`:

```csharp
// Pure combat rules. CanHit is the friend/foe gate ONLY — the underworld-only restriction is the inInstance/region
// check at the call site (OnStrike), and self-hits are skipped there too. Pure + unit-tested so the same rule
// serves server hit detection today and future client-side hit prediction.
public static class CombatRules
{
    public static bool CanHit(Faction attacker, Faction victim)
    {
        if (attacker == Faction.Enemy && victim == Faction.Enemy) return false;   // no AI friendly-fire
        return true;   // Player↔Player (in-instance PvP), Player↔Enemy, Enemy↔Player
    }
}
```

- [ ] **Step 4: Verify it passes (green)**

Run: `refresh_unity` (scope=all, force, compile) → `read_console` (0 errors) → `run_tests` (mode=EditMode) → `get_test_job`.
Expected: clean compile; 4 `CombatRulesTests` pass; suite green.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Combat/Core/Faction.cs Assets/_Scripts/Combat/Core/Faction.cs.meta Assets/_Scripts/Combat/Core/CombatRules.cs Assets/_Scripts/Combat/Core/CombatRules.cs.meta Assets/Tests/EditMode/Combat/CombatRulesTests.cs Assets/Tests/EditMode/Combat/CombatRulesTests.cs.meta
git commit -m "Combat: add Faction + CombatRules.CanHit friend/foe gate"
```

---

## Task 3: `CharacterDef` ScriptableObject

Shared character data: faction + base stats, with `CreateStats()`. Default assembly, alongside `WeaponCatalog`/`StatusCatalog`.

**Files:**
- Create: `Assets/_Scripts/Combat/CharacterDef.cs`

- [ ] **Step 1: Write the implementation**

Create `Assets/_Scripts/Combat/CharacterDef.cs`:

```csharp
using UnityEngine;

// Shared character data: base combat stats + faction for one KIND of character (the player, or an AI type). Players
// reference one of these (wired on Game); each AI type gets its own. CreateStats() seeds the runtime Stats a
// Combatant resolves from. v1 carries maxHp + faction; damage/defense/moveSpeed/mass slot in later (add a field
// here + a StatKind + a SetBase line). Built/tuned via Tools > Combat > Build Character Defs.
[CreateAssetMenu(menuName = "Minifantasy/Character Def", fileName = "CharacterDef")]
public sealed class CharacterDef : ScriptableObject
{
    [Header("Identity")]
    public string displayName = "Character";
    public Faction faction = Faction.Player;

    [Header("Base stats")]
    public int maxHp = 100;

    // Build the runtime Stats seeded with this def's base values.
    public Stats CreateStats()
    {
        var s = new Stats();
        s.SetBase(StatKind.MaxHp, maxHp);
        return s;
    }
}
```

- [ ] **Step 2: Verify clean compile**

Run: `refresh_unity` (scope=all, force, compile) → `read_console` (types error).
Expected: 0 errors. (`CharacterDef` resolves `Faction`/`StatKind`/`Stats` from the auto-referenced `Minifantasy.Combat` asmdef.)

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/Combat/CharacterDef.cs Assets/_Scripts/Combat/CharacterDef.cs.meta
git commit -m "Combat: add CharacterDef SO (faction + base stats -> Stats)"
```

---

## Task 4: `Combatant` base + `ServerPlayer : Combatant` + registry + Game wiring

The core refactor. Pull the hittable/combat fields up into `Combatant`; slim `ServerPlayer` to player/networking/input; hold all combatants in the registry; source player faction/stats from the `CharacterDef` on `Game`. **Behavior must be identical** (players are still the only combatants).

**Files:**
- Create: `Assets/_Scripts/Combat/Combatant.cs`
- Modify: `Assets/_Scripts/Net/PlayerRegistry.cs` (full rewrite below)
- Modify: `Assets/_Scripts/Game.cs` (add `playerCharacter` field + accessor + Awake guard)
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs` (`EnsurePlayer` passes the def)

- [ ] **Step 1: Create `Combatant.cs`**

Create `Assets/_Scripts/Combat/Combatant.cs`:

```csharp
using System.Collections.Generic;
using UnityEngine;

// Base for every hittable character — players today, AI later (`class Npc : Combatant`). Holds the combat/spatial
// state the sim, the OnStrike hit seam, and the collision broadphase operate on; deliberately free of any
// player/networking/input concerns (those stay on ServerPlayer). Server-only — nothing here replicates directly;
// the snapshot stream carries derived values. Lives in the default assembly (it references AttackEvent in Net/).
public class Combatant
{
    public const ulong NpcIdBase = 1UL << 40;   // NPC entityIds start here so they never collide with NGO clientIds

    public ulong entityId;                  // stable id; == clientId for players, >= NpcIdBase for NPCs
    public Faction faction = Faction.Player;
    public Vector2 worldPos;
    public Vector2Int regionKey;            // (0,0) overworld; else underworld room interior origin
    public bool inInstance;

    public int hp;                          // server-authoritative; set from stats on instance enter
    public bool Alive => hp > 0;
    public Stats stats;                     // resolved character stats (maxHp now; scaling hooks for the rest)

    public readonly StatusState status = new();   // active status effects; reduces to the gate the sim consumes

    // Authoritative attack state (in-instance only). Stepped via the shared InstanceStep.
    public AttackState attackState;
    public PhaseScales attackScales = PhaseScales.One;
    public byte weaponId;                   // equipped weapon (catalog id)
    public AttackPhase prevAttackPhase;     // for transition detection (events + hit seam)
    public Queue<AttackEvent> pendingEvents;    // drained into per-viewer event RPCs each snapshot
}
```

- [ ] **Step 2: Rewrite `PlayerRegistry.cs`**

Replace the entire contents of `Assets/_Scripts/Net/PlayerRegistry.cs` with:

```csharp
using System.Collections.Generic;
using UnityEngine;

// Server-only authoritative state for one connected player. Combat/hittable state is inherited from Combatant;
// this class adds only the player/networking/input fields. Nothing here replicates; the snapshot stream is the
// only thing that leaves the server.
public sealed class ServerPlayer : Combatant
{
    public readonly PlayerMotion motion = new();
    public Vector2Int overworldReturnCell;
    public Vector2 submittedInput;        // latest owner intent (replaces the moveInput NetworkVariable)
    public bool snap;                     // set on teleport; cleared after the next snapshot carries it

    public uint lastProcessedTick;        // highest contiguous client tick the server has simulated (free move)
    public Vector2 lastInput;             // last applied free-move input (reference/debug)
    public RingBuffer<InputCommand> serverInputs;// received tick-stamped commands (free/in-instance only; lazily created)
}

// All combatants, keyed for two access patterns: Players (clients — networking/AOI/input, keyed by clientId) and
// Combatants (all — the hit + collision sweeps). Players register into both. Server-only.
public sealed class PlayerRegistry
{
    public readonly Dictionary<ulong, ServerPlayer> Players = new();
    public readonly List<Combatant> Combatants = new();

    public ServerPlayer Add(ulong clientId, Vector2Int cell, Vector2 worldPos, CharacterDef def)
    {
        var sp = new ServerPlayer { entityId = clientId, worldPos = worldPos, regionKey = Vector2Int.zero };
        sp.faction = def != null ? def.faction : Faction.Player;
        sp.stats = def != null ? def.CreateStats() : FallbackStats();
        sp.motion.cell = cell;
        Players[clientId] = sp;
        Combatants.Add(sp);
        return sp;
    }

    public void Remove(ulong clientId)
    {
        if (Players.TryGetValue(clientId, out var sp)) Combatants.Remove(sp);
        Players.Remove(clientId);
    }

    public bool TryGet(ulong clientId, out ServerPlayer sp) => Players.TryGetValue(clientId, out sp);

    // Default stats when no CharacterDef is wired (keeps the old 100 HP so behavior degrades gracefully + visibly).
    static Stats FallbackStats() { var s = new Stats(); s.SetBase(StatKind.MaxHp, 100); return s; }
}
```

- [ ] **Step 3: Add the `playerCharacter` field + accessor on `Game.cs`**

In `Assets/_Scripts/Game.cs`, in the `[Header("Combat")]` block (after the `statusCatalog` field, ~line 48), add:

```csharp
    [SerializeField] CharacterDef playerCharacter;  // shared player character data (faction + base stats); server reads it on player register
```

After the `public StatusCatalog StatusCatalog => statusCatalog;` line (~line 74), add:

```csharp
    public CharacterDef PlayerCharacter => playerCharacter;
```

In `Awake()`, after the `statusCatalog == null` LogError block (~line 129), add:

```csharp
        if (playerCharacter == null)
            Debug.LogError("[Game] PlayerCharacter (CharacterDef) is unassigned — players fall back to 100 HP. Assign Assets/_Combat/Characters/Player.asset to the Game component's Player Character field (GridManager prefab instance).");
```

- [ ] **Step 4: Update `EnsurePlayer` in `ReplicationHub.cs` to pass the def**

In `Assets/_Scripts/Net/ReplicationHub.cs`, replace the body of `EnsurePlayer` (~lines 55-63). Change the final line from `registry.Add(clientId, cell, pos);` to pass the player CharacterDef:

```csharp
    void EnsurePlayer(ulong clientId)
    {
        if (registry.Players.ContainsKey(clientId)) return;
        var gm = Game.Instance;
        int n = (int)clientId;
        var cell = new Vector2Int((n % 5) - 2, (n / 5) - 2);   // small spread around origin (old OnNetworkSpawn)
        var pos = gm != null ? gm.CellCenter(cell.x, cell.y) : (Vector2)cell;
        registry.Add(clientId, cell, pos, gm != null ? gm.PlayerCharacter : null);
    }
```

- [ ] **Step 5: Verify clean compile + suite green**

Run: `refresh_unity` (scope=all, force, compile) → `read_console` (types error → 0) → `run_tests` (mode=EditMode) → `get_test_job`.
Expected: 0 compile errors (every existing `sp.worldPos/hp/status/attackState/...` still resolves because `ServerPlayer` inherits them); full EditMode suite still green. If you see "ServerPlayer does not contain a definition for X", that field was missed in the Combatant/ServerPlayer split — re-check Step 1/2 against the spec field table.

- [ ] **Step 6: Verify Play boot (behavior unchanged)**

Run: `read_console action=clear` → `manage_editor action=play` → wait ~8s → `read_console` (types error/exception). `manage_editor action=stop`.
Expected: no recurring errors. Expected ONE-TIME on first boot if the asset isn't wired yet: the `[Game] PlayerCharacter (CharacterDef) is unassigned` LogError (asset is created + wired in Task 7) — that is expected here and resolved later. No NullReferenceException from player registration.

- [ ] **Step 7: Commit**

```bash
git add Assets/_Scripts/Combat/Combatant.cs Assets/_Scripts/Combat/Combatant.cs.meta Assets/_Scripts/Net/PlayerRegistry.cs Assets/_Scripts/Game.cs Assets/_Scripts/Net/ReplicationHub.cs
git commit -m "Combat: extract Combatant base from ServerPlayer; registry holds all combatants; player stats from CharacterDef"
```

---

## Task 5: Migrate `maxHp` off `StatusCatalog` onto stats

`maxHp` now lives on `CharacterDef` (via `Stats`). Repoint the 3 readers and delete the dead field.

**Files:**
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs` (enter-instance HP init)
- Modify: `Assets/_Scripts/Combat/StatusCatalog.cs` (remove `maxHp` field)
- Modify: `Assets/Editor/StatusCatalogBuilder.cs` (remove `maxHp` set + log)

- [ ] **Step 1: Init HP from stats on instance enter**

In `Assets/_Scripts/Net/ReplicationHub.cs`, in `EnterInstanceRpc` (~lines 231-233), replace:

```csharp
        var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        sp.hp = cat != null ? cat.maxHp : 100;   // full HP each run
        sp.status.Clear();                        // no effects carried in from a previous run
```

with:

```csharp
        sp.hp = sp.stats != null ? sp.stats.GetInt(StatKind.MaxHp) : 100;   // full HP each run (from CharacterDef stats)
        sp.status.Clear();                        // no effects carried in from a previous run
```

- [ ] **Step 2: Remove the dead field from `StatusCatalog.cs`**

In `Assets/_Scripts/Combat/StatusCatalog.cs`, delete the line (~line 9):

```csharp
    public int maxHp = 100;
```

- [ ] **Step 3: Remove `maxHp` from `StatusCatalogBuilder.cs`**

In `Assets/Editor/StatusCatalogBuilder.cs`, delete the line (~line 56):

```csharp
        catalog.maxHp = 100;
```

And change the final log (~line 60) from:

```csharp
        Debug.Log($"[StatusCatalog] {(catCreated ? "created" : "updated")} with {assets.Length} effects, maxHp={catalog.maxHp}");
```

to:

```csharp
        Debug.Log($"[StatusCatalog] {(catCreated ? "created" : "updated")} with {assets.Length} effects");
```

- [ ] **Step 4: Verify clean compile + suite green**

Run: `refresh_unity` (scope=scripts, force, compile) → `read_console` (types error → 0) → `run_tests` (mode=EditMode) → `get_test_job`.
Expected: 0 errors (grep already confirmed these are the only 3 `maxHp` readers); suite green. (No new files this task, so `scope=scripts` is fine.)

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Net/ReplicationHub.cs Assets/_Scripts/Combat/StatusCatalog.cs Assets/Editor/StatusCatalogBuilder.cs
git commit -m "Combat: migrate maxHp off StatusCatalog onto CharacterDef stats"
```

> The `StatusCatalog.asset` keeps a now-unused serialized `maxHp` value on disk; it is harmless (no field to deserialize into) and is cleaned the next time **Tools > Combat > Build Status Catalog** runs. No action needed.

---

## Task 6: Generalize the hit + collision sweeps to combatants

`OnStrike` sweeps all combatants in the region (gated by `CombatRules.CanHit`); the collision broadphase builds bodies from all in-instance combatants. **Behavior identical now** (players only); the seam is what changes.

**Files:**
- Modify: `Assets/_Scripts/Net/AttackSimSystem.cs` (full rewrite below)

- [ ] **Step 1: Rewrite `AttackSimSystem.cs`**

Replace the entire contents of `Assets/_Scripts/Net/AttackSimSystem.cs` with:

```csharp
using System.Collections.Generic;
using UnityEngine;

// Server-authoritative in-instance step: drain contiguous InputCommands per player, run the shared InstanceStep
// (attack + lunge + movement) so position is server-derived, advance lastProcessedTick, and on attack phase
// transitions enqueue AttackEvents + call the OnStrike hit seam. Then resolve combatant-vs-combatant overlaps for
// every in-instance combatant per room. Server-only.
//
// The hit seam (OnStrike) and the collision pass operate on ALL combatants (PlayerRegistry.Combatants), so future
// AI is hittable + solid with no further wiring. Player INPUT integration (Phase A) is still player-only; AI's
// integration is the brain step and will feed the same Phase-B collision pass.
public static class AttackSimSystem
{
    static Game _gm;
    static PlayerRegistry _reg;   // set each StepInstanceFixed so OnStrike can sweep same-room combatants
    static readonly System.Func<Vector2, bool> _walkAt = p => { var c = _gm.WorldToCell(p); return _gm.IsWalkable(c.x, c.y); };

    const float MovedEps = 1e-6f;   // sq-distance below which a combatant is "pinned" (didn't move this tick)

    struct Pending { public ulong id; public Combatant c; public Vector2Int region; public Vector2 startPos; public float invMass; }
    static readonly List<Pending> _pending = new();
    static CollisionBody[] _bodies = new CollisionBody[8];
    static readonly System.Comparison<Pending> _byRegionThenId = (a, b) =>
    {
        int c = a.region.x.CompareTo(b.region.x); if (c != 0) return c;
        c = a.region.y.CompareTo(b.region.y);     if (c != 0) return c;
        return a.id.CompareTo(b.id);
    };

    public static void StepInstanceFixed(PlayerRegistry reg, WeaponCatalog catalog, MovementSettings cfg, float dt)
    {
        var gm = Game.Instance; if (gm == null) return;
        _gm = gm;
        _reg = reg;
        var statusCat = gm.StatusCatalog;
        var statusDefs = statusCat != null ? statusCat.Defs : System.Array.Empty<StatusEffectDef>();

        // ---- Pre-pass: snapshot start positions for every in-instance combatant (for mover-yields invMass) ----
        _pending.Clear();
        foreach (var c in reg.Combatants)
        {
            if (!c.inInstance) continue;
            _pending.Add(new Pending { id = c.entityId, c = c, region = c.regionKey, startPos = c.worldPos });
        }

        // ---- Phase A: integrate each in-instance PLAYER (attack + lunge + movement vs walls), as before. AI
        //      integration (the brain step) will run here too and mutate its own worldPos before Phase B. ----
        foreach (var kv in reg.Players)
        {
            var sp = kv.Value;
            if (!sp.inInstance) continue;
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
                        ApplyDamage(sp, res.periodicDamage);
                        EmitTransitions(sp, def, c.tick, res.feinted);
                    }
                    else
                    {
                        // No weapon: still age status so slows/roots/DOT tick, and gate free-move.
                        var gate = GateMod.Quantize(StatusLogic.Step(sp.status, statusDefs, out int dmg));
                        ApplyDamage(sp, dmg);
                        sp.worldPos = MovementStep.Step(sp.worldPos, InstanceStep.FreeMove(c.rawMove, gate), dt, cfg.moveSpeed, _walkAt);
                    }
                    sp.lastInput = c.rawMove;
                    sp.lastProcessedTick++;
                }
            }
        }

        // ---- mover-yields weighting: moved this tick -> mover (1, absorbs the push), otherwise pinned (0) ----
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            p.invMass = (p.c.worldPos - p.startPos).sqrMagnitude > MovedEps ? 1f : 0f;
            _pending[i] = p;
        }

        // ---- Phase B: resolve combatant-vs-combatant overlaps per room (deterministic: sorted by region then id) ----
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
                    _bodies[k] = new CollisionBody { id = pp.id, pos = pp.c.worldPos, radius = cfg.collisionRadius, invMass = pp.invMass };
                }
                CollisionStep.Resolve(_bodies, n, _walkAt, cfg.collisionIterations);
                for (int k = 0; k < n; k++) _pending[start + k].c.worldPos = _bodies[k].pos;
            }
            start = end;
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

    static void EmitTransitions(Combatant attacker, AttackDefinition def, uint tick, bool feinted)
    {
        var prev = attacker.prevAttackPhase; var now = attacker.attackState.phase;
        if (prev == now && !feinted) return;   // only on a phase change (or a feint)
        attacker.pendingEvents ??= new System.Collections.Generic.Queue<AttackEvent>();
        if (prev == AttackPhase.Idle && now == AttackPhase.Anticipation)
            attacker.pendingEvents.Enqueue(Evt(attacker, AttackEvent.Started, tick));
        if (now == AttackPhase.Hit)
        {
            attacker.pendingEvents.Enqueue(Evt(attacker, AttackEvent.Struck, tick));
            OnStrike(attacker, def, tick);   // the forward hitbox seam
        }
        if (feinted)
            attacker.pendingEvents.Enqueue(Evt(attacker, AttackEvent.Feinted, tick));
    }

    // Server-authoritative damage: clamp at 0, log once on reaching 0 (no death/respawn in v1).
    static void ApplyDamage(Combatant c, int dmg)
    {
        if (dmg <= 0 || c.hp <= 0) return;
        c.hp = Mathf.Max(0, c.hp - dmg);
        if (c.hp == 0) Debug.Log($"[combat] combatant {c.entityId} reached 0 HP (no death/respawn yet)");
    }

    static AttackEvent Evt(Combatant attacker, byte kind, uint tick) => new AttackEvent
    {
        attackerId = attacker.entityId, kind = kind, weaponId = attacker.weaponId, tick = tick,
        aimAngle = AimQuant.Encode(attacker.attackState.lockedAim),
    };

    // Broadphase hit query (PLACEHOLDER for the deferred pixel narrowphase): same-region combatants within the
    // weapon's range + forward arc (from lockedAim), gated by faction (CombatRules.CanHit), take damage + the
    // weapon's on-hit effects. The inInstance/region/self checks ARE the underworld-only gate. Server-only.
    static void OnStrike(Combatant attacker, AttackDefinition def, uint tick)
    {
        if (_reg == null) return;
        var tl = def.Timeline;
        var statusCat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        var defs = statusCat != null ? statusCat.Defs : null;
        Vector2 origin = attacker.worldPos;
        Vector2 aim = attacker.attackState.lockedAim.sqrMagnitude > 1e-6f ? attacker.attackState.lockedAim.normalized : Vector2.right;
        bool full = attacker.attackState.windupComplete;   // full (charged) strike vs tap → scales the hitstun duration
        int hitstunTicks = full ? tl.hitstunTicks : tl.hitstunTapTicks;
        float range2 = tl.hitRange * tl.hitRange;
        foreach (var victim in _reg.Combatants)
        {
            if (victim == attacker) continue;
            if (!victim.inInstance || victim.regionKey != attacker.regionKey) continue;   // underworld-only + same room
            if (!CombatRules.CanHit(attacker.faction, victim.faction)) continue;          // friend/foe gate
            Vector2 to = victim.worldPos - origin;
            if (to.sqrMagnitude > range2 || to.sqrMagnitude < 1e-6f) continue;
            if (Vector2.Dot(to.normalized, aim) < tl.hitArcCos) continue;     // outside the forward arc
            ApplyDamage(victim, tl.damage);
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
            Debug.Log($"[attack] HIT {attacker.entityId} -> {victim.entityId} dmg={tl.damage} hp={victim.hp} hitstun={hitstunTicks}t ({(full ? "full" : "tap")})");
        }
    }
}
```

> Key changes vs. the old file: `_pending` holds `Combatant` (not a player-typed struct) and is built from `reg.Combatants` BEFORE Phase A (start positions captured up front); `EmitTransitions`/`Evt`/`ApplyDamage`/`OnStrike` take `Combatant` and use `attacker.entityId`; `OnStrike` sweeps `_reg.Combatants` and adds the `CombatRules.CanHit` line. Player-vs-player behavior is preserved because `CanHit(Player, Player) == true` and the combatant set is exactly the in-instance players.

- [ ] **Step 2: Verify clean compile + suite green**

Run: `refresh_unity` (scope=scripts, force, compile) → `read_console` (0 errors) → `run_tests` (mode=EditMode) → `get_test_job`.
Expected: 0 errors; the full EditMode suite (incl. `Minifantasy.InstanceSim.Tests` collision/determinism tests) stays green — proves the collision restructure is behavior-preserving.

- [ ] **Step 3: Verify Play boot**

Run: `read_console action=clear` → `manage_editor action=play` → wait ~8s → `read_console`. `manage_editor action=stop`.
Expected: no recurring errors/exceptions from the FixedUpdate sim.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/Net/AttackSimSystem.cs
git commit -m "Combat: OnStrike + collision sweep all combatants, faction-gated via CanHit"
```

---

## Task 7: Author CharacterDef assets (Player + Goblin), wire on Game, verify

Create the builder tool, run it to produce `Player.asset` + `Goblin.asset`, wire the Player def onto `Game`, and do the final end-to-end verification.

**Files:**
- Create: `Assets/Editor/CharacterDefBuilder.cs`
- Create (via tool): `Assets/_Combat/Characters/Player.asset`, `Assets/_Combat/Characters/Goblin.asset`
- Modify (via MCP): the `Game` component on the `GridManager` scene instance (`playerCharacter` field)

- [ ] **Step 1: Write the builder tool**

Create `Assets/Editor/CharacterDefBuilder.cs`:

```csharp
using UnityEditor;
using UnityEngine;

// Tools > Combat > Build Character Defs. Creates/updates the shared CharacterDef assets — the Player and the first
// enemy, Goblin — with default stats. Re-running RESETS stats to these defaults, so tune values in the Inspector
// afterward. After building, wire Assets/_Combat/Characters/Player.asset onto the Game component's Player Character
// field (GridManager prefab instance).
public static class CharacterDefBuilder
{
    const string Dir = "Assets/_Combat/Characters";

    struct Spec { public string file; public string name; public Faction faction; public int maxHp; }

    static Spec[] Specs() => new[]
    {
        new Spec { file = "Player", name = "Player", faction = Faction.Player, maxHp = 100 },
        new Spec { file = "Goblin", name = "Goblin", faction = Faction.Enemy,  maxHp = 60  },
    };

    [MenuItem("Tools/Combat/Build Character Defs")]
    public static void Build()
    {
        if (!AssetDatabase.IsValidFolder(Dir)) AssetDatabase.CreateFolder("Assets/_Combat", "Characters");
        foreach (var sp in Specs())
        {
            string path = $"{Dir}/{sp.file}.asset";
            var a = AssetDatabase.LoadAssetAtPath<CharacterDef>(path);
            bool created = a == null;
            if (created) a = ScriptableObject.CreateInstance<CharacterDef>();
            a.displayName = sp.name; a.faction = sp.faction; a.maxHp = sp.maxHp;
            if (created) AssetDatabase.CreateAsset(a, path); else EditorUtility.SetDirty(a);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[CharacterDef] built Player (100 HP) + Goblin (60 HP)");
    }
}
```

- [ ] **Step 2: Compile the tool**

Run: `refresh_unity` (scope=all, force, compile) → `read_console` (types error → 0).
Expected: 0 errors.

- [ ] **Step 3: Run the builder**

Run: `execute_menu_item` (menu_path="Tools/Combat/Build Character Defs").
Then `read_console` — expect the log `[CharacterDef] built Player (100 HP) + Goblin (60 HP)`.
Verify the assets exist: `Read` `Assets/_Combat/Characters/Player.asset` and `Assets/_Combat/Characters/Goblin.asset` — confirm each is a `CharacterDef` with the expected `displayName`/`faction`/`maxHp` (Goblin `faction: 1`, `maxHp: 60`).

- [ ] **Step 4: Get the Player asset GUID**

Run: `Read` `Assets/_Combat/Characters/Player.asset.meta` and note the `guid`.

- [ ] **Step 5: Wire the Player def onto `Game`**

The `Game` component lives on the `GridManager` scene instance (a prefab override in the open scene). Wire the field via MCP:
- `find_gameobjects` (by_component="Game", include_inactive=true) → note the instance id. (If empty at edit time because the object is bootstrapped at runtime, instead set the override on the `GridManager` prefab instance in the scene per the established workflow.)
- `manage_components` (action=set_property) on that GameObject, component="Game", property="playerCharacter", value={"guid":"<Player.asset guid>"}.
- `manage_scene` (action=save).

> This dirties the scene. Per [[feedback-committing-wip]], leave the scene-file commit to Ryan unless he says otherwise — flag it in the handoff.

- [ ] **Step 6: Settle, then verify Play boot (no unassigned LogError)**

Creating assets then immediately playing can bounce play mode — settle first:
Run: `refresh_unity` (scope=all, force, compile) → confirm `mcpforunity://editor/state` shows `external_changes_dirty:false` + `ready_for_tools:true`.
Then: `read_console action=clear` → `manage_editor action=play` → wait ~8s → `read_console`.
Expected: clean compile; **no** `[Game] PlayerCharacter (CharacterDef) is unassigned` error this time (it's wired); no recurring runtime errors. `manage_editor action=stop`.

> Live host/join HP behavior (a player entering the underworld initializes to 100 HP from the Player def) needs the IMGUI Host button, which MCP cannot click — leave that to Ryan's manual test. The static gates (compile + suite + boot + the asset/field reads) are the automated proof.

- [ ] **Step 7: Run the full EditMode suite once more**

Run: `run_tests` (mode=EditMode) → `get_test_job` (wait_timeout=60, include_failed_tests=true).
Expected: entire suite green (Stats + CombatRules + all pre-existing tests).

- [ ] **Step 8: Commit**

```bash
git add Assets/Editor/CharacterDefBuilder.cs Assets/Editor/CharacterDefBuilder.cs.meta Assets/_Combat/Characters Assets/_Combat/Characters.meta
git commit -m "Combat: add Character Defs builder + Player/Goblin assets"
```

> If Ryan wants the scene wiring committed too, add the scene file (e.g. `Assets/Scenes/SampleScene.unity` or the `GridManager` prefab) in a separate commit — confirm first (it may carry his WIP).

---

## Self-Review

**1. Spec coverage** (each spec section → task):
- §3.1 `Combatant` base + `ServerPlayer : Combatant` + field split + `entityId`/`NpcIdBase` → **Task 4** (Combatant.cs field table matches the spec exactly; `NpcIdBase = 1UL<<40`).
- §3.2 registry `Players` + `Combatants` views + `Add`/`Remove` → **Task 4**.
- §3.3 `Faction` + `CombatRules.CanHit` matrix → **Task 2** (+ wired into OnStrike in Task 6).
- §3.4 OnStrike sweeps combatants; collision Phase B over all combatants; Phase A players-only; startPos captured up front → **Task 6**.
- §3.5 `CharacterDef` SO + `Stats` (`(base+Σadd)×Πmul`, order-independent, int HP) + `maxHp` migration → **Tasks 1, 3, 5**.
- §3.6 HP on Combatant; player reset-on-enter from stats; clamp at 0; no death → **Tasks 4, 5, 6** (`ApplyDamage` clamp preserved).
- §4 Player + Goblin defs via builder; Goblin `faction=Enemy` → **Task 7**.
- §5 no wire/ghost/AI/spawn change → honored (no edits to `SnapshotEntry`/`GhostManager`/`Ghost.prefab`).
- §7 unit tests (CanHit matrix, Stats resolver) + MCP gates → **Tasks 1, 2** + verification steps throughout.
- §8 footguns: Game-wiring guard (Task 4 Step 3), maxHp migration all readers (Task 5 — the 3 grep'd readers), asmdef boundaries (pure types in Core, Combatant in default), `scope=all` for new files (every create task), curated assets via builder (Task 7). All covered.

No gaps.

**2. Placeholder scan:** No "TBD"/"TODO"/"handle edge cases"/"similar to Task N". Every code step has complete code; every verify step has the exact MCP call + expected output. `NpcIdBase` value is concrete (`1UL<<40`); Goblin maxHp concrete (60, tunable).

**3. Type consistency:** `Stats` API — `SetBase`, `AddModifier`, `RemoveBySource`, `Get`, `GetInt` — used identically in Tasks 1, 3 (`CreateStats`), 4 (`FallbackStats`), 5 (`stats.GetInt(StatKind.MaxHp)`). `CombatRules.CanHit(Faction, Faction)` defined in Task 2, called in Task 6 with `(attacker.faction, victim.faction)`. `Combatant` fields (`entityId`, `faction`, `worldPos`, `regionKey`, `inInstance`, `hp`, `stats`, `status`, `attackState`, `attackScales`, `weaponId`, `prevAttackPhase`, `pendingEvents`) defined in Task 4, consumed in Task 6 (`attacker.entityId`, `victim.faction`, etc.). `CharacterDef.CreateStats()`/`faction`/`maxHp` defined in Task 3, used in Task 4 (`def.CreateStats()`, `def.faction`) and Task 7 (`a.maxHp`, `a.faction`). `PlayerRegistry.Add(clientId, cell, pos, def)` defined in Task 4, called in Task 4's `EnsurePlayer`. Consistent throughout.

---

## Execution Handoff

After the plan is approved, see the two execution options (subagent-driven vs inline) presented separately.
