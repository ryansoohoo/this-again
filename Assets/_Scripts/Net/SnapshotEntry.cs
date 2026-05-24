using Unity.Netcode;

// One player in a client's snapshot: position + flags. Animation/facing are derived on the client from the
// interpolated ghost motion (like PlayerView does today from the transform), so nothing else is sent.
public struct SnapshotEntry : INetworkSerializable
{
    public ulong id;
    public float x, y;
    public byte flags;             // bit0 = snap (teleport: don't interpolate); bit1 = inInstance (self entry); bit2 = attacking

    // Attack block — only serialized when AttackingBit is set (in-instance attackers). Drives remote rendering and
    // resyncs a viewer who enters mid-swing. The self entry's selfExtra additionally carries owner-reconcile state.
    public byte weaponId;
    public byte pose;              // AttackPose.Pack(phase, frame, dir)
    public ushort residual;        // AimQuant of the locked aim (remote weapon tilt + owner reconcile)
    public byte selfExtra;         // self entry: bit0 windupComplete, bits1-7 quantized cooldown (×20, 0..127)

    public const byte SnapBit = 1, InInstanceBit = 2, AttackingBit = 4;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref x);
        s.SerializeValue(ref y);
        s.SerializeValue(ref flags);
        if ((flags & AttackingBit) != 0)
        {
            s.SerializeValue(ref weaponId);
            s.SerializeValue(ref pose);
            s.SerializeValue(ref residual);
            s.SerializeValue(ref selfExtra);
        }
    }
}
