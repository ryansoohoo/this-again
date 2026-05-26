# Inventory System — Design

- **Date:** 2026-05-25
- **Status:** Design approved (shape); pending spec review → implementation plan
- **Topic:** Server-authoritative per-player inventory with weapons + consumables, accessible via chat commands only (no UI in v1). Replaces the hardcoded `LocalPlayer.weapons[]` array with a real inventory; introduces the chat/command surface that future item systems extend.

---

## 1. Context & Motivation

**Today.** Each `LocalPlayer` prefab has a `[SerializeField] AttackDefinition[] weapons` array. Number keys 1-9,0 pick a slot, set `currentAttack` client-side, and the chosen `weaponId` is sent every tick via `InputCommand.weaponId`. The server trusts that ID and uses it to step the attack sim. The "equipped weapon" is therefore *client-authored*; players can't acquire, lose, or swap items in any data-driven way.

**Why now.** The combat foundation is complete — [[project_attack_replication]], [[project_status_effects]], [[project_weapon_effects]], and [[project_combatant_foundation]] all landed on `main` over the last two days. The next layer is **items that players acquire, hold, equip, and consume.** A real inventory is the prerequisite for: loot drops, weapon variety in play, consumable testing of the existing status-effect framework, and any future trade/economy work.

**Why a minimal v1.** The user explicitly wants no UI yet — just chat commands. That trims the work to its core: data model, server authority, replication, and command parsing. The chat layer doubles as a real game-feel surface (a roguelike-style item shell) **and** as the debug interface, so we don't pay twice. UI is a future skin over the same RPCs.

**What this unlocks.**
- A real `/use` path that exercises the [[project_status_effects]] framework end-to-end.
- A real `/equip` path that the server validates (no more client-trusted `weaponId`).
- The first per-player chat primitive (targeted `ClientRpc`), reusable for every future personal notification.

---

## 2. Scope

### In
- A server-side `Inventory` class (20 slots, in-memory) on every `ServerPlayer`.
- Two item categories: **Weapons** (existing `AttackDefinition` via `WeaponCatalog`) and **Consumables** (new `ConsumableDefinition` / `ConsumableCatalog` parallel to weapons).
- One equipped weapon per player, server-authoritative, sourced from the inventory (not from a client array).
- Five chat commands: `inv`, `equip`, `use`, `drop`, `give` (host-only).
- Equip-gate rule: `equip` is rejected while the player is inside an instance.
- A new targeted-log primitive: `TargetedLogClientRpc(OutputType, string)` that posts to one client's `GameLog`.
- A new inventory-sync RPC: `InventoryChangedClientRpc(InventorySlot[20])` that the server emits to the owner on every mutation and on connect.
- Removal of `LocalPlayer.weapons[]` and the number-key 1-9,0 picker; client-side `currentAttack` becomes a snapshot-derived mirror.
- Reservation of `WeaponCatalog.weapons[0]` as a null/unarmed sentinel; existing catalog entries shift up by one. `AttackSimSystem` gets a null-check on `WeaponCatalog.Get()` (defensive).

### Out (deferred)
- UI of any kind — no inventory window, no slot widgets, no tooltips. Chat is the only surface.
- Persistence across disconnect or host shutdown — inventories are pure runtime state.
- Loot drops from kills, starter loadouts, or any in-game acquisition path other than the `give` debug command.
- World-drop entities — `drop` deletes the item from inventory; nothing appears in the world.
- Giving items to other players — `give` only targets the host who runs it in v1. The targeted-log primitive is the seam that lets us extend this later.
- Item-targeting consumables (e.g., a scroll cast at another player). All `use` is self-targeted.
- Weapon enchanting via inventory — the existing client-side `enchant` debug command is **not** part of this work. Note: it will break when `LocalPlayer.EquippedWeapon` is removed; flagged in §8.
- Adding new status effects to support consumables. v1 ships with whatever consumables can be expressed using *existing* effects (Bleed, Fire, Fear, Poison, Freeze, Slow). A true Heal effect is a follow-up.

