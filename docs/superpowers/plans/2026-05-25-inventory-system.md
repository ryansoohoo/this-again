# Inventory System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Server-authoritative per-player inventory (20 slots, weapons + consumables, no persistence) accessible via chat commands. Replaces the hardcoded `LocalPlayer.weapons[]` array. Adds two new replication primitives: a targeted per-player chat RPC and an owner-only inventory-sync RPC.

**Architecture:** Pure-C# `Inventory` class lives on every `ServerPlayer`. Five commands (`inv`, `equip`, `use`, `drop`, `give`) — `inv` reads a client mirror locally; the other four send ServerRpcs that mutate the inventory, then the server emits an `InventoryChangedClientRpc` to the owner and a `TargetedLogClientRpc` carrying the user-visible result. Equip is gated to overworld (`!sp.inInstance`); fresh players default to `weaponId = 255` so `WeaponCatalog.Get(255) == null` and attacks no-op (unarmed sentinel — supersedes the spec's "reserve catalog index 0" approach).

**Tech Stack:** Unity 6000.3.7f1, C#, Netcode for GameObjects. Pure-logic types live in a new `Minifantasy.Inventory` asmdef (`Assets/_Scripts/Inventory/`, `autoReferenced: true`) and are unit-tested via NUnit EditMode tests (`Minifantasy.Inventory.Tests`). SO catalogs (Consumable) live in default Assembly-CSharp alongside `WeaponCatalog` because they reference `StatusEffectAsset` (also default). Verification is via the unity-mcp bridge (`refresh_unity` → `read_console` → `run_tests`/`get_test_job` → `manage_editor` play), **not** a CLI — `execute_code` is broken on this machine.

**Spec:** [`docs/superpowers/specs/2026-05-25-inventory-system-design.md`](../specs/2026-05-25-inventory-system-design.md)

---

## Refinement of spec

Two implementation choices diverge slightly from the spec — both strict improvements:

1. **Unarmed sentinel uses byte 255, not "reserve catalog index 0".** `WeaponCatalog.Get(255)` already returns null via the existing `id < weapons.Length` guard (catalog has <256 entries). `Combatant.weaponId` defaults to `255`. Zero asset edits required; `AttackSimSystem` already null-checks the result (`Net/AttackSimSystem.cs:57`). The spec's "shift catalog entries up by one" is dropped.
2. **`Inventory` lives in its own asmdef `Minifantasy.Inventory`, not the default assembly.** Required for unit-test reachability (Assembly-CSharp can't be referenced from a tests asmdef directly). `autoReferenced: true` so default Assembly-CSharp still uses it implicitly — functionally equivalent.

`InventorySlot` and `ItemKind` go in the same asmdef. `ConsumableDefinition` + `ConsumableCatalog` stay in default Assembly-CSharp because they reference `StatusEffectAsset` (also default).

---

## Conventions for every task

- **Verification is MCP, not CLI.** After writing/editing `.cs` files: `refresh_unity` (mode=force, compile=request, wait_for_ready=true). **Use `scope=all` whenever you CREATED a file** (so Unity imports it and generates its `.meta`; `scope=scripts` will not). Then `read_console` (types error) — **0 entries = clean compile**. For pure-logic tasks, also `run_tests` (mode=EditMode) → `get_test_job` (job_id, wait_timeout=60, include_failed_tests=true).
- **A new `.cs` file only gets its `.meta` after `refresh_unity scope=all`.** Do the refresh BEFORE `git add` or the `.cs.meta` add will fail.
- **Commit style:** match the repo — `"Inventory: <description>"`. **No Claude attribution** (no `Co-Authored-By`, no "Generated with"). **Add specific files by name — never `git add -A`/`.`.**
- **WIP caution:** this feature edits shared files (`Game.cs`, `ReplicationHub.cs`, `Combatant.cs`, `PlayerRegistry.cs`, `LocalPlayer.cs`, `CommandBootstrap.cs`, `GameLog.cs`). At plan time the tree has `M Assets/_Combat/Slash.asset` (unrelated WIP) — leave it alone. If any source file above has uncommitted changes when you arrive at a task, confirm with Ryan before staging it.
- **Asset authoring is hands-off.** The `ConsumableCatalog.asset` and any `ConsumableDefinition.asset` entries are created empty (no entries); Ryan curates them per [[feedback_biome_assets]]. Don't populate via MCP.
- **Play-mode checks:** the FIRST play after a recompile can log phantom reload errors — `read_console action=clear`, re-enter play, read again; a real error recurs, a transient one shows zero on the clean replay.

---

## Task 1: Foundation types + `Minifantasy.Inventory` asmdef

`ItemKind` enum and `InventorySlot` struct in a new asmdef. No logic — just the wire-shape primitives. INetworkSerializable lives on the struct so the inventory RPC can pass `InventorySlot[]` directly.

**Files:**
- Create: `Assets/_Scripts/Inventory/Minifantasy.Inventory.asmdef`
- Create: `Assets/_Scripts/Inventory/ItemKind.cs`
- Create: `Assets/_Scripts/Inventory/InventorySlot.cs`

- [ ] **Step 1: Create the asmdef**

Create `Assets/_Scripts/Inventory/Minifantasy.Inventory.asmdef`:

```json
{
    "name": "Minifantasy.Inventory",
    "rootNamespace": "",
    "references": [
        "Unity.Netcode.Runtime"
    ],
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

- [ ] **Step 2: Create `ItemKind.cs`**

```csharp
// Category tag for an inventory slot. Drives lookup against the matching catalog (WeaponCatalog or
// ConsumableCatalog). 0 = None (empty slot). Future categories take 3+.
public enum ItemKind : byte
{
    None       = 0,
    Weapon     = 1,
    Consumable = 2,
}
```

- [ ] **Step 3: Create `InventorySlot.cs`**

```csharp
using Unity.Netcode;

// One inventory slot. 4 bytes on the wire so a 20-slot inventory packs to 80 bytes (sent only on change
// to one client, never in snapshots). Weapons: count == 1. Consumables: count 1..255 stacks share one slot.
public struct InventorySlot : INetworkSerializable
{
    public ItemKind kind;
    public byte id;        // index into WeaponCatalog (kind=Weapon) or ConsumableCatalog (kind=Consumable)
    public byte count;
    public byte _reserved;

    public bool IsEmpty => kind == ItemKind.None;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        byte k = (byte)kind; s.SerializeValue(ref k); kind = (ItemKind)k;
        s.SerializeValue(ref id);
        s.SerializeValue(ref count);
        s.SerializeValue(ref _reserved);
    }
}
```

- [ ] **Step 4: Refresh Unity + verify compile**

Call `refresh_unity` with `mode=force`, `scope=all`, `wait_for_ready=true`. Then `read_console` filtered to errors — expect **0 entries**.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Inventory/Minifantasy.Inventory.asmdef Assets/_Scripts/Inventory/Minifantasy.Inventory.asmdef.meta Assets/_Scripts/Inventory/ItemKind.cs Assets/_Scripts/Inventory/ItemKind.cs.meta Assets/_Scripts/Inventory/InventorySlot.cs Assets/_Scripts/Inventory/InventorySlot.cs.meta
git commit -m "Inventory: foundation types (ItemKind, InventorySlot) + Minifantasy.Inventory asmdef"
```

If Unity hasn't generated a `.meta` for the asmdef itself (sometimes asmdef meta lags), re-run `refresh_unity scope=all` once before staging.

---

## Task 2: `Inventory` class (TDD)

The pure data class with TryGive / TryEquip / TryUse / TryDrop. Operates on `InventorySlot[]` only — no SO refs. Caller passes `maxStack` for consumables. Name resolution lives outside `Inventory` (in the command handlers).

