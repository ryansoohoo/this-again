using System;
using UnityEngine;

// One deterministic in-instance tick: step the attack, derive the lunge, then step movement (lunge overrides
// WASD). Pure (no Time/random/Netcode). Shared by client predict, client replay, and server sim — the single
// source of truth that keeps all three in lockstep, exactly like MovementStep does for movement alone.
public struct InstanceInput
{
    public Vector2 rawMove;     // raw WASD intent (-1/0/1 per axis); used only when the attack imposes no lunge
    public AttackIntent attack; // reconstructed from wire bits on each side
}

public struct InstanceCtx
{
    public AttackTimeline timeline;
    public PhaseScales scales;
    public float dt;
    public float speed;
    public Func<Vector2, bool> walkable;
}

public static class InstanceStep
{
    public static void Step(ref AttackState atk, ref Vector2 pos, in InstanceInput cmd, in InstanceCtx ctx)
    {
        atk = AttackLogic.Step(atk, cmd.attack, ctx.timeline, ctx.scales, ctx.dt);
        Vector2? lunge = AttackLogic.LungeVelocity(atk, ctx.timeline);  // null = idle, zero = rooted windup, vec = lunge
        Vector2 move = lunge ?? cmd.rawMove;                            // lunge overrides WASD
        pos = MovementStep.Step(pos, move, ctx.dt, ctx.speed, ctx.walkable);
    }
}
