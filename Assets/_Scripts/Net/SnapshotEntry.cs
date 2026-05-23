using Unity.Netcode;

// One player in a client's snapshot: position + flags. Animation/facing are derived on the client from the
// interpolated ghost motion (like PlayerView does today from the transform), so nothing else is sent.
public struct SnapshotEntry : INetworkSerializable
{
    public ulong id;
    public float x, y;
    public byte flags;             // bit0 = snap (teleport: don't interpolate across the jump); bit1 = inInstance (self entry only)

    public const byte SnapBit = 1, InInstanceBit = 2;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref id);
        s.SerializeValue(ref x);
        s.SerializeValue(ref y);
        s.SerializeValue(ref flags);
    }
}