**Files:**
- Create: `Assets/_Scripts/Inventory/Inventory.cs`
- Create: `Assets/Tests/EditMode/Inventory/Minifantasy.Inventory.Tests.asmdef`
- Create: `Assets/Tests/EditMode/Inventory/InventoryTests.cs`

- [ ] **Step 1: Create the tests asmdef**

`Assets/Tests/EditMode/Inventory/Minifantasy.Inventory.Tests.asmdef`:

```json
{
    "name": "Minifantasy.Inventory.Tests",
    "rootNamespace": "",
    "references": [
        "Minifantasy.Inventory",
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

- [ ] **Step 2: Write the failing tests**

`Assets/Tests/EditMode/Inventory/InventoryTests.cs`:

```csharp
using NUnit.Framework;

public class InventoryTests
{
    static Inventory Make() => new Inventory();
    const byte HealId = 0;
    const byte FireId = 1;
    const byte SwordId = 0;
    const byte MaxStack = 64;

    [Test]
    public void TryGive_EmptyInventory_AddsToFirstSlot()
    {
        var inv = Make();
        Assert.IsTrue(inv.TryGive(ItemKind.Consumable, HealId, 1, MaxStack, out byte added, out _));
        Assert.AreEqual(1, added);
        Assert.AreEqual(ItemKind.Consumable, inv.slots[0].kind);
        Assert.AreEqual(HealId, inv.slots[0].id);
        Assert.AreEqual(1, inv.slots[0].count);
    }

    [Test]
    public void TryGive_StacksConsumables_UpToMaxStack()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Consumable, HealId, 10, MaxStack, out _, out _);
        inv.TryGive(ItemKind.Consumable, HealId, 5, MaxStack, out byte added, out _);
        Assert.AreEqual(5, added);
        Assert.AreEqual(15, inv.slots[0].count);
        Assert.IsTrue(inv.slots[1].IsEmpty);
    }

    [Test]
    public void TryGive_SpillsToNextSlotWhenStackFull()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Consumable, HealId, MaxStack, MaxStack, out _, out _);
        inv.TryGive(ItemKind.Consumable, HealId, 3, MaxStack, out byte added, out _);
        Assert.AreEqual(3, added);
        Assert.AreEqual(MaxStack, inv.slots[0].count);
        Assert.AreEqual(3, inv.slots[1].count);
        Assert.AreEqual(HealId, inv.slots[1].id);
    }

    [Test]
    public void TryGive_WeaponsNeverStack_OneSlotPerCount()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Weapon, SwordId, 3, MaxStack, out byte added, out _);
        Assert.AreEqual(3, added);
        Assert.AreEqual(1, inv.slots[0].count);
        Assert.AreEqual(1, inv.slots[1].count);
        Assert.AreEqual(1, inv.slots[2].count);
        Assert.IsTrue(inv.slots[3].IsEmpty);
    }

    [Test]
    public void TryGive_FullInventory_AddsZero_ReturnsFalse()
    {
        var inv = Make();
        for (int i = 0; i < Inventory.Capacity; i++)
            inv.TryGive(ItemKind.Weapon, SwordId, 1, MaxStack, out _, out _);
        bool ok = inv.TryGive(ItemKind.Weapon, SwordId, 1, MaxStack, out byte added, out string reason);
        Assert.IsFalse(ok);
        Assert.AreEqual(0, added);
        Assert.IsNotNull(reason);
    }

    [Test]
    public void TryGive_PartiallyFullInventory_AddsSomeUntilFull()
    {
        var inv = Make();
        for (int i = 0; i < Inventory.Capacity - 1; i++)
            inv.TryGive(ItemKind.Weapon, SwordId, 1, MaxStack, out _, out _);
        inv.TryGive(ItemKind.Weapon, SwordId, 5, MaxStack, out byte added, out _);
        Assert.AreEqual(1, added);
    }

    [Test]
    public void TryEquip_WeaponSlot_UpdatesEquippedState()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Weapon, SwordId, 1, MaxStack, out _, out _);
        Assert.IsTrue(inv.TryEquip(0, out _));
        Assert.AreEqual(0, inv.equippedSlot);
        Assert.AreEqual(SwordId, inv.equippedWeaponId);
    }

    [Test]
    public void TryEquip_ConsumableSlot_RejectsWithReason()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Consumable, HealId, 1, MaxStack, out _, out _);
        Assert.IsFalse(inv.TryEquip(0, out string reason));
        Assert.That(reason, Does.Contain("weapon").IgnoreCase);
    }

    [Test]
    public void TryEquip_EmptySlot_RejectsWithReason()
    {
        var inv = Make();
        Assert.IsFalse(inv.TryEquip(0, out string reason));
        Assert.IsNotEmpty(reason);
    }

    [Test]
    public void TryEquip_OutOfRangeSlot_RejectsWithReason()
    {
        var inv = Make();
        Assert.IsFalse(inv.TryEquip(-1, out _));
        Assert.IsFalse(inv.TryEquip(Inventory.Capacity, out _));
    }

    [Test]
    public void TryUse_ConsumableSlot_DecrementsCount()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Consumable, HealId, 3, MaxStack, out _, out _);
        Assert.IsTrue(inv.TryUse(0, out _));
        Assert.AreEqual(2, inv.slots[0].count);
        Assert.AreEqual(ItemKind.Consumable, inv.slots[0].kind);
    }

    [Test]
    public void TryUse_ConsumableSlotLastCount_FreesSlot()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Consumable, HealId, 1, MaxStack, out _, out _);
        Assert.IsTrue(inv.TryUse(0, out _));
        Assert.IsTrue(inv.slots[0].IsEmpty);
    }

    [Test]
    public void TryUse_WeaponSlot_RejectsWithReason()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Weapon, SwordId, 1, MaxStack, out _, out _);
        Assert.IsFalse(inv.TryUse(0, out string reason));
        Assert.That(reason, Does.Contain("consumable").IgnoreCase.Or.Contain("equip").IgnoreCase);
    }

    [Test]
    public void TryUse_EmptySlot_RejectsWithReason()
    {
        var inv = Make();
        Assert.IsFalse(inv.TryUse(0, out _));
    }

    [Test]
    public void TryDrop_PartialCount_KeepsRemainder()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Consumable, HealId, 5, MaxStack, out _, out _);
        Assert.IsTrue(inv.TryDrop(0, 2, out _));
        Assert.AreEqual(3, inv.slots[0].count);
    }

    [Test]
    public void TryDrop_FullStack_FreesSlot()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Consumable, HealId, 5, MaxStack, out _, out _);
        Assert.IsTrue(inv.TryDrop(0, 5, out _));
        Assert.IsTrue(inv.slots[0].IsEmpty);
    }

    [Test]
    public void TryDrop_CountGreaterThanStack_DropsWholeStack()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Consumable, HealId, 3, MaxStack, out _, out _);
        Assert.IsTrue(inv.TryDrop(0, 99, out _));
        Assert.IsTrue(inv.slots[0].IsEmpty);
    }

    [Test]
    public void TryDrop_EquippedWeapon_ClearsEquippedState()
    {
        var inv = Make();
        inv.TryGive(ItemKind.Weapon, SwordId, 1, MaxStack, out _, out _);
        inv.TryEquip(0, out _);
        Assert.IsTrue(inv.TryDrop(0, 1, out _));
        Assert.AreEqual(-1, inv.equippedSlot);
        Assert.AreEqual((byte)255, inv.equippedWeaponId);
    }

    [Test]
    public void TryDrop_EmptySlot_Rejects()
    {
        var inv = Make();
        Assert.IsFalse(inv.TryDrop(0, 1, out _));
    }

    [Test]
    public void NewInventory_HasUnarmedSentinel()
    {
        var inv = Make();
        Assert.AreEqual(-1, inv.equippedSlot);
        Assert.AreEqual((byte)255, inv.equippedWeaponId);
    }
}
```

- [ ] **Step 3: Refresh Unity to verify tests fail to compile (Inventory not defined yet)**

`refresh_unity scope=all` then `read_console types=error` — expect compile errors mentioning `Inventory` not found. That's the "test fails" check at the compile level.

- [ ] **Step 4: Implement `Inventory`**

Create `Assets/_Scripts/Inventory/Inventory.cs`:

```csharp
// Per-player server-side inventory: 20 slots, weapons + consumables, in-memory only. Pure C# class —
// no Unity / Netcode / SO refs — so it's trivially unit-testable. Name resolution and effect application
// live in the calling command handler, not here. equippedWeaponId mirrors ServerPlayer.weaponId; the caller
// (command handler) is responsible for copying it across atomically. equippedWeaponId == 255 is the unarmed
// sentinel (WeaponCatalog.Get(255) returns null).
public sealed class Inventory
{
    public const int Capacity = 20;
    public const byte UnarmedSentinel = 255;