**Non-goal guarantee:** combat behavior is identical for any player who has equipped a weapon. The only behavioral change in combat is that a freshly-joined player has *no* equipped weapon until `give` + `equip` runs — attempting to attack while `weaponId == 0` is a no-op (same as today when the catalog lookup misses).

---

## 3. Architecture

### 3.1 Data model

**`ItemKind` enum** (default assembly, new file `Assets/_Scripts/Inventory/ItemKind.cs`):
```csharp
public enum ItemKind : byte
{
    None       = 0,
    Weapon     = 1,
    Consumable = 2,
    // 3..255 reserved for future categories (Enchantment, Material, Quest, …)
}
```

**`InventorySlot` struct** (same file):
```csharp
public struct InventorySlot
{
    public ItemKind kind;
    public byte id;        // index into WeaponCatalog (kind=Weapon) or ConsumableCatalog (kind=Consumable)
    public byte count;     // 1 for weapons; 1..maxStack for consumables
    public byte _reserved; // padding / future flags
    public bool IsEmpty => kind == ItemKind.None;
}
```

Fixed 4-byte layout, deliberately chosen so a future wire path (or persistence blob) can write `InventorySlot[20]` as a flat 80-byte buffer without per-slot framing.

### 3.2 `ConsumableDefinition` + `ConsumableCatalog`

Mirrors the existing `AttackDefinition` + `WeaponCatalog` pattern.

`Assets/_Scripts/Combat/ConsumableDefinition.cs`:
```csharp
[CreateAssetMenu(menuName = "Combat/Consumable Definition")]
public class ConsumableDefinition : ScriptableObject
{
    public string displayName;
    [TextArea] public string description;
    public StatusEffectAsset onUseEffect;   // applied to user on /use
    [Range(1, 255)] public byte maxStack = 64;
}
```

`Assets/_Scripts/Combat/ConsumableCatalog.cs`:
```csharp
[CreateAssetMenu(menuName = "Combat/Consumable Catalog")]
public class ConsumableCatalog : ScriptableObject
{
    public ConsumableDefinition[] entries;
    public ConsumableDefinition Get(byte id) =>
        entries != null && id < entries.Length ? entries[id] : null;
}
```

`Game.cs` gets a `[SerializeField] ConsumableCatalog consumableCatalog;` next to `WeaponCatalog`. **Footgun parity:** like `WeaponCatalog`, this must be wired on the Game prefab — server-side code reads it through `Game.Instance.ConsumableCatalog`. Per [[feedback_biome_assets]], the catalog asset and its `ConsumableDefinition` entries are user-curated; the implementation plan only creates the empty `.asset` files and lets the user populate them.

### 3.3 `Inventory` class

`Assets/_Scripts/Inventory/Inventory.cs`, default assembly, server-only state. Pure C# class (no MonoBehaviour, no SO) so it's trivially unit-testable.

```csharp
public sealed class Inventory
{
    public const int Capacity = 20;
    public readonly InventorySlot[] slots = new InventorySlot[Capacity];

    public byte equippedWeaponId;   // mirror of ServerPlayer.weaponId; 0 = unarmed
    public int equippedSlot;        // -1 = nothing equipped; otherwise slots[equippedSlot].kind == Weapon

    public bool TryGive(ItemKind kind, byte id, byte count,
                        WeaponCatalog weapons, ConsumableCatalog consumables,
                        out byte added, out string reason);

    public bool TryEquip(int slotIndex, out string reason);
    public bool TryUse(int slotIndex, out string reason);
    public bool TryDrop(int slotIndex, byte count, out string reason);

    public int FindByName(string query, WeaponCatalog w, ConsumableCatalog c,
                          out string ambiguousReason);   // -1 if not found
}
```

**Stacking rules** (TryGive):
- Weapons: never stack. Each `give weapon X` consumes one empty slot, count is forced to 1, and if multiple are requested via `give weapon X 3` the server adds up to 3 separate slots — stopping cleanly when full.
- Consumables: stack into the *first* slot matching `(kind, id)` until it hits `def.maxStack`, then spill into the next empty slot. The `out byte added` tells the caller how many were actually placed (so `give` reports "Received Heal Potion x12" vs. asked-for 20 when inventory ran out of room).

