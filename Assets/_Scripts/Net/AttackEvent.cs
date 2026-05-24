using Unity.Netcode;

// A discrete attack transition the server guarantees to deliver (reliable, tick-stamped) to a viewer who can see
// the attacker — the "crisp beat" half of the hybrid wire. The snapshot pose carries continuous state; this never
// drops a Started/Struck/Feinted even if it happens between snapshots. Struck is also the client-side VFX/SFX seam.
public struct AttackEvent : INetworkSerializable
{
    public ulong attackerId;
    public byte kind;        // Started/Struck/Feinted
    public byte weaponId;
    public uint tick;
    public ushort aimAngle;

    public const byte Started = 0, Struck = 1, Feinted = 2;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref attackerId);
        s.SerializeValue(ref kind);
        s.SerializeValue(ref weaponId);
        s.SerializeValue(ref tick);
        s.SerializeValue(ref aimAngle);
    }
}