    public readonly InventorySlot[] slots = new InventorySlot[Capacity];
    public byte equippedWeaponId = UnarmedSentinel;
    public int equippedSlot = -1;

    public bool TryGive(ItemKind kind, byte id, byte count, byte maxStack, out byte added, out string reason)
    {
        added = 0; reason = null;
        if (kind == ItemKind.None || count == 0) { reason = "Invalid item."; return false; }

        int remaining = count;
        // Consumables stack into matching slots first
        if (kind == ItemKind.Consumable)
        {
            for (int i = 0; i < Capacity && remaining > 0; i++)
            {
                if (slots[i].kind != ItemKind.Consumable || slots[i].id != id) continue;
                int space = maxStack - slots[i].count;
                if (space <= 0) continue;
                int take = remaining < space ? remaining : space;
                slots[i].count = (byte)(slots[i].count + take);
                remaining -= take;
            }
        }
        // Spill into empty slots. Weapons always one per slot; consumables fill a new stack up to maxStack.
        for (int i = 0; i < Capacity && remaining > 0; i++)
        {
            if (!slots[i].IsEmpty) continue;
            byte take = kind == ItemKind.Weapon ? (byte)1 : (byte)(remaining < maxStack ? remaining : maxStack);
            slots[i] = new InventorySlot { kind = kind, id = id, count = take };
            remaining -= take;
        }

        added = (byte)(count - remaining);
        if (added == 0) { reason = "Inventory is full."; return false; }
        return true;
    }

    public bool TryEquip(int slotIndex, out string reason)
    {
        reason = null;
        if (slotIndex < 0 || slotIndex >= Capacity) { reason = "No such slot."; return false; }
        ref var s = ref slots[slotIndex];
        if (s.IsEmpty) { reason = "Empty slot."; return false; }
        if (s.kind != ItemKind.Weapon) { reason = "Can't equip a consumable — try use."; return false; }
        equippedSlot = slotIndex;
        equippedWeaponId = s.id;
        return true;
    }

    public bool TryUse(int slotIndex, out string reason)
    {
        reason = null;
        if (slotIndex < 0 || slotIndex >= Capacity) { reason = "No such slot."; return false; }
        ref var s = ref slots[slotIndex];
        if (s.IsEmpty) { reason = "Empty slot."; return false; }
        if (s.kind != ItemKind.Consumable) { reason = "Can't use a weapon — try equip."; return false; }
        s.count = (byte)(s.count - 1);
        if (s.count == 0) s = default;
        return true;
    }

    public bool TryDrop(int slotIndex, byte count, out string reason)
    {
        reason = null;
        if (slotIndex < 0 || slotIndex >= Capacity) { reason = "No such slot."; return false; }
        ref var s = ref slots[slotIndex];
        if (s.IsEmpty) { reason = "Empty slot."; return false; }
        if (count == 0 || count > s.count) count = s.count;
        s.count = (byte)(s.count - count);
        if (s.count == 0)
        {
            bool wasEquipped = (slotIndex == equippedSlot);
            s = default;
            if (wasEquipped) { equippedSlot = -1; equippedWeaponId = UnarmedSentinel; }
        }
        return true;
    }
}
```

- [ ] **Step 5: Refresh + run tests**

`refresh_unity scope=all`, `read_console types=error` → 0 errors. Then `run_tests mode=EditMode test_filter=InventoryTests`, then `get_test_job job_id=<id> wait_timeout=60 include_failed_tests=true`. Expect all tests passing.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Scripts/Inventory/Inventory.cs Assets/_Scripts/Inventory/Inventory.cs.meta Assets/Tests/EditMode/Inventory/Minifantasy.Inventory.Tests.asmdef Assets/Tests/EditMode/Inventory/Minifantasy.Inventory.Tests.asmdef.meta Assets/Tests/EditMode/Inventory/InventoryTests.cs Assets/Tests/EditMode/Inventory/InventoryTests.cs.meta
git commit -m "Inventory: pure Inventory class + 20 unit tests (TryGive/Equip/Use/Drop)"
```

---

## Task 3: `ConsumableDefinition` + `ConsumableCatalog` SOs + empty catalog asset

The SO pair that consumables resolve through, parallel to `AttackDefinition` + `WeaponCatalog`. Both live in default Assembly-CSharp because `ConsumableDefinition` references `StatusEffectAsset` (also default). Empty catalog asset is created so `Game.cs` has something to wire — Ryan populates entries.

**Files:**
- Create: `Assets/_Scripts/Combat/ConsumableDefinition.cs`
- Create: `Assets/_Scripts/Combat/ConsumableCatalog.cs`
- Create (via Unity): `Assets/_Combat/ConsumableCatalog.asset`

- [ ] **Step 1: Create `ConsumableDefinition.cs`**

```csharp
using UnityEngine;

// One consumable item authored as data. Applied to the user on /use via the existing status-effect framework.
// Lives in default Assembly-CSharp because StatusEffectAsset is there (visual data forbids Combat/Core).
[CreateAssetMenu(menuName = "Minifantasy/Consumable Definition", fileName = "Consumable")]
public sealed class ConsumableDefinition : ScriptableObject
{
    public string displayName;
    [TextArea] public string description;
    public StatusEffectAsset onUseEffect;       // applied to user on /use (StatusLogic.Apply)
    [Range(1, 255)] public byte maxStack = 64;
}
```

- [ ] **Step 2: Create `ConsumableCatalog.cs`**

```csharp
using UnityEngine;

// Shared byte-id <-> ConsumableDefinition map, parallel to WeaponCatalog. Wired on Game.
[CreateAssetMenu(menuName = "Minifantasy/Consumable Catalog", fileName = "ConsumableCatalog")]
public sealed class ConsumableCatalog : ScriptableObject
{
    public ConsumableDefinition[] entries;

    public ConsumableDefinition Get(byte id) =>
        entries != null && id < entries.Length ? entries[id] : null;

    public int IndexOf(ConsumableDefinition def)
    {
        if (entries == null) return -1;
        for (int i = 0; i < entries.Length; i++) if (entries[i] == def) return i;
        return -1;
    }
}
```

- [ ] **Step 3: Refresh + verify compile**

`refresh_unity scope=all`, `read_console types=error` → 0 errors.

- [ ] **Step 4: Create empty catalog asset**

Use the unity-mcp tool `manage_asset` (or `execute_menu_item` for `Assets/Create/Minifantasy/Consumable Catalog`) to create `Assets/_Combat/ConsumableCatalog.asset` with no entries. Don't author definitions — that's Ryan's job per [[feedback_biome_assets]].

