using UnityEngine;

// Quantize an aim direction to a ushort angle and back. The owner encodes BEFORE predicting and sending, so
// client prediction and the server consume the identical direction (determinism for reconciliation). Pure.
public static class AimQuant
{
    const float Scale = 65536f / 360f;

    public static ushort Encode(Vector2 dir)
    {
        if (dir.sqrMagnitude < 1e-12f) return 0;
        float deg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg; // -180..180
        if (deg < 0f) deg += 360f;
        return (ushort)(Mathf.RoundToInt(deg * Scale) & 0xFFFF);
    }

    public static Vector2 Decode(ushort code)
    {
        float r = (code / Scale) * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(r), Mathf.Sin(r));
    }
}
