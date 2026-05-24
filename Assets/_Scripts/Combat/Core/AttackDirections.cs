using System.Collections.Generic;
using UnityEngine;

// Pure: choose the authored direction nearest the aim, and the residual rotation to point exactly at it.
public static class AttackDirections
{
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
