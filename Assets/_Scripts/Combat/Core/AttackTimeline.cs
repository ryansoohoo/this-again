using UnityEngine;

// One effect a strike applies to each victim. magnitude is kind-specific (stacks for Poison, etc.).
[System.Serializable]
public struct OnHitEffect { public StatusKind kind; public int magnitude; }

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
    public int feintCooldownTicks;  // feintCooldown converted to ticks (60 Hz); the AttackCooldown effect's duration
    public float aimSnapDegrees;  // 0 = rotate freely to the cursor; 45 = snap the aim to 8 directions
    public AnimationCurve lungeCurve;  // copied from the SO; lets the pure core compute the lunge without an SO ref

    // On-hit (consumed by AttackSimSystem.OnStrike's broadphase query).
    public int damage;            // HP removed per strike
    public float hitRange;        // broadphase radius from the attacker
    public float hitArcCos;       // cos(halfArc); a victim must be within this of lockedAim
    public OnHitEffect[] onHit;   // effects applied to each victim on a strike (e.g., HitStun, Poison)
    public int hitstunTicks;      // HitStun duration for a FULL strike (windup completed)
    public int hitstunTapTicks;   // HitStun duration for a TAP strike (released before windup completed) — shorter

    public Color glowColor;       // weapon-shine color (per attack); per-frame intensity is TimedFrame.glow
}
