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
    public GateMod? gate;   // null = ungated (treated as GateMod.None); a set value gates this step
}

public static class InstanceStep
{
    public static void Step(ref AttackState atk, ref Vector2 pos, in InstanceInput cmd, in InstanceCtx ctx)
    {
        GateMod g = ctx.gate ?? GateMod.None;
        atk = AttackLogic.Step(atk, cmd.attack, ctx.timeline, ctx.scales, ctx.dt, g.CanAttack);
        Vector2? lunge = AttackLogic.LungeVelocity(atk, ctx.timeline);  // null = idle, zero = rooted windup, vec = lunge
        Vector2 move = lunge ?? FreeMove(cmd.rawMove, g);               // lunge overrides WASD; gate scales/blocks WASD only
        pos = MovementStep.Step(pos, move, ctx.dt, ctx.speed, ctx.walkable);
    }

    // The effective free-move vector after gating: zero when movement is blocked, else the (clamped) WASD
    // direction scaled by moveScale so the magnitude carries the slow. Baking the scale into the vector (rather
    // than into the speed arg) keeps it identical between the live step and reconcile replay, which re-runs the
    // stored vector at the plain move speed. When gate == None this returns the same value MovementStep would
    // have normalized internally, so the ungated path is unchanged.
    public static Vector2 FreeMove(Vector2 rawMove, in GateMod gate)
    {
        if (!gate.CanMove) return Vector2.zero;
        Vector2 dir = rawMove.sqrMagnitude > 1f ? rawMove.normalized : rawMove;
        return dir * gate.moveScale;
    }
}
