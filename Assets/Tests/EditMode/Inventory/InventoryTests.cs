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
