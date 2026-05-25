using Unity.Netcode;

// One player in a client's snapshot. flags select optional blocks. The viewer's own entry (SelfBit) carries the
// full status block (for owner prediction); remote in-instance entries (InstanceBit, not SelfBit) carry a compact
// effect mask (cosmetic) + HP. Attack block (pose) unchanged. Animation/facing are derived on the client from
// interpolated motion.
public struct SnapshotEntry : INetworkSerializable
{
    public ulong id;
    public float x, y;
    public byte flags;             // bit0 snap; bit1 self; bit2 attacking; bit3 inInstance(member)

    // attack block (AttackingBit)
    public byte weaponId;
    public byte pose;              // AttackPose.Pack(phase, frame, dir)
    public ushort residual;        // AimQuant of the locked aim (remote weapon tilt)

    // in-instance member block (InstanceBit)
    public ushort hp;
    public ushort effectMask;      // remotes: one bit per active StatusKind (cosmetic)

    // self status block (SelfBit): authoritative active effects for owner prediction
    public byte effectCount;
    public byte[] effDefId;
    public ushort[] effRemaining;
    public byte[] effStacks;
    public ushort selfFleeAngle;   // quantized flee dir for the active forced-move effect (self block); 0xFFFF = none

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
            s.SerializeValue(ref selfFleeAngle);
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
