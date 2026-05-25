using System;
using UnityEngine;

// One deterministic in-instance tick: age status effects → effective gate, step the attack, derive the lunge,
// then step movement (lunge overrides WASD). Pure (no Time/random/Netcode). Shared by client predict, client
// replay, and server sim — the single source of truth that keeps all three in lockstep.
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
    public StatusEffectDef[] defs;   // status catalog; the gate is derived from the player's StatusState, not passed in
}

public struct InstanceResult
{
    public bool feinted;        // a feint cancelled a windup this tick (caller emits the event; nothing client-side)
    public int periodicDamage;  // DOT accrued this tick (server applies to HP; predictor ignores)
    public Vector2 moveApplied; // the exact pre-collision move vector used (owner buffers this for reconcile replay)
}

public static class InstanceStep
{
    public static void Step(ref AttackState atk, StatusState status, ref Vector2 pos, in InstanceInput cmd, in InstanceCtx ctx, out InstanceResult result)
    {
        result = default;
        GateMod g = StatusLogic.Step(status, ctx.defs, out result.periodicDamage);  // age effects → effective gate
        g = GateMod.Quantize(g);                                                     // server == owner consume the wire value

        AttackPhase prev = atk.phase;
        atk = AttackLogic.Step(atk, cmd.attack, ctx.timeline, ctx.scales, ctx.dt, g.CanAttack);

        // Feint = a windup cancelled to Idle while attacking was allowed (a gated interrupt is not a feint).
        if (g.CanAttack && prev == AttackPhase.Anticipation && atk.phase == AttackPhase.Idle && cmd.attack.feint)
        {
            result.feinted = true;
            // tick 0: appliedTick on a self-inflicted cooldown is unused by reconcile (the owner keeps self-inflicted
            // effects by KIND, not tick). InstanceInput carries no tick field.
            StatusLogic.Apply(status, ctx.defs[(int)StatusKind.AttackCooldown], 0u, self: true,
                              durationOverride: ctx.timeline.feintCooldownTicks);
        }

        Vector2? lunge = AttackLogic.LungeVelocity(atk, ctx.timeline);  // null = idle, zero = rooted windup, vec = lunge
        Vector2 move = lunge ?? FreeMove(cmd.rawMove, g);               // lunge overrides WASD; gate scales/blocks WASD only
        if (StatusLogic.ActiveForcedMove(status, ctx.defs, out var fdir, out var fscale) && fdir.sqrMagnitude > 1e-6f)
            move = fdir * fscale;                                       // forced flee (Fear) overrides everything
        result.moveApplied = move;
        pos = MovementStep.Step(pos, move, ctx.dt, ctx.speed, ctx.walkable);
    }

    // The effective free-move vector after gating: zero when movement is blocked, else the (clamped) WASD direction
    // scaled by moveScale so the magnitude carries the slow. Baking the scale into the vector (rather than the speed
    // arg) keeps it identical between the live step and reconcile replay. When gate == None this matches what
    // MovementStep would have normalized internally, so the ungated path is unchanged.
    public static Vector2 FreeMove(Vector2 rawMove, in GateMod gate)
    {
        if (!gate.CanMove) return Vector2.zero;
        Vector2 dir = rawMove.sqrMagnitude > 1f ? rawMove.normalized : rawMove;
        return dir * gate.moveScale;
    }
}
