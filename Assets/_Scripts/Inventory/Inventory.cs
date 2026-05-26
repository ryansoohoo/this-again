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
        if (s.kind != ItemKind.Weapon) { reason = "Only weapons can be equipped — try use for consumables."; return false; }
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
