using System;
using UnityEngine;

// Deterministic kinematic player-vs-player resolution for ONE underworld room, plus the broadphase overlap query
// the future hitbox layer reuses. Pure (no Time/random/Netcode/Game/physics) — same discipline as
// MovementStep/InstanceStep, so it stays unit-testable and can later run inside client prediction. The caller
// (AttackSimSystem) maps ServerPlayer <-> CollisionBody and pre-sorts bodies by id for deterministic order.
public struct CollisionBody
{
    public ulong id;       // deterministic sort key + write-back key
    public Vector2 pos;    // mutated in place during Resolve
    public float radius;
    public float invMass;  // 0 = pinned (held this tick); 1 = mover; future: stat-driven mass / knockback hook
}

public static class CollisionStep
{
    const float Eps = 1e-5f;

    // Resolve overlaps for one room. bodies[0..count) MUST be pre-sorted by id so iteration order — and thus the
    // result — is deterministic. Mover-yields: a pinned body (invMass 0) holds its ground and a mover absorbs the
    // push; two movers split it by invMass; two pinned bodies (e.g. a spawn overlap) fall back to an equal split
    // so they still separate. Correction is along the contact normal only, so a mover slides around a pinned body.
    public static void Resolve(CollisionBody[] bodies, int count, Func<Vector2, bool> walkable, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                {
                    Vector2 d = bodies[j].pos - bodies[i].pos;
                    float rsum = bodies[i].radius + bodies[j].radius;
                    float dist2 = d.sqrMagnitude;
                    if (dist2 >= rsum * rsum) continue;                        // not overlapping

                    float dist = Mathf.Sqrt(dist2);
                    Vector2 normal = dist > Eps ? d / dist : Vector2.right;     // degenerate same-pos -> fixed axis (i<j => i goes -x)
                    float pen = rsum - dist;

                    float inv = bodies[i].invMass + bodies[j].invMass;
                    float wi, wj;
                    if (inv <= 0f) { wi = 0.5f; wj = 0.5f; }                    // both pinned -> separate gently
                    else { wi = bodies[i].invMass / inv; wj = bodies[j].invMass / inv; }

                    bodies[i].pos = SlideClamp(bodies[i].pos, -normal * (pen * wi), walkable);
                    bodies[j].pos = SlideClamp(bodies[j].pos,  normal * (pen * wj), walkable);
                }
    }

    // Broadphase query seam for the future hitbox layer: fill `results` with indices of bodies whose circle
    // overlaps the (center,radius) probe; returns the count written. Unused by collision itself — v1 ships it so
    // the OnStrike hit seam later becomes "Overlap roommates, then pixel-perfect narrowphase" with no rework.
    public static int Overlap(CollisionBody[] bodies, int count, Vector2 center, float radius, int[] results)
    {
        int n = 0;
        for (int i = 0; i < count && n < results.Length; i++)
        {
            float rsum = bodies[i].radius + radius;
            if ((bodies[i].pos - center).sqrMagnitude < rsum * rsum) results[n++] = i;
        }
        return n;
    }

    // Positional correction with the SAME per-axis wall slide as MovementStep: a body pushed toward a wall slides
    // along it instead of entering non-walkable space (walls win over the push).
    static Vector2 SlideClamp(Vector2 from, Vector2 delta, Func<Vector2, bool> walkable)
    {
        Vector2 next = from;
        Vector2 tryX = new Vector2(from.x + delta.x, next.y);
        if (delta.x != 0f && walkable(tryX)) next.x = tryX.x;
        Vector2 tryY = new Vector2(next.x, from.y + delta.y);
        if (delta.y != 0f && walkable(tryY)) next.y = tryY.y;
        return next;
    }
}
