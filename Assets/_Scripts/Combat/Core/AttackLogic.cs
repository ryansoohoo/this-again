using UnityEngine;

// Pure, deterministic attack state machine over the timed-frame phase lists. No scene/SO access.
public static class AttackLogic
{
    public static bool IsAttacking(AttackPhase p) => p != AttackPhase.Idle;
    public static bool InHitWindow(AttackPhase p) => p == AttackPhase.Hit;

    public static AttackState Step(AttackState s, AttackIntent intent, AttackTimeline tl, PhaseScales scales, float dt)
    {
        switch (s.phase)
        {
            case AttackPhase.Idle:
                if (s.cooldown > 0f) s.cooldown = Mathf.Max(0f, s.cooldown - dt);
                if (intent.pressed && s.cooldown <= 0f && Has(tl.anticipation))
                {
                    s.phase = AttackPhase.Anticipation;
                    s.frameIndex = 0; s.phaseElapsed = 0f; s.windupComplete = false;
                    Aim(ref s, tl, intent.aimDir);
                }
                return s;

            case AttackPhase.Anticipation:
                Aim(ref s, tl, intent.aimDir);                      // re-aimable while holding
                if (intent.feint)
                    return new AttackState { phase = AttackPhase.Idle, cooldown = tl.feintCooldown };
                if (intent.released || !intent.held)                // commit; direction stays locked from here
                {
                    bool tap = !s.windupComplete && Has(tl.tapAnticipation);
                    s.phase = tap ? AttackPhase.TapWindup : AttackPhase.Hit;
                    s.frameIndex = 0; s.phaseElapsed = 0f;
                    return s;
                }
                if (!s.windupComplete && Advance(ref s, tl.anticipation, scales.anticipation, dt))
                {
                    s.windupComplete = true;
                    s.frameIndex = tl.anticipation.Length - 1;       // hold the wound-up frame
                }
                return s;

            case AttackPhase.TapWindup:
                if (Advance(ref s, tl.tapAnticipation, scales.anticipation, dt))
                    Enter(ref s, AttackPhase.Hit);
                return s;

            case AttackPhase.Hit:
                if (Advance(ref s, tl.hit, scales.hit, dt))
                    Enter(ref s, AttackPhase.FollowThrough);
                return s;

            case AttackPhase.FollowThrough:
                if (Advance(ref s, tl.followThrough, scales.followThrough, dt))
                    Enter(ref s, AttackPhase.Idle);
                return s;
        }
        return s;
    }

    public static int CurrentColumn(AttackState s, AttackTimeline tl)
    {
        switch (s.phase)
        {
            case AttackPhase.Anticipation: return Col(tl.anticipation, s.frameIndex);
            case AttackPhase.TapWindup:    return Col(tl.tapAnticipation, s.frameIndex);
            case AttackPhase.Hit:          return Col(tl.hit, s.frameIndex);
            case AttackPhase.FollowThrough:return Col(tl.followThrough, s.frameIndex);
            default:                       return Has(tl.anticipation) ? tl.anticipation[0].column : 0;
        }
    }

    public static float TimeToHit(AttackTimeline tl, bool tapped, PhaseScales scales)
    {
        if (!tapped) return 0f;
        float sum = 0f;
        if (tl.tapAnticipation != null)
            for (int i = 0; i < tl.tapAnticipation.Length; i++) sum += tl.tapAnticipation[i].duration;
        return sum * scales.anticipation;
    }

    static void Enter(ref AttackState s, AttackPhase phase) { s.phase = phase; s.frameIndex = 0; s.phaseElapsed = 0f; }

    static void Aim(ref AttackState s, AttackTimeline tl, Vector2 aim)
    {
        var (idx, residual) = AttackDirections.Pick(tl.dirs, aim);
        s.dirIndex = idx; s.residualDeg = residual;
    }

    // Advance through a timed list by dt*scale; returns true once the cursor runs past the last frame.
    static bool Advance(ref AttackState s, TimedFrame[] list, float scale, float dt)
    {
        if (!Has(list)) return true;
        s.phaseElapsed += dt;
        while (s.frameIndex < list.Length && s.phaseElapsed >= list[s.frameIndex].duration * scale)
        {
            s.phaseElapsed -= list[s.frameIndex].duration * scale;
            s.frameIndex++;
        }
        return s.frameIndex >= list.Length;
    }

    static int Col(TimedFrame[] list, int i) => Has(list) ? list[Mathf.Clamp(i, 0, list.Length - 1)].column : 0;
    static bool Has(TimedFrame[] list) => list != null && list.Length > 0;
}
