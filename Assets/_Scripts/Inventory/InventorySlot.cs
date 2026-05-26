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