Concretely:
```
manage_asset action=create path=Assets/_Combat/ConsumableCatalog.asset assetType=ConsumableCatalog
```
or (if action=create isn't supported for SOs) trigger the menu item:
```
execute_menu_item menuPath="Assets/Create/Minifantasy/Consumable Catalog"
```
then rename and move the created asset to `Assets/_Combat/ConsumableCatalog.asset`. Verify with `manage_asset action=read path=Assets/_Combat/ConsumableCatalog.asset` that `entries` is empty.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Combat/ConsumableDefinition.cs Assets/_Scripts/Combat/ConsumableDefinition.cs.meta Assets/_Scripts/Combat/ConsumableCatalog.cs Assets/_Scripts/Combat/ConsumableCatalog.cs.meta Assets/_Combat/ConsumableCatalog.asset Assets/_Combat/ConsumableCatalog.asset.meta
git commit -m "Inventory: ConsumableDefinition + ConsumableCatalog SOs + empty catalog asset"
```

---

## Task 4: Wire `ConsumableCatalog` into `Game`

`Game.cs` is the singleton that exposes catalogs to runtime systems. Add the field, expose an accessor, and wire the asset reference on the Game prefab.

**Files:**
- Modify: `Assets/_Scripts/Game.cs`
- Modify (via Unity Inspector): `Assets/_Prefabs/Game.prefab` (or wherever Game lives — confirm via Glob first)

- [ ] **Step 1: Locate the WeaponCatalog wiring on Game**

Use `Grep` to find `WeaponCatalog` references in `Game.cs`. The pattern is `[SerializeField] WeaponCatalog ...` + a public accessor. Add `ConsumableCatalog` next to it using identical shape.

- [ ] **Step 2: Add the field + accessor**

In `Game.cs`, find the WeaponCatalog line and add immediately after:

```csharp
[SerializeField] ConsumableCatalog consumableCatalog;
public ConsumableCatalog ConsumableCatalog => consumableCatalog;
```

(Field name and accessor name match the WeaponCatalog convention found in step 1; adjust if needed.)

- [ ] **Step 3: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors.

- [ ] **Step 4: Wire the asset reference on the Game prefab/scene object**

Find the Game prefab (`Glob Assets/**/Game.prefab`) or its scene instance. Use `manage_components` or `manage_prefabs` to set the new `consumableCatalog` field to `Assets/_Combat/ConsumableCatalog.asset`. Verify via `manage_prefabs action=read` that the field is populated.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Game.cs <Game.prefab path> <Game.prefab.meta path if changed>
git commit -m "Inventory: wire ConsumableCatalog on Game"
```

---

## Task 5: `Combatant` unarmed sentinel (weaponId = 255 default)

Set `Combatant.weaponId`'s default to the unarmed sentinel. The existing null-check in `AttackSimSystem.cs:57` (`if (def != null)`) handles `WeaponCatalog.Get(255) == null` cleanly — no other code changes needed.

**Files:**
- Modify: `Assets/_Scripts/Combat/Combatant.cs`

- [ ] **Step 1: Update the field declaration**

Edit `Assets/_Scripts/Combat/Combatant.cs:27`:

Find:
```csharp
public byte weaponId;                   // equipped weapon (catalog id)
```

Replace with:
```csharp
public byte weaponId = 255;             // equipped weapon (catalog id); 255 = unarmed sentinel (Get returns null)
```

- [ ] **Step 2: Refresh + run combat tests**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors. Then `run_tests mode=EditMode` (the full suite) → `get_test_job` → expect all 115+ tests still passing. If any combat test creates a `Combatant` and assumes a specific weapon, fix the test to set `weaponId` explicitly (don't revert the default).

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/Combat/Combatant.cs
git commit -m "Inventory: Combatant.weaponId defaults to 255 (unarmed sentinel)"
```

---

## Task 6: Add `Inventory` field to `ServerPlayer`

One-liner — add the field initializer. No tests; the inventory is exercised by Inventory's own tests, and integration is covered by manual host-test.

**Files:**
- Modify: `Assets/_Scripts/Net/PlayerRegistry.cs`

- [ ] **Step 1: Add the field**

In `Assets/_Scripts/Net/PlayerRegistry.cs`, find the `ServerPlayer` class. Add after the last `public readonly` field (after the existing `motion` field at line 9):

```csharp
public readonly Inventory inventory = new();
```

- [ ] **Step 2: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Assets/_Scripts/Net/PlayerRegistry.cs
git commit -m "Inventory: ServerPlayer.inventory field"
```

---

## Task 7: `TargetedLogClientRpc` + `GameLog.PostTo` helper

Per-player chat output primitive. Lives on `ReplicationHub` (which is the only server-side `NetworkBehaviour` we can use for ClientRpcs); `GameLog.PostTo` is a static convenience wrapper.

**Files:**
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs`
- Modify: `Assets/_Scripts/Core/GameLog.cs`

- [ ] **Step 1: Add the ClientRpc on `ReplicationHub`**

In `Assets/_Scripts/Net/ReplicationHub.cs`, find a logical home near the existing ClientRpcs (search for `[ClientRpc]`). Add:

```csharp
[ClientRpc(RequireOwnership = false)]
void TargetedLogClientRpc(byte outputType, string message, ClientRpcParams target = default)
{
    // Receiving client posts to its local GameLog. The Send.TargetClientIds in `target` ensures
    // only one client receives this.
    GameLog.Post((OutputType)outputType, message);
}

// Server-side entry point used by GameLog.PostTo.
public void ServerPostTargeted(ulong clientId, OutputType type, string message)
{
    if (!IsServer) return;
    var p = new ClientRpcParams { Send = { TargetClientIds = new[] { clientId } } };
    TargetedLogClientRpc((byte)type, message, p);
}
```

If `ReplicationHub` uses pooled `ClientRpcParams` (check the file for an existing pattern), reuse that pattern instead of allocating a new array per call.

- [ ] **Step 2: Add `GameLog.PostTo`**

In `Assets/_Scripts/Core/GameLog.cs`, add as a static method on the existing `GameLog` class (next to the existing `Post`):

```csharp
// Server-side: route a per-player message to one client. The recipient's local GameLog.Post
// fires on receipt, so it appears in their chat with the same OutputType styling as any other entry.
public static void PostTo(ulong clientId, OutputType type, string message)
{
    var hub = ReplicationHub.Instance;
    if (hub != null) hub.ServerPostTargeted(clientId, type, message);
}
```

- [ ] **Step 3: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/Net/ReplicationHub.cs Assets/_Scripts/Core/GameLog.cs
git commit -m "Inventory: per-player TargetedLogClientRpc + GameLog.PostTo helper"
```

---

## Task 8: `InventoryChangedClientRpc` + connect-time initial sync

The owner-only inventory sync. Sent on every successful mutation (set up in tasks 11-14) and once on player connect.

**Files:**
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs`

- [ ] **Step 1: Add the ClientRpc**

In `ReplicationHub.cs`, near `TargetedLogClientRpc`:

```csharp
[ClientRpc(RequireOwnership = false)]
void InventoryChangedClientRpc(InventorySlot[] slots, ClientRpcParams target = default)
{
    // Client mirror handler — populated in Task 9 (LocalPlayer changes).
    var lp = LocalPlayer.Instance;
    if (lp != null) lp.OnInventoryChanged(slots);
}

// Server-side entry: emit the full slot array to one client. Used by Inventory mutation commands
// and once on connect.
public void ServerEmitInventory(ulong clientId, InventorySlot[] slots)
{
    if (!IsServer) return;
    var p = new ClientRpcParams { Send = { TargetClientIds = new[] { clientId } } };
    InventoryChangedClientRpc(slots, p);
}
```

- [ ] **Step 2: Wire the connect-time sync**

Find `EnsurePlayer(ulong clientId)` in `ReplicationHub.cs` (around line 66 per the survey). At the END of the method, after the ServerPlayer is registered, emit the initial inventory:

```csharp
// Initial inventory sync for the newly-connected client (empty array, sets up the client mirror).
ServerEmitInventory(clientId, sp.inventory.slots);
```

Where `sp` is the `ServerPlayer` reference returned by `registry.Add(...)`. If the existing code doesn't keep that reference, capture it: `var sp = registry.Add(clientId, cell, pos, gm?.PlayerCharacter);`.

- [ ] **Step 3: Refresh + verify compile**

`refresh_unity scope=scripts`. **Expect compile errors** about `LocalPlayer.OnInventoryChanged` not existing — that method lands in Task 9. Confirm the errors are only that one missing method; if so, proceed. (We intentionally let this break briefly because the two halves are atomic with each other; we re-commit cleanly at the end of Task 9.)

Alternative: stub `LocalPlayer.OnInventoryChanged(InventorySlot[])` with an empty body now and fully implement in Task 9. Recommended — it keeps each commit compiling.

- [ ] **Step 4: If you chose the stub path, add empty stub in `LocalPlayer.cs`**

```csharp
// Stub — full implementation lands in Task 9.
public void OnInventoryChanged(InventorySlot[] slots) { }
```

- [ ] **Step 5: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors.

- [ ] **Step 6: Commit**

```bash
git add Assets/_Scripts/Net/ReplicationHub.cs Assets/_Scripts/Net/LocalPlayer.cs
git commit -m "Inventory: InventoryChangedClientRpc + connect-time sync (stub client handler)"
```

---

## Task 9: `LocalPlayer` inventory mirror + remove `weapons[]` array & hotkey block

Replace the hardcoded prefab `weapons[]` array and the number-key 1-9,0 picker with a snapshot-derived mirror. The mirror is display-only (read by `inv`, `equip`-by-name, etc.). Also handle the `enchant` debug command's now-broken `EquippedWeapon` reference.

**Files:**
- Modify: `Assets/_Scripts/Net/LocalPlayer.cs`
- Modify: `Assets/_Scripts/CommandBootstrap.cs` (the `enchant` command at line 136-170)

- [ ] **Step 1: Update `LocalPlayer.cs`**

Read the current file to confirm exact line numbers, then:

a) **Remove** the `[SerializeField] AttackDefinition[] weapons` field declaration (lines ~19-21 per the survey).

b) **Remove** the number-key 1-9,0 picker block in `Update()` (around lines ~88-90 per the survey). Search the file for `KeyCode.Alpha1` to find it.

c) **Remove** the `EquippedWeapon` property and the `currentAttack` swap-on-keypress logic.

d) **Add** the mirror field and the handler. Near the top of the class, after the existing fields:

```csharp
public readonly InventorySlot[] inventoryMirror = new InventorySlot[Inventory.Capacity];

public void OnInventoryChanged(InventorySlot[] slots)
{
    if (slots == null) return;
    int n = slots.Length < Inventory.Capacity ? slots.Length : Inventory.Capacity;
    for (int i = 0; i < n; i++) inventoryMirror[i] = slots[i];
    for (int i = n; i < Inventory.Capacity; i++) inventoryMirror[i] = default;
}
```

e) `InputCommand.weaponId` continues to be sent for now — set it to 0 in the client send path (the server ignores it for equip purposes; see Task 12 for the server-side flow). Find where `InputCommand` is constructed in `LocalPlayer.cs` and replace any `weaponId = <currentAttack lookup>` with `weaponId = 0`. (A follow-up PR later drops the field entirely — see spec §8.)

- [ ] **Step 2: Patch the `enchant` command**

In `CommandBootstrap.cs`, the `enchant` command at line ~140-141 references `lp.EquippedWeapon`. Since that property is gone, the command now needs to error out cleanly. Replace the body's first lines:

```csharp
var lp = LocalPlayer.Instance;
var weapon = lp != null ? lp.EquippedWeapon : null;
if (weapon == null) return CommandResult.Bad("No weapon equipped.");
```

with:

```csharp
return CommandResult.Bad("'enchant' is being rewired for the new inventory system — see spec §8 follow-up.");
```

The `enchant` registration stays in place (so `help` shows it) but errors when invoked. Rewiring to use the equipped slot's `AttackDefinition` is the spec §8 follow-up.

- [ ] **Step 3: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors. If any other file referenced `LocalPlayer.weapons` or `LocalPlayer.EquippedWeapon`, fix call sites (or grep for them ahead of time).

```
Grep pattern: "LocalPlayer.*\.(weapons|EquippedWeapon)"
```

- [ ] **Step 4: Verify nothing breaks at runtime (Play mode smoke)**

`manage_editor action=play`. Wait ~8s. `read_console types=error` → 0 entries. The player should still spawn, walk, and… not attack (no weapon equipped yet — expected). `manage_editor action=stop`.

- [ ] **Step 5: Commit**

```bash
git add Assets/_Scripts/Net/LocalPlayer.cs Assets/_Scripts/CommandBootstrap.cs
git commit -m "Inventory: LocalPlayer mirror + remove weapons[] hotkey block (enchant stubbed)"
```

---

## Task 10: Replace `inv` command stub

Reads the local mirror, formats output, posts via `GameLog.Post(OutputType.Inventory, …)`. Pure client-side — no RPC.

**Files:**
- Modify: `Assets/_Scripts/CommandBootstrap.cs`

- [ ] **Step 1: Replace the `inventory` Run delegate**

In `Assets/_Scripts/CommandBootstrap.cs` find the existing `inventory` registration (around line 72-77):

```csharp
r.Register(new Command
{
    Keyword = "inventory", Aliases = new[] { "inv" }, Scope = CommandScope.Inventory, Arg = ArgMode.None,
    Description = "Show your inventory.",
    Run = _ => CommandResult.Ok("Your inventory is empty.", keepOpen: true, output: OutputType.Inventory),
});
```

Replace with:

```csharp
r.Register(new Command
{
    Keyword = "inventory", Aliases = new[] { "inv" }, Scope = CommandScope.Inventory, Arg = ArgMode.None,
    Description = "Show your inventory.",
    Run = _ =>
    {
        var lp = LocalPlayer.Instance;
        if (lp == null) return CommandResult.Bad("No player yet.");
        var mirror = lp.inventoryMirror;
        var weapons = Game.Instance != null ? Game.Instance.WeaponCatalog : null;
        var consumables = Game.Instance != null ? Game.Instance.ConsumableCatalog : null;
        var sb = new StringBuilder();
        bool any = false;
        for (int i = 0; i < mirror.Length; i++)
        {
            var s = mirror[i];
            if (s.IsEmpty) continue;
            any = true;
            string name = s.kind == ItemKind.Weapon
                ? (weapons != null && weapons.Get(s.id) != null ? weapons.Get(s.id).name : $"weapon#{s.id}")
                : (consumables != null && consumables.Get(s.id) != null ? consumables.Get(s.id).displayName : $"consumable#{s.id}");
            char tag = s.kind == ItemKind.Weapon ? 'W' : 'C';
            if (s.count > 1) sb.AppendLine($"[{i + 1}] {name} x{s.count} ({tag})");
            else sb.AppendLine($"[{i + 1}] {name} ({tag})");
        }
        if (!any) return CommandResult.Ok("Your inventory is empty.", keepOpen: true, output: OutputType.Inventory);
        // v1 doesn't surface the equipped marker — the server's last `equip` confirmation log is the signal.
        return CommandResult.Ok(sb.ToString().TrimEnd(), keepOpen: true, output: OutputType.Inventory);
    },
});
```

The exact accessor name `Game.Instance.WeaponCatalog` / `ConsumableCatalog` — confirm via the Game.cs reads in Task 4 (the WeaponCatalog accessor naming is the source of truth). If the WeaponCatalog field is private with no accessor, add one (`public WeaponCatalog WeaponCatalog => weaponCatalog;`) and commit it as part of this task.

Make sure `using System.Text;` is at the top of `CommandBootstrap.cs` (it is — `StringBuilder` is already used by the `effect` command).

- [ ] **Step 2: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors.

- [ ] **Step 3: Play-mode smoke test**

`manage_editor action=play`. Open chat (look up keybinding — likely Enter or `~`). Type `inv`. Expect: `Your inventory is empty.` `manage_editor action=stop`.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/CommandBootstrap.cs Assets/_Scripts/Game.cs
git commit -m "Inventory: replace inv stub — reads LocalPlayer mirror, formats slots"
```

(Include `Game.cs` only if you needed to add the WeaponCatalog accessor.)

---

## Task 11: `give` command (host-only) + ServerRpc

Host runs `give weapon 0 3` → server adds 3 swords to host's inventory → server emits `InventoryChangedClientRpc` + `TargetedLogClientRpc`. v1 targets the host's own clientId only — multi-target form is a follow-up.

**Files:**
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs`
- Modify: `Assets/_Scripts/CommandBootstrap.cs`

- [ ] **Step 1: Add the ServerRpc on `ReplicationHub`**

In `ReplicationHub.cs`, near the other ServerRpcs:

```csharp
[ServerRpc(RequireOwnership = false)]
public void GiveSelfServerRpc(byte kind, byte id, byte count, ServerRpcParams rpc = default)
{
    if (!IsServer) return;
    ulong sender = rpc.Receive.SenderClientId;
    if (sender != NetworkManager.ServerClientId) {
        GameLog.PostTo(sender, OutputType.System, "give is host-only.");
        return;
    }
    if (!registry.TryGet(sender, out var sp)) return;

    var k = (ItemKind)kind;
    byte maxStack = ResolveMaxStack(k, id);
    if (maxStack == 0) {
        GameLog.PostTo(sender, OutputType.System, "Unknown item.");
        return;
    }

    bool ok = sp.inventory.TryGive(k, id, count, maxStack, out byte added, out string reason);
    string name = ResolveDisplayName(k, id);
    if (ok && added == count) GameLog.PostTo(sender, OutputType.Inventory, $"Received {name}{(added > 1 ? $" x{added}" : "")}.");
    else if (ok)               GameLog.PostTo(sender, OutputType.Inventory, $"Inventory full — added {added} of {count} {name}.");
    else                       GameLog.PostTo(sender, OutputType.System, reason ?? "Couldn't add item.");
    ServerEmitInventory(sender, sp.inventory.slots);
}

byte ResolveMaxStack(ItemKind kind, byte id)
{
    var gm = Game.Instance;
    if (gm == null) return 0;
    if (kind == ItemKind.Weapon)     return gm.WeaponCatalog != null && gm.WeaponCatalog.Get(id) != null ? (byte)1 : (byte)0;
    if (kind == ItemKind.Consumable) return gm.ConsumableCatalog != null && gm.ConsumableCatalog.Get(id) != null ? gm.ConsumableCatalog.Get(id).maxStack : (byte)0;
    return 0;
}

string ResolveDisplayName(ItemKind kind, byte id)
{
    var gm = Game.Instance;
    if (gm == null) return $"item#{id}";
    if (kind == ItemKind.Weapon)     return gm.WeaponCatalog?.Get(id) != null ? gm.WeaponCatalog.Get(id).name : $"weapon#{id}";
    if (kind == ItemKind.Consumable) return gm.ConsumableCatalog?.Get(id) != null ? gm.ConsumableCatalog.Get(id).displayName : $"consumable#{id}";
    return $"item#{id}";
}
```

The two helpers are reused by Tasks 12-14. If you'd rather move them to a separate file (e.g., `ItemResolver.cs`), do that — keeps `ReplicationHub.cs` from bloating. Otherwise leave them as private methods on the hub.

- [ ] **Step 2: Add the `give` command registration**

In `CommandBootstrap.cs`, after the `inventory` registration, register `give`:

```csharp
r.Register(new Command
{
    Keyword = "give", Scope = CommandScope.Inventory, Arg = ArgMode.Required,
    Description = "(host) Give yourself an item. Usage: give weapon|consumable <id|name> [count]",
    Usage = "give weapon|consumable <id|name> [count]",
    Run = arg =>
    {
        var hub = ReplicationHub.Instance;
        if (hub == null || !hub.IsHost) return CommandResult.Bad("give is host-only.");
        var parts = arg.Split(new[] { ' ' }, 3, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return CommandResult.Bad("Usage: give weapon|consumable <id|name> [count]");

        ItemKind kind;
        switch (parts[0].ToLowerInvariant())
        {
            case "w": case "weapon": kind = ItemKind.Weapon; break;
            case "c": case "consumable": kind = ItemKind.Consumable; break;
            default: return CommandResult.Bad("First arg must be 'weapon' or 'consumable'.");
        }

        if (!TryResolveItemId(kind, parts[1], out byte id, out string resolveReason))
            return CommandResult.Bad(resolveReason);

        byte count = 1;
        if (parts.Length == 3 && (!byte.TryParse(parts[2], out count) || count == 0))
            return CommandResult.Bad("Count must be 1..255.");

        hub.GiveSelfServerRpc((byte)kind, id, count);
        return CommandResult.Ok(keepOpen: true);
    },
});
```

You'll also need the static `TryResolveItemId` helper. Add at the bottom of `CommandBootstrap`:

```csharp
static bool TryResolveItemId(ItemKind kind, string idOrName, out byte id, out string reason)
{
    id = 0; reason = null;
    if (byte.TryParse(idOrName, out id)) return true;   // numeric id always wins

    var gm = Game.Instance;
    if (gm == null) { reason = "No catalog available."; return false; }

    if (kind == ItemKind.Weapon)
    {
        var c = gm.WeaponCatalog;
        if (c == null || c.weapons == null) { reason = "WeaponCatalog not wired."; return false; }
        int found = -1, hits = 0;
        for (int i = 0; i < c.weapons.Length; i++)
        {
            var w = c.weapons[i];
            if (w == null) continue;
            if (string.Equals(w.name, idOrName, System.StringComparison.OrdinalIgnoreCase)
             || w.name.IndexOf(idOrName, System.StringComparison.OrdinalIgnoreCase) >= 0)
            { found = i; hits++; }
        }
        if (hits == 0) { reason = $"No weapon matches '{idOrName}'."; return false; }
        if (hits > 1) { reason = $"Multiple weapons match '{idOrName}'. Be more specific."; return false; }
        id = (byte)found; return true;
    }
    else // Consumable
    {
        var c = gm.ConsumableCatalog;
        if (c == null || c.entries == null) { reason = "ConsumableCatalog not wired."; return false; }
        int found = -1, hits = 0;
        for (int i = 0; i < c.entries.Length; i++)
        {
            var d = c.entries[i];
            if (d == null) continue;
            string n = d.displayName ?? d.name;
            if (string.Equals(n, idOrName, System.StringComparison.OrdinalIgnoreCase)
             || n.IndexOf(idOrName, System.StringComparison.OrdinalIgnoreCase) >= 0)
            { found = i; hits++; }
        }
        if (hits == 0) { reason = $"No consumable matches '{idOrName}'."; return false; }
        if (hits > 1) { reason = $"Multiple consumables match '{idOrName}'. Be more specific."; return false; }
        id = (byte)found; return true;
    }
}
```

- [ ] **Step 3: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/Net/ReplicationHub.cs Assets/_Scripts/CommandBootstrap.cs
git commit -m "Inventory: give command (host-only) + GiveSelfServerRpc"
```

---

## Task 12: `equip` command + ServerRpc + overworld gate

`equip <slot|name>` — server gates on `!sp.inInstance`, calls `Inventory.TryEquip`, copies `inventory.equippedWeaponId` into `sp.weaponId`. Snapshots already replicate `weaponId` so remote clients pick up the change.

**Files:**
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs`
- Modify: `Assets/_Scripts/CommandBootstrap.cs`

- [ ] **Step 1: Add `EquipRequestServerRpc`**

In `ReplicationHub.cs`, near `GiveSelfServerRpc`:

```csharp
[ServerRpc(RequireOwnership = false)]
public void EquipRequestServerRpc(int slotIndex, ServerRpcParams rpc = default)
{
    if (!IsServer) return;
    ulong sender = rpc.Receive.SenderClientId;
    if (!registry.TryGet(sender, out var sp)) return;

    if (sp.inInstance) {
        GameLog.PostTo(sender, OutputType.System, "Can't switch weapons inside an instance.");
        return;
    }
    if (!sp.inventory.TryEquip(slotIndex, out string reason)) {
        GameLog.PostTo(sender, OutputType.System, reason);
        return;
    }
    sp.weaponId = sp.inventory.equippedWeaponId;
    string name = ResolveDisplayName(ItemKind.Weapon, sp.weaponId);
    GameLog.PostTo(sender, OutputType.Inventory, $"Equipped {name}.");
    ServerEmitInventory(sender, sp.inventory.slots);
}
```

- [ ] **Step 2: Add the `equip` command**

In `CommandBootstrap.cs`:

```csharp
r.Register(new Command
{
    Keyword = "equip", Scope = CommandScope.Inventory, Arg = ArgMode.Required,
    Description = "Equip a weapon by slot number or name. Overworld only.",
    Usage = "equip <slot|name>",
    Run = arg =>
    {
        var lp = LocalPlayer.Instance;
        var hub = ReplicationHub.Instance;
        if (lp == null || hub == null) return CommandResult.Bad("Not connected.");
        if (!TryResolveSlotArg(lp.inventoryMirror, arg, out int slotIndex, out string reason))
            return CommandResult.Bad(reason);
        hub.EquipRequestServerRpc(slotIndex);
        return CommandResult.Ok(keepOpen: true);
    },
});
```

Add the static slot-resolver helper at the bottom of `CommandBootstrap.cs`:

```csharp
// Resolves "3" → slot 2 (0-based), or "iron sword" → first matching slot by item name. Returns -1 on miss.
static bool TryResolveSlotArg(InventorySlot[] mirror, string arg, out int slotIndex, out string reason)
{
    slotIndex = -1; reason = null;
    arg = arg.Trim();
    if (int.TryParse(arg, out int n)) {
        if (n < 1 || n > mirror.Length) { reason = $"Slot must be 1..{mirror.Length}."; return false; }
        slotIndex = n - 1;
        if (mirror[slotIndex].IsEmpty) { reason = "That slot is empty."; return false; }
        return true;
    }
    // Name match — walk the mirror, look up display names via catalogs
    var gm = Game.Instance;
    int found = -1, hits = 0;
    for (int i = 0; i < mirror.Length; i++)
    {
        var s = mirror[i];
        if (s.IsEmpty) continue;
        string name = null;
        if (s.kind == ItemKind.Weapon && gm?.WeaponCatalog?.Get(s.id) != null)
            name = gm.WeaponCatalog.Get(s.id).name;
        else if (s.kind == ItemKind.Consumable && gm?.ConsumableCatalog?.Get(s.id) != null)
        {
            var d = gm.ConsumableCatalog.Get(s.id);
            name = d.displayName ?? d.name;
        }
        if (name == null) continue;
        if (string.Equals(name, arg, System.StringComparison.OrdinalIgnoreCase)
         || name.IndexOf(arg, System.StringComparison.OrdinalIgnoreCase) >= 0)
        { found = i; hits++; }
    }
    if (hits == 0) { reason = $"No item matches '{arg}'."; return false; }
    if (hits > 1) { reason = $"Multiple items match '{arg}'. Be more specific."; return false; }
    slotIndex = found; return true;
}
```

- [ ] **Step 3: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/Net/ReplicationHub.cs Assets/_Scripts/CommandBootstrap.cs
git commit -m "Inventory: equip command + ServerRpc + overworld gate"
```

---

## Task 13: `use` command + ServerRpc + status effect apply

`use <slot|name>` — server calls `Inventory.TryUse`, then applies the consumable's `onUseEffect` via `StatusLogic.Apply` (the existing framework that `effect` and weapon on-hit use).

**Files:**
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs`
- Modify: `Assets/_Scripts/CommandBootstrap.cs`

- [ ] **Step 1: Add `UseRequestServerRpc`**

In `ReplicationHub.cs`:

```csharp
[ServerRpc(RequireOwnership = false)]
public void UseRequestServerRpc(int slotIndex, ServerRpcParams rpc = default)
{
    if (!IsServer) return;
    ulong sender = rpc.Receive.SenderClientId;
    if (!registry.TryGet(sender, out var sp)) return;

    if (slotIndex < 0 || slotIndex >= Inventory.Capacity) {
        GameLog.PostTo(sender, OutputType.System, "No such slot.");
        return;
    }
    var slot = sp.inventory.slots[slotIndex];   // capture id before TryUse mutates
    if (!sp.inventory.TryUse(slotIndex, out string reason)) {
        GameLog.PostTo(sender, OutputType.System, reason);
        return;
    }

    var gm = Game.Instance;
    var def = gm?.ConsumableCatalog?.Get(slot.id);
    if (def != null && def.onUseEffect != null && gm.StatusCatalog != null)
    {
        var effectDef = gm.StatusCatalog.Defs != null && (int)def.onUseEffect.kind < gm.StatusCatalog.Defs.Length
                        ? gm.StatusCatalog.Defs[(int)def.onUseEffect.kind] : null;
        if (effectDef != null) StatusLogic.Apply(sp.status, effectDef, 0u, self: true, durationOverride: -1);
    }
    string name = def != null ? (def.displayName ?? def.name) : $"consumable#{slot.id}";
    GameLog.PostTo(sender, OutputType.Inventory, $"Used {name}.");
    ServerEmitInventory(sender, sp.inventory.slots);
}
```

Verify the StatusLogic.Apply signature matches what the `effect` debug command uses (`CommandBootstrap.cs:129`) — copy the same call shape. The `self: true` flag matches the existing self-apply semantics from the `effect` command.

- [ ] **Step 2: Add the `use` command**

```csharp
r.Register(new Command
{
    Keyword = "use", Scope = CommandScope.Inventory, Arg = ArgMode.Required,
    Description = "Use a consumable by slot number or name.",
    Usage = "use <slot|name>",
    Run = arg =>
    {
        var lp = LocalPlayer.Instance;
        var hub = ReplicationHub.Instance;
        if (lp == null || hub == null) return CommandResult.Bad("Not connected.");
        if (!TryResolveSlotArg(lp.inventoryMirror, arg, out int slotIndex, out string reason))
            return CommandResult.Bad(reason);
        hub.UseRequestServerRpc(slotIndex);
        return CommandResult.Ok(keepOpen: true);
    },
});
```

- [ ] **Step 3: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/Net/ReplicationHub.cs Assets/_Scripts/CommandBootstrap.cs
git commit -m "Inventory: use command + ServerRpc + onUseEffect via StatusLogic.Apply"
```

---

## Task 14: `drop` command + ServerRpc

`drop <slot> [count]` — server calls `Inventory.TryDrop`. No world entity in v1; items are gone.

**Files:**
- Modify: `Assets/_Scripts/Net/ReplicationHub.cs`
- Modify: `Assets/_Scripts/CommandBootstrap.cs`

- [ ] **Step 1: Add `DropRequestServerRpc`**

In `ReplicationHub.cs`:

```csharp
[ServerRpc(RequireOwnership = false)]
public void DropRequestServerRpc(int slotIndex, byte count, ServerRpcParams rpc = default)
{
    if (!IsServer) return;
    ulong sender = rpc.Receive.SenderClientId;
    if (!registry.TryGet(sender, out var sp)) return;

    if (slotIndex < 0 || slotIndex >= Inventory.Capacity) {
        GameLog.PostTo(sender, OutputType.System, "No such slot.");
        return;
    }
    var slot = sp.inventory.slots[slotIndex];   // capture for name
    if (!sp.inventory.TryDrop(slotIndex, count, out string reason)) {
        GameLog.PostTo(sender, OutputType.System, reason);
        return;
    }
    // If dropping the equipped weapon left us unarmed, sync sp.weaponId.
    if (sp.inventory.equippedSlot < 0) sp.weaponId = Inventory.UnarmedSentinel;

    string name = ResolveDisplayName(slot.kind, slot.id);
    byte dropped = count == 0 || count > slot.count ? slot.count : count;
    GameLog.PostTo(sender, OutputType.Inventory, $"Dropped {name}{(dropped > 1 ? $" x{dropped}" : "")}.");
    ServerEmitInventory(sender, sp.inventory.slots);
}
```

- [ ] **Step 2: Add the `drop` command**

```csharp
r.Register(new Command
{
    Keyword = "drop", Scope = CommandScope.Inventory, Arg = ArgMode.Required,
    Description = "Drop items from a slot. Items are destroyed in v1.",
    Usage = "drop <slot> [count]",
    Run = arg =>
    {
        var lp = LocalPlayer.Instance;
        var hub = ReplicationHub.Instance;
        if (lp == null || hub == null) return CommandResult.Bad("Not connected.");
        var parts = arg.Split(new[] { ' ' }, 2, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1) return CommandResult.Bad("Usage: drop <slot> [count]");
        if (!TryResolveSlotArg(lp.inventoryMirror, parts[0], out int slotIndex, out string reason))
            return CommandResult.Bad(reason);
        byte count = 0;   // 0 means "whole stack" on the server side
        if (parts.Length == 2 && !byte.TryParse(parts[1], out count))
            return CommandResult.Bad("Count must be 1..255 or omitted.");
        hub.DropRequestServerRpc(slotIndex, count);
        return CommandResult.Ok(keepOpen: true);
    },
});
```

- [ ] **Step 3: Refresh + verify compile**

`refresh_unity scope=scripts`, `read_console types=error` → 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Assets/_Scripts/Net/ReplicationHub.cs Assets/_Scripts/CommandBootstrap.cs
git commit -m "Inventory: drop command + ServerRpc (no world entity in v1)"
```

---

## Task 15: Manual host-test verification

Spec §7.4 calls out exactly what to validate. The Inventory unit tests cover pure logic; the RPC + netcode plumbing is host-tested manually. After this step, Ryan authors actual `ConsumableDefinition` assets and runs the loop end-to-end.

**Files:** none (verification only).

- [ ] **Step 1: Author one test consumable asset**

In Unity (or via `manage_asset`), create one `ConsumableDefinition.asset` referencing an existing `StatusEffectAsset` (e.g., Fire or Poison). Add it to `ConsumableCatalog.entries[0]`. Set `displayName = "Berserker Brew"` or similar. Keep this asset uncommitted unless Ryan asks to commit it (per [[feedback_biome_assets]]).

- [ ] **Step 2: Enter Play mode**

`manage_editor action=play`. Wait ~8s. `read_console types=error` → 0 entries.

- [ ] **Step 3: Run through the spec §7.4 host-test script**

For each line, type the command and verify the chat output. Single host instance is sufficient for v1 since `give` is host-only and per-player chat is only meaningful with 2+ clients.

1. Type `inv` → expect "Your inventory is empty."
2. Type `give weapon 0 1` → expect "Received <weapon name>."
3. Type `inv` → expect one weapon line.
4. Type `equip 1` → expect "Equipped <weapon name>." Confirm the player can attack and the goblin takes damage.
5. Type `dungeon` (enters underworld instance) → expect "(debug) Descending into the test dungeon."
6. Type `equip 1` → expect "Can't switch weapons inside an instance." Inventory unchanged.
7. Type `leave` → expect overworld return.
8. Type `give consumable 0 3` → expect "Received Berserker Brew x3."
9. Type `inv` → expect a `[N] Berserker Brew x3 (C)` line.
10. Type `dungeon` (re-enter underworld so status applies have somewhere to land), `use 2` → expect "Used Berserker Brew." `effect list` → expect Fire/Poison/whatever active.
11. Type `inv` → expect `[N] Berserker Brew x2 (C)`.
12. Type `drop 2 1` → expect "Dropped Berserker Brew." `inv` → expect count 1.
13. Type `drop 2` → expect "Dropped Berserker Brew." `inv` → that slot empty.
14. Type `give weapon 0 21` → expect "Inventory full — added 20 of 21 ..." (assuming 19 weapons + 1 leftover after step 13).

- [ ] **Step 4: Stop Play, clean up**

`manage_editor action=stop`. `read_console action=clear`. If you authored a test consumable asset that should be persisted (and Ryan agrees), commit separately:

```bash
git add Assets/_Combat/<consumable-asset>.asset Assets/_Combat/<consumable-asset>.asset.meta Assets/_Combat/ConsumableCatalog.asset
git commit -m "Inventory: author Berserker Brew test consumable + add to catalog"
```

Otherwise leave the asset uncommitted (Ryan will curate).

---

## Self-Review Notes

After completing all tasks, before declaring done, run through this list:

1. **Spec coverage check** — every "In" item in spec §2 maps to a task:
   - Server-side `Inventory` class (20 slots) → Task 2
   - Two item categories → Tasks 1 + 3
   - One server-authoritative equipped weapon → Tasks 6 + 12
   - Five chat commands → Tasks 10-14
   - Equip-gate rule → Task 12
   - Targeted log primitive → Task 7
   - Inventory-sync RPC → Task 8
   - Removal of `LocalPlayer.weapons[]` + hotkeys → Task 9
   - Unarmed sentinel → Task 5 (refined from spec's "reserve catalog index 0")

2. **No `LocalPlayer.weapons` or `EquippedWeapon` references remain.** Run `Grep` for both patterns; expect zero matches outside of removed sites.

3. **Every commit compiles.** Tasks 8 and 9 are paired (the LocalPlayer stub in Task 8 keeps things compiling between them). If you deviated, verify the in-between commits compile.

4. **InputCommand.weaponId is now vestigial** — flagged in spec §8 as follow-up. Confirm we send 0 (not the now-removed equipped weapon) from the client.

5. **All 115 pre-existing tests still pass.** Run `run_tests mode=EditMode` once more after all tasks.

6. **Manual host-test passed.** Spec §7.4 script ran clean.

7. **Deviation from spec §7.2 (intentional):** Spec listed `CommandParseTests` for `give`/`equip` with assertions on the resulting inventory state. This plan does not add them. Reason: the testable surface (the static `TryResolveItemId` / `TryResolveSlotArg` helpers) reads `Game.Instance` directly, so unit-testing them would require either a Game singleton stub or refactoring the helpers to accept catalogs as parameters. v1 keeps coverage at: deep Inventory unit tests + the spec §7.4 manual host-test. If Ryan wants the resolver tests, a small follow-up extracts pure variants (`TryResolveItemIdPure(WeaponCatalog, ConsumableCatalog, ...)`) and adds a `CommandParseTests` file.

8. **Equipped marker in `inv` output (deferred):** v1's `inv` shows the slot list but not which slot is equipped. The signal is the server's last `equip` confirmation in the chat log. Adding the marker requires the server to send the equipped slot index alongside the slot array — a one-byte addition to the RPC. Trivial follow-up.
