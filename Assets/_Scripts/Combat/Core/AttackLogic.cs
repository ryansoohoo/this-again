using UnityEngine;

// Pure, deterministic attack state machine over the timed-frame phase lists. No scene/SO access.
public static class AttackLogic
{
    public static bool IsAttacking(AttackPhase p) => p != AttackPhase.Idle;
    public static bool InHitWindow(AttackPhase p) => p == AttackPhase.Hit;

    public static AttackState Step(AttackState s, AttackIntent intent, AttackTimeline tl, PhaseScales scales, float dt, bool canAttack = true)
    {
        if (!canAttack)
        {
            // Gated: interrupt any in-progress attack back to Idle, and never start a new one.
            if (s.phase != AttackPhase.Idle) return new AttackState { phase = AttackPhase.Idle };
            return s;
        }

        switch (s.phase)
        {
            case AttackPhase.Idle:
                // Attack-start is blocked by the gate (AttackCooldown sets blocksAttack) — no cooldown field here.
                if (intent.pressed && Has(tl.anticipation))
                {
                    s.phase = AttackPhase.Anticipation;
                    s.frameIndex = 0; s.phaseElapsed = 0f; s.windupComplete = false;
                    Aim(ref s, tl, intent.aimDir);
                }
                return s;

            case AttackPhase.Anticipation:
                Aim(ref s, tl, intent.aimDir);                      // re-aimable while holding
                if (intent.feint)
                    return new AttackState { phase = AttackPhase.Idle };   // InstanceStep applies the AttackCooldown effect
                if (intent.released || !intent.held)                // commit; direction stays locked from here
                {
                    bool tap = !s.windupComplete && Has(tl.tapAnticipation);
                    s.phase = tap ? AttackPhase.TapWindup : AttackPhase.Hit;
                    s.frameIndex = 0; s.phaseElapsed = 0f;
                    s.lockedAim = AimVector(s, tl);                  // freeze the aim vector for the lunge
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

    // Total seconds of the hit + follow-through phases (base durations) — the lunge window.
    public static float LungeDuration(AttackTimeline tl) => SumDur(tl.hit) + SumDur(tl.followThrough);

    // Normalized 0..1 progress through the lunge window (hit then follow-through); 0 outside those phases.
    public static float LungeProgress(AttackState s, AttackTimeline tl)
    {
        float dur = LungeDuration(tl);
        if (dur <= 0f) return 1f;
        float elapsed;
        if (s.phase == AttackPhase.Hit) elapsed = SumBefore(tl.hit, s.frameIndex) + s.phaseElapsed;
        else if (s.phase == AttackPhase.FollowThrough) elapsed = SumDur(tl.hit) + SumBefore(tl.followThrough, s.frameIndex) + s.phaseElapsed;
        else return 0f;
        return Mathf.Clamp01(elapsed / dur);
    }

    // The movement override an attack imposes: null outside an attack (normal WASD), zero during the wind-up
    // (rooted), and the lunge vector during hit/follow-through (lockedAim * curve(progress)). Pure.
    public static Vector2? LungeVelocity(AttackState s, AttackTimeline tl)
    {
        switch (s.phase)
        {
            case AttackPhase.Hit:
            case AttackPhase.FollowThrough:
                float speed = tl.lungeCurve != null ? Mathf.Clamp01(tl.lungeCurve.Evaluate(LungeProgress(s, tl))) : 0f;
                return s.lockedAim * speed;
            case AttackPhase.Anticipation:
            case AttackPhase.TapWindup:
                return Vector2.zero;
            default:
                return null;
        }
    }

    static void Enter(ref AttackState s, AttackPhase phase) { s.phase = phase; s.frameIndex = 0; s.phaseElapsed = 0f; }

    static void Aim(ref AttackState s, AttackTimeline tl, Vector2 aim)
    {
        var (idx, residual) = AttackDirections.Pick(tl.dirs, aim);
        if (tl.aimSnapDegrees > 0f) residual = Mathf.Round(residual / tl.aimSnapDegrees) * tl.aimSnapDegrees;
        s.dirIndex = idx; s.residualDeg = residual;
    }

    // The committed aim direction: the chosen canonical direction rotated by the residual, so it points exactly
    // where the cursor was at release (including any snapping already baked into residualDeg).
    static Vector2 AimVector(AttackState s, AttackTimeline tl)
    {
        if (tl.dirs == null || tl.dirs.Length == 0) return Vector2.zero;
        int i = Mathf.Clamp(s.dirIndex, 0, tl.dirs.Length - 1);
        return Rotate(tl.dirs[i].normalized, s.residualDeg);
    }

    static Vector2 Rotate(Vector2 v, float deg)
    {
        float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), sn = Mathf.Sin(r);
        return new Vector2(v.x * c - v.y * sn, v.x * sn + v.y * c);
    }

    static float SumDur(TimedFrame[] list) { float t = 0f; if (list != null) for (int i = 0; i < list.Length; i++) t += list[i].duration; return t; }
    static float SumBefore(TimedFrame[] list, int idx) { float t = 0f; if (list != null) for (int i = 0; i < idx && i < list.Length; i++) t += list[i].duration; return t; }

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

    // Coarse 0..1 progress across the whole swing for FX overlays: anticipation = 0..0.5, hit = 0.5..0.75,
    // follow-through = 0.75..1 (frame-fraction within each phase). Idle = 0.
    public static float PhaseProgress01(in AttackState s, AttackTimeline tl)
    {
        switch (s.phase)
        {
            case AttackPhase.Anticipation:
            case AttackPhase.TapWindup: return 0.5f * FrameFrac(s, tl.anticipation);
            case AttackPhase.Hit:          return 0.5f + 0.25f * FrameFrac(s, tl.hit);
            case AttackPhase.FollowThrough:return 0.75f + 0.25f * FrameFrac(s, tl.followThrough);
            default: return 0f;
        }
    }

    static float FrameFrac(in AttackState s, TimedFrame[] phase)
    {
        if (phase == null || phase.Length == 0) return 0f;
        return Mathf.Clamp01((s.frameIndex + 0.5f) / phase.Length);
    }
}
