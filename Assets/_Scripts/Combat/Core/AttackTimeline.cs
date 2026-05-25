using UnityEngine;

// One resolved on-hit effect application: which catalog effect (defId) and a per-weapon magnitude scale.
public struct EffectApply { public byte defId; public float scale; }

// Runtime, render-agnostic view of an attack's timing + directions. Built once from an AttackDefinition.
public sealed class AttackTimeline
{
    public TimedFrame[] anticipation;
    public TimedFrame[] tapAnticipation;
    public TimedFrame[] hit;
    public TimedFrame[] followThrough;
    public DirectionEntry[] directions;
    public Vector2[] dirs;        // cached directions[].canonicalDir for the picker (no per-tick alloc)
    public float feintCooldown;
    public int feintCooldownTicks;
    public float aimSnapDegrees;
    public AnimationCurve lungeCurve;

    // On-hit (consumed by AttackSimSystem.OnStrike's broadphase query). HitStun is implicit (always applied).
    public int damage;
    public float hitRange;
    public float hitArcCos;
    public EffectApply[] onHit;   // non-HitStun effects to apply to each victim
    public int hitstunTicks;
    public int hitstunTapTicks;
}
