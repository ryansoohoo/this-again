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
        return Slide(pos, delta, isWalkableAt);
    }

    // Per-axis wall slide: move `from` by `delta`, taking each axis only if its destination is walkable (walls
    // win, so you slide along a wall instead of stopping dead). Shared by movement integration above and the
    // collision push-apart (CollisionStep) so both resolve against walls identically.
    public static Vector2 Slide(Vector2 from, Vector2 delta, Func<Vector2, bool> isWalkableAt)
    {
        Vector2 next = from;
        Vector2 tryX = new Vector2(from.x + delta.x, next.y);
        if (delta.x != 0f && isWalkableAt(tryX)) next.x = tryX.x;
        Vector2 tryY = new Vector2(next.x, from.y + delta.y);
        if (delta.y != 0f && isWalkableAt(tryY)) next.y = tryY.y;
        return next;
    }
}
