using Unity.Netcode;
using UnityEngine;

// One owner tick: raw move + attack edges (packed) + quantized aim + equipped weapon. Primitives only, so the
// pure Movement/Combat asmdefs are never referenced from the wire. The server reconstructs AttackIntent from bits.
public struct InputCommand : INetworkSerializable, ITick
{
    public uint tick;
    public Vector2 rawMove;
    public byte attackBits;   // bit0 pressed, bit1 held, bit2 released, bit3 feint
    public ushort aimAngle;
    public byte weaponId;

    public const byte Pressed = 1, Held = 2, Released = 4, Feint = 8;

    public uint Tick => tick;   // ITick: lets the shared RingBuffer index commands by tick

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref tick);
        s.SerializeValue(ref rawMove);
        s.SerializeValue(ref attackBits);
        s.SerializeValue(ref aimAngle);
        s.SerializeValue(ref weaponId);
    }
}
