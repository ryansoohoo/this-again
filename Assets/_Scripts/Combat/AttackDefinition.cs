using UnityEngine;

// Authoring asset for one attack. Holds the three sliced sheets + columnsPerRow, the four timed-frame phase
// lists, the feint cooldown, and the per-direction vectors. Builds/caches a render-agnostic AttackTimeline.
[CreateAssetMenu(menuName = "Minifantasy/Attack Definition", fileName = "Attack")]
public sealed class AttackDefinition : ScriptableObject
{
    [Header("Sliced sheets (row-major)")]
    public Sprite[] bodyFrames;
    public Sprite[] weaponFrontFrames;
    public Sprite[] weaponBackFrames;
    public int columnsPerRow = 4;

    [Header("Phases - (column, duration seconds)")]
    public TimedFrame[] anticipation;
    public TimedFrame[] tapAnticipation;
    public TimedFrame[] hit;
    public TimedFrame[] followThrough;

    [Header("Rules")]
    public float feintCooldown = 0.5f;
    public bool rotateToAim = true;   // tilt the rig the residual degrees to aim exactly at the cursor; off = snap to nearest direction
    public float aimSnapDegrees = 0f; // 0 = rotate freely to the cursor; 45 = snap the aim to 8 directions (used with rotateToAim)
    // Forward lunge speed (fraction of moveSpeed, Y) over normalized hit+follow-through time (X 0..1).
    // Default: ease-out impulse (full at the strike, decaying to 0). Flat 0 = no lunge.
    public AnimationCurve lungeCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
    public DirectionEntry[] directions;
    public string attackId;

    [Header("On hit")]
    public int damage = 10;
    public float hitRange = 1.0f;
    [Range(0f, 180f)] public float hitArcDegrees = 90f;   // half-arc each side of the locked aim
    public OnHitEffect[] onHit = new[] { new OnHitEffect { kind = StatusKind.HitStun, magnitude = 1 } };

    AttackTimeline _timeline;
    public AttackTimeline Timeline => _timeline ??= BuildTimeline();

    public AttackTimeline BuildTimeline()
    {
        int n = directions != null ? directions.Length : 0;
        var dirs = new Vector2[n];
        for (int i = 0; i < n; i++) dirs[i] = directions[i].canonicalDir;
        return new AttackTimeline
        {
            anticipation = anticipation,
            tapAnticipation = tapAnticipation,
            hit = hit,
            followThrough = followThrough,
            directions = directions,
            dirs = dirs,
            feintCooldown = feintCooldown,
            feintCooldownTicks = Mathf.CeilToInt(feintCooldown * 60f),
            aimSnapDegrees = aimSnapDegrees,
            lungeCurve = lungeCurve,
            damage = damage,
            hitRange = hitRange,
            hitArcCos = Mathf.Cos(hitArcDegrees * Mathf.Deg2Rad),
            onHit = onHit,
        };
    }

    void OnValidate() => _timeline = null; // rebuild after edits in the Inspector
}