**TryEquip rules:**
- `slotIndex` is 1-based at the command surface, converted to 0-based here.
- Rejects if slot is empty, slot is a Consumable, or `slotIndex` is out of range. **Does not check the overworld gate** — that's the caller's responsibility (see §3.5), so the unit tests don't need a `ServerPlayer` to test equip semantics.
- On success: sets `equippedSlot`, sets `equippedWeaponId = slots[slotIndex].id`. The caller copies `equippedWeaponId` into `ServerPlayer.weaponId` (the `Combatant` field that drives `AttackSimSystem`).

**TryUse rules:**
- Rejects if slot is empty or not a Consumable.
- Decrements `count`; if it reaches 0, the slot becomes empty.
- Returns success — the *caller* (the command handler) is responsible for applying `def.onUseEffect` to the `ServerPlayer.status` via the existing `StatusLogic.Apply`. Same separation as TryEquip: keeps `Inventory` pure.

**TryDrop rules:**
- Default count = the whole stack. Caller passes the parsed user count.
- Subtracts from `count`; clears the slot at 0. If `slotIndex == equippedSlot` and the dropped slot empties, also clears `equippedSlot = -1, equippedWeaponId = 0` (you dropped your equipped weapon → you're now unarmed).

### 3.4 `ServerPlayer` & lifecycle

`PlayerRegistry.cs`:
- `ServerPlayer` gets `public Inventory inventory = new();` field.
- On client connect (existing seam in `ReplicationHub` that creates the `ServerPlayer`): nothing extra — `Inventory` constructs empty. Server sends `InventoryChangedClientRpc(emptySlots)` to the owner so the client mirror initializes.
- On disconnect: existing `ServerPlayer` removal GC's the inventory. No persistence.

The existing `Combatant.weaponId` field (`Assets/_Scripts/Combat/Combatant.cs:27`) is the source of truth for the sim. The inventory's `equippedWeaponId` is a redundant cache; the command handler writes both atomically. (We could remove the inventory copy and just mutate `Combatant.weaponId`, but keeping a local copy lets `Inventory` be unit-tested without a `Combatant` instance.)

### 3.5 Equip gate

The rule: `equip` succeeds only when the player is in the overworld.

The check uses the existing `Combatant.inInstance` bool (`Combatant.cs:16`). The command handler does:

```csharp
if (sp.inInstance) return CommandResult.Bad("Can't switch weapons inside an instance.");
if (!sp.inventory.TryEquip(slotIdx, out var reason)) return CommandResult.Bad(reason);
sp.weaponId = sp.inventory.equippedWeaponId;
```

Two-step rather than baking the gate into `Inventory.TryEquip` so the inventory class stays pure C# and unit-testable without a `ServerPlayer`.

### 3.6 Replication

**Equipped weapon** — no change. Already replicated via `SnapshotEntry.weaponId` (`Assets/_Scripts/Net/SnapshotEntry.cs:14`) in the `AttackingBit` block. Server populates from `Combatant.weaponId` exactly as today.

**Inventory contents** — new dedicated RPC, *not* in the snapshot. Snapshots are tuned for AOI-gated, per-tick state at `snapshotHz` and are wrong for low-frequency bulk data.

```csharp
// In ReplicationHub.cs
[ClientRpc] void InventoryChangedClientRpc(InventorySlot[] slots,   // length always 20
                                            ClientRpcParams target);
```

- Sent only to the owner (`target.Send.TargetClientIds = new[] { sp.entityId }`).
- Sent on: connect (initial sync), every successful `give`/`equip`/`use`/`drop`.
- The client's `LocalPlayer` keeps a `InventorySlot[20] inventoryMirror`. This mirror is **display-only**; the server never reads back from it.

`InventorySlot` needs to be Netcode-serializable. Two options: implement `INetworkSerializable`, or pass as a flat `byte[80]` buffer. The implementation plan picks one — `INetworkSerializable` is more idiomatic, `byte[]` is closer to wire-shape. I'll default to `INetworkSerializable` (sketch in §3.8) unless the plan finds a reason otherwise.

### 3.7 Targeted chat output

**Server-side static helper** (in `GameLog.cs` or a new `GameLog.Server` partial):

```csharp
public static class GameLog
{
    // existing local Post(...) unchanged
    public static void PostTo(ulong clientId, OutputType type, string message)
    {
        // routes via a small ClientRpc on ReplicationHub (which has the NetworkBehaviour);
        // on the receiving client, the RPC handler calls the existing local Post(type, message).
    }
}
```

The actual RPC lives on `ReplicationHub` (the existing server-side `NetworkBehaviour`):

```csharp
[ClientRpc] void TargetedLogClientRpc(OutputType type, string message, ClientRpcParams target);
```

Client-side handler is one line: `GameLog.Post(type, message);`.

**Command convention:**
- **All server-side mutations route their user-visible result through `TargetedLogClientRpc`.** A ServerRpc has no return value in Netcode for GameObjects, so the only way to surface authoritative success/failure text to the caller is a ClientRpc back. The same RPC handles host and remote clients uniformly. The client-side command handler returns `CommandResult.Ok` with no message — the message arrives async via the targeted log.
- Pure-client commands like `inv` (which reads the local mirror only) still use `CommandResult.message` synchronously — no server round-trip, no RPC needed.
- The primitive is therefore exercised heavily in v1 (every `equip`/`use`/`drop`/`give` response), not just future server-initiated notifications.

### 3.8 INetworkSerializable sketch

```csharp
public struct InventorySlot : INetworkSerializable
{
    public ItemKind kind; public byte id; public byte count; public byte _reserved;
    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        byte k = (byte)kind; s.SerializeValue(ref k); kind = (ItemKind)k;
        s.SerializeValue(ref id); s.SerializeValue(ref count); s.SerializeValue(ref _reserved);
    }
}
```

`InventorySlot[]` is sent as a fixed-length 20-element array. Empty slots have `kind == None` and zero everywhere else.

---

## 4. Commands

All in `CommandBootstrap.cs`. Scope = `CommandScope.Inventory` (already enabled by default in `CommandRouter.Active`, per the survey). The dummy `inventory` stub at `CommandBootstrap.cs:76` is **replaced**, not duplicated.

The user prefers bare keywords (no leading `/`), matching the existing convention in `effect`, `enchant`, `dungeon`, etc.

### 4.1 `inv` (alias `inventory`)
- **Args:** none.
- **Auth:** any player.
- **Behavior:** reads `LocalPlayer.Instance.inventoryMirror` and prints one line per non-empty slot:
  ```
  [1] Iron Sword (W)
  [2] Heal Potion x3 (C)
  [3] Fire Scroll x1 (C)
  Equipped: [1] Iron Sword
  ```
  Empty inventory prints `Your inventory is empty.` (matches current stub). Output via `GameLog.Post(OutputType.Inventory, ...)` (already green per `ChatPopup.cs:73`).
- **No server round-trip** — the client mirror is sufficient for display.

### 4.2 `equip <slot|name>`
- **Args:** required. Either a 1-based slot number (`equip 2`) or a fuzzy name match (`equip iron`, `equip "Iron Sword"`).
- **Auth:** any player, targets self.
- **Behavior:**
  1. Client parses the arg, resolves to a slot index via the mirror's name lookup; on ambiguity prints `Multiple matches: Iron Sword, Iron Mace. Be more specific.` and aborts.
  2. Client sends `EquipRequestServerRpc(int slotIndex)`.
  3. Server runs the overworld gate + `Inventory.TryEquip`, writes `sp.weaponId`, emits `InventoryChangedClientRpc` (to update the equipped marker), and replies through `TargetedLogClientRpc` with the success/failure message.
- The local command returns `CommandResult.Ok` with **no message** — the authoritative success/failure text arrives via the server's targeted log. This avoids "Equip pending..." then "Equipped X" duplication and is the simplest path.

### 4.3 `use <slot|name>`
- **Args:** required (slot or name).
- **Auth:** any player, targets self.
- **Behavior:** identical pattern to `equip` — client RPCs to server (`UseRequestServerRpc(int slotIndex)`); server runs `Inventory.TryUse`, calls `StatusLogic.Apply(sp.status, consumableDef.onUseEffect.def, …)`, emits `InventoryChangedClientRpc` + targeted log.

### 4.4 `drop <slot> [count]`
- **Args:** slot required, count optional (default = whole stack).
- **Auth:** any player, targets self.
- **Behavior:** `DropRequestServerRpc(int slotIndex, byte count)` → `Inventory.TryDrop` → emit `InventoryChangedClientRpc` + targeted log. No world entity created.

### 4.5 `give <kind> <id|name> [count]`
- **Args:** `kind` ∈ `{weapon, w, consumable, c}`; `id|name` resolves against the matching catalog; count defaults to 1.
- **Auth:** **host-only.** Uses the same gate pattern as `effect` (`CommandBootstrap.cs:107-109`): check `ReplicationHub.Instance` / `IsHost`; refuse otherwise.
- **Behavior:** Host's client locally validates kind/id/count, sends `GiveSelfServerRpc(ItemKind, byte, byte)`. Server runs `Inventory.TryGive`, emits `InventoryChangedClientRpc` + targeted log (`Received Iron Sword.` / `Inventory full — only 5 of 10 Heal Potions added.`).
- **Targeting other players is out of scope for v1.** Even host-to-host is the only path. The implementation should leave the RPC signature open enough that a future `give <player> ...` form fits naturally.

### 4.6 Scope choice for `equip`/`use`/`drop`/`give`

The existing `inventory` stub uses `CommandScope.Inventory`. The new commands use the same scope so they share availability. The `Inventory` scope is in `CommandRouter.Active` by default (per the survey), so all five commands are always typable — server-side validation is what enforces context (in-instance, host-only, etc.).

This is deliberate: `inv` should work from anywhere (you want to see what you have during combat), and the per-command server checks handle the rest.

---

## 5. File / Asmdef Map

Per [[scripts_architecture]], the `Commands/` folder is its own asmdef and types don't cross that boundary. Inventory lives in the default assembly.

**New files:**
- `Assets/_Scripts/Inventory/ItemKind.cs` — enum + `InventorySlot` struct (default assembly).
- `Assets/_Scripts/Inventory/Inventory.cs` — pure C# class (default assembly).
- `Assets/_Scripts/Combat/ConsumableDefinition.cs` — SO (default assembly, with the other combat defs).
- `Assets/_Scripts/Combat/ConsumableCatalog.cs` — SO.
- `Assets/_Combat/ConsumableCatalog.asset` — **created empty, user populates per [[feedback_biome_assets]].**

**Modified files:**
- `Assets/_Scripts/Combat/Combatant.cs` — no field changes; `weaponId` already exists.
- `Assets/_Scripts/Net/PlayerRegistry.cs` — `ServerPlayer` gets `public Inventory inventory = new();`.
- `Assets/_Scripts/Net/LocalPlayer.cs` — remove `weapons[]` array, the 1-9,0 hotkey block, and the `EquippedWeapon` property. Add `InventorySlot[20] inventoryMirror` and a handler for `InventoryChangedClientRpc`.
- `Assets/_Scripts/Net/InputCommand.cs` — `weaponId` field stays for now (zero out client-side; server ignores it). Cleanup deferred — flagged in §8.
- `Assets/_Scripts/Net/ReplicationHub.cs` — three new ClientRpcs (`InventoryChangedClientRpc`, `TargetedLogClientRpc`) and four new ServerRpcs (`EquipRequestServerRpc`, `UseRequestServerRpc`, `DropRequestServerRpc`, `GiveSelfServerRpc`). The connect path emits the initial inventory sync.
- `Assets/_Scripts/Net/AttackSimSystem.cs` — **no change.** Still reads `sp.weaponId`, which is now sourced from inventory equip.
- `Assets/_Scripts/Core/GameLog.cs` — add `PostTo(ulong, OutputType, string)` static (or a sibling helper); thin wrapper around `ReplicationHub.Instance.TargetedLogClientRpc`.
- `Assets/_Scripts/CommandBootstrap.cs` — replace `inventory` stub; register `equip`, `use`, `drop`, `give`.
- `Assets/_Scripts/Game.cs` — add `[SerializeField] ConsumableCatalog consumableCatalog;` + accessor.

---

## 6. Lifecycle & Edge Cases

**Unarmed sentinel (resolved).** Today `WeaponCatalog.weapons[0]` is a real weapon — a fresh `Combatant.weaponId == 0` would silently equip that weapon, contradicting the "no starter loadout" choice. **Resolution:** as part of this work, **reserve `WeaponCatalog.weapons[0]` as a null/sentinel slot.** Existing catalog entries shift up by one. `AttackSimSystem` is updated to no-op when `WeaponCatalog.Get(weaponId) == null` (defensive regardless). Any code that hardcodes byte `0` as a specific weapon is updated. The implementation plan inventories these call sites; the user re-curates the catalog asset per [[feedback_biome_assets]] (we don't edit it via MCP).

| Scenario | Behavior |
|---|---|
| Player joins with no weapon | `weaponId == 0` → `WeaponCatalog.Get(0)` returns null → `AttackSimSystem` no-ops. Player cannot attack until `give` + `equip`. |
| Drop the equipped weapon | `Inventory.TryDrop` clears `equippedSlot` and zeros `equippedWeaponId`. Server writes 0 into `sp.weaponId`. Player is now unarmed; can't attack until they `equip` again. |
| `give` overflows capacity | TryGive returns success with `added < count`. Targeted log says `Inventory full — added 5 of 10 Heal Potions.` |
| `equip` while in instance | Server rejects with `Can't switch weapons inside an instance.` Inventory is unchanged. |
| `use` while in instance | **Allowed.** Consumables are usable in combat. Only equipping is gated. |
| `use` on a weapon slot | Rejected by `TryUse`; targeted log `Can't use a weapon — try /equip.` |
| `equip` on a consumable slot | Rejected by `TryEquip`; targeted log `Can't equip a consumable — try /use.` |
| Two clients run `inv` simultaneously | Each reads their own client mirror locally; no cross-talk. |
| Host runs `give` while client also exists | Only the host's inventory changes; client's chat is silent. |
| Client tries to spoof equip (e.g., picks slot they don't own) | The ServerRpc receiver implicitly knows the sender's clientId via `ServerRpcParams.Receive.SenderClientId`. Server resolves `sp` from sender; client can't target someone else. |
| Server receives `EquipRequestServerRpc` for a non-existent slot | TryEquip rejects; targeted log `Empty slot.` |
| Inventory mirror diverges from server (e.g., dropped packet) | The next mutation re-syncs the full 20-slot array. v1 doesn't reconcile actively; mismatched-state commands fail server-side and the client picks up the truth on the next `InventoryChangedClientRpc`. |

---

## 7. Testing

Per [[unity_mcp_verification]]: `refresh_unity` → poll `editor_state.isCompiling` → `read_console` for compile errors → run tests via the test runner. The existing suite stands at 115 tests ([[project_combatant_foundation]]) and these add ~15 more.

### 7.1 EditMode unit tests — `InventoryTests` (Assets/_Tests/...)

```
TryGive_EmptyInventory_AddsToFirstSlot
TryGive_StacksConsumables_UpToMaxStack
TryGive_SpillsToNextSlotWhenStackFull
TryGive_WeaponsNeverStack_OneSlotPerCount
TryGive_FullInventory_AddsZero_ReturnsFalse
TryGive_PartiallyFullInventory_AddsSomeUntilFull_OutAddedReflectsAmount

TryEquip_WeaponSlot_UpdatesEquippedWeaponIdAndSlot
TryEquip_ConsumableSlot_RejectsWithReason
TryEquip_EmptySlot_RejectsWithReason
TryEquip_OutOfRangeSlot_RejectsWithReason

TryUse_ConsumableSlot_DecrementsCount
TryUse_ConsumableSlotLastCount_FreesSlot
TryUse_WeaponSlot_RejectsWithReason
TryUse_EmptySlot_RejectsWithReason

TryDrop_PartialCount_KeepsRemainder
TryDrop_FullStack_FreesSlot
TryDrop_EquippedWeapon_ClearsEquippedState

FindByName_ExactMatch_ReturnsSlot
FindByName_PartialMatch_ReturnsSlot
FindByName_Ambiguous_ReturnsNegativeOneWithReason
FindByName_NoMatch_ReturnsNegativeOne
```

### 7.2 EditMode command-parsing tests — `CommandParseTests` (subset)

These run against `CommandRegistry.Execute` directly, with a stub `Game` / `ReplicationHub`.

```
Give_WeaponById_FormsCorrectServerRpcArgs
Give_WeaponByName_FormsCorrectServerRpcArgs
Give_UnknownItem_ReturnsBadUsage
Give_AsNonHost_ReturnsBadUsage_WithHostOnlyMessage
Equip_BySlotNumber_FormsCorrectServerRpcArgs
Equip_ByName_FormsCorrectServerRpcArgs
Equip_AmbiguousName_ReturnsBadUsage_WithDisambiguationReason
Inv_EmptyMirror_PrintsExpectedString
Inv_PopulatedMirror_FormatsTwoColumnsWithEquippedMarker
```

### 7.3 Out of automated test coverage (host-test only)
- The Netcode RPCs themselves arriving and routing to the right client.
- `TargetedLogClientRpc` only firing the affected client.
- The full `give → equip → attack uses new weapon → snapshot reflects new weaponId` chain.
- Equip-gate-in-instance round trip.

### 7.4 Manual host-test script (per [[unity_mcp_verification]])

1. Host + 1 remote client. Both connect. `inv` on both → "Your inventory is empty."
2. Host runs `give weapon 0` → host's chat: `Received <weapon name>.` Remote's chat: silent.
3. Host runs `equip 1` (in overworld) → host's chat: `Equipped <weapon name>.` Host runs an attack → goblin takes damage (existing combat path).
4. Host enters underworld via `dungeon` → host runs `equip 1` → host's chat: `Can't switch weapons inside an instance.` Inventory unchanged.
5. Host exits via `leave` → host runs `give consumable 0 3` → `inv` shows `[2] <consumable> x3 (C)`.
6. Host runs `use 2` → status effect applied (verify with `effect list` inside an instance). `inv` now shows `[2] <consumable> x2 (C)`.
7. Host runs `drop 2 1` → `inv` shows `[2] <consumable> x1 (C)`. Host runs `drop 2` → slot 2 empty.
8. Host runs `give weapon 0 21` → `inv` shows 20 weapons. Targeted log says `Inventory full — added 20 of 21 …`.

---

## 8. Known Follow-ups (not in this work)

- **`enchant` debug command** — currently mutates `LocalPlayer.EquippedWeapon` client-side (`CommandBootstrap.cs:136-170`). When `LocalPlayer.weapons[]` and `EquippedWeapon` are removed, this command breaks. **In this PR:** leave the call site referencing `lp.EquippedWeapon` and let it return its existing "No weapon equipped." error, OR mark it `Disabled` in the registry. **Follow-up PR:** rewire as a server RPC that mutates the catalog asset (host-only debug).
- **`InputCommand.weaponId` field** — becomes vestigial after this work (server reads `Combatant.weaponId` from inventory, not from input). Removing it touches the input-tick wire format. **Follow-up PR:** drop the field. In *this* PR: client sends 0, server ignores.
- **A true Heal status effect** — needed before "Heal Potion" is a sensible item. v1 ships only consumables expressible with the existing six effects.
- **Loot drops from kills.** Goblin death table grants items. Hooks into `OnStrike` / death events.
- **World-drop entities.** `drop` becomes a world action (an item lying on the ground that another player can pick up).
- **Inventory UI.** A real window, slot widgets, drag-and-drop. Built on the same RPCs.
- **Persistence.** Save inventory across disconnects, then across host restarts (forces an identity decision).
- **Multi-target `give`** (e.g., `give @PlayerName weapon iron`). The `TargetedLogClientRpc` primitive built here is exactly what makes this trivial later.

---

## 9. Out-of-Scope Reminders

- **No UI.** No popups, no slot icons, no tooltips. Only chat.
- **No persistence.** Disconnect == reset.
- **No starter loadout.** Players join with empty inventory. `give` is the only acquisition path.
- **No item targeting.** All `use` is self; v1 `give` is host-to-host only.
- **No new status effects.** Consumables ship with existing effects.
