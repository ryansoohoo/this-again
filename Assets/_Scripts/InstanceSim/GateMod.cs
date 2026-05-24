using UnityEngine;

// One action-gate value: whether movement / attacking are blocked, plus a free-move speed multiplier
// (1 = full, 0 = rooted). Used two ways: as one source's contribution (see AbilityGate) and as the reduced
// EFFECTIVE gate that the deterministic sim consumes. Pure value type — no scene/SO refs. The effective gate
// is quantized to a single wire byte (Pack/Unpack) so the server and the owning predictor consume the exact
// same value, the same discipline AimQuant uses for aim. moveScale applies to free WASD only, never the lunge.
public struct GateMod
{
    public bool blocksMove;     // movement (WASD) is disabled
    public bool blocksAttack;   // starting an attack is disabled, and an in-progress one is interrupted
    public float moveScale;     // free-move speed multiplier, 0..1

    public static GateMod None => new GateMod { blocksMove = false, blocksAttack = false, moveScale = 1f };

    public bool CanMove => !blocksMove;
    public bool CanAttack => !blocksAttack;

    // Pack the effective gate into one byte: bit0 canMove, bit1 canAttack, bits2-7 moveScale (0..63 -> /63).
    public static byte Pack(GateMod g)
    {
        int q = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(g.moveScale) * 63f), 0, 63);
        return (byte)((g.CanMove ? 1 : 0) | (g.CanAttack ? 2 : 0) | (q << 2));
    }

    public static GateMod Unpack(byte b) => new GateMod
    {
        blocksMove = (b & 1) == 0,
        blocksAttack = (b & 2) == 0,
        moveScale = ((b >> 2) & 0x3F) / 63f,
    };

    // Round-trip through the wire quantization so a value used locally matches the byte sent to the owner.
    public static GateMod Quantize(GateMod g) => Unpack(Pack(g));
}
