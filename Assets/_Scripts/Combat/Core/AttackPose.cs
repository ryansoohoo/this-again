// Pure bit-packing of the remote-renderable attack pose into one byte: phase(3) | frame(3) | dir(2).
// 5 phases fit in 3 bits, attack phases never exceed ~6 frames (fits 3 bits), all weapons use 4 directions (2 bits).
public static class AttackPose
{
    public static byte Pack(AttackPhase phase, int frame, int dir)
        => (byte)(((int)phase & 0x7) | ((frame & 0x7) << 3) | ((dir & 0x3) << 6));

    public static void Unpack(byte b, out AttackPhase phase, out int frame, out int dir)
    {
        phase = (AttackPhase)(b & 0x7);
        frame = (b >> 3) & 0x7;
        dir = (b >> 6) & 0x3;
    }
}
