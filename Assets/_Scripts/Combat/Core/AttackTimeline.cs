using UnityEngine;

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
}
