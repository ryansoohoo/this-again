using System;
using UnityEngine;

// The single deterministic kinematic movement step, shared by client prediction, client replay, and the
// server sim. Pure: inputs in, position out. No Time.*, no randomness, no physics — this purity is what makes
// reconciliation's replay match the server. `isWalkableAt(worldPos)` is supplied identically by both sides
// (it wraps Game.WorldToCell + Game.IsWalkable). Collision is per-axis slide so walls don't fully stop you.
public static class MovementStep
{
    public static Vector2 Step(Vector2 pos, Vector2 input, float dt, float speed, Func<Vector2, bool> isWalkableAt)
    {
        Vector2 dir = input.sqrMagnitude > 1f ? input.normalized : input;
        Vector2 delta = dir * (speed * dt);
        if (delta == Vector2.zero) return pos;

        Vector2 next = pos;

        Vector2 tryX = new Vector2(pos.x + delta.x, next.y);
        if (delta.x != 0f && isWalkableAt(tryX)) next.x = tryX.x;

        Vector2 tryY = new Vector2(next.x, pos.y + delta.y);
        if (delta.y != 0f && isWalkableAt(tryY)) next.y = tryY.y;

        return next;
    }
}
