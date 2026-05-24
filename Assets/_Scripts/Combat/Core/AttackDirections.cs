using System.Collections.Generic;
using UnityEngine;

// Pure: choose the authored direction nearest the aim, and the residual rotation to point exactly at it.
public static class AttackDirections
{
    // Canonical authored direction sets. Array index == sprite-sheet row, so these define the one true
    // row mapping for every weapon and every authoring path (the batch tool and the inspector buttons).
    public static readonly Vector2[] Diagonal = { new Vector2(1, -1), new Vector2(-1, -1), new Vector2(1, 1), new Vector2(-1, 1) }; // SE,SW,NE,NW
    public static readonly Vector2[] Cardinal = { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, -1), new Vector2(0, 1) };    // E,W,S,N

    // Build row-indexed DirectionEntry[] from a canonical set (row = array index).
    public static DirectionEntry[] Entries(Vector2[] dirs)
    {
        var e = new DirectionEntry[dirs.Length];
        for (int i = 0; i < dirs.Length; i++) e[i] = new DirectionEntry { canonicalDir = dirs[i], row = i };
        return e;
    }

    public static (int index, float residualDeg) Pick(IReadOnlyList<Vector2> dirs, Vector2 aim)
    {
        if (dirs == null || dirs.Count == 0 || aim.sqrMagnitude < 1e-8f) return (0, 0f);
        Vector2 a = aim.normalized;
        int best = 0;
        float bestDot = float.NegativeInfinity;
        for (int i = 0; i < dirs.Count; i++)
        {
            float d = Vector2.Dot(dirs[i].normalized, a);
            if (d > bestDot) { bestDot = d; best = i; }
        }
        float residual = Vector2.SignedAngle(dirs[best], aim); // CCW-positive; matches Unity Z-euler
        return (best, residual);
    }
}
