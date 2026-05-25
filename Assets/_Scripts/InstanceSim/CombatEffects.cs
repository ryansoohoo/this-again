using UnityEngine;

// Source-agnostic effect application — the seam weapons (now) and spells/abilities (later) funnel through.
// Pure: operates on a StatusState + positions + def. For forced-move effects it freezes a flee direction away
// from the source, quantized through AimQuant so the server and the owning predictor derive the identical vector.
public static class CombatEffects
{
    public static void ApplyEffect(StatusState target, Vector2 targetPos, Vector2 sourcePos,
                                   in StatusEffectDef def, uint tick, float scale = 1f, int durationOverride = -1)
    {
        Vector2 forcedDir = default;
        if (def.forcedMove == ForcedMoveKind.FleeFrozen)
        {
            Vector2 away = targetPos - sourcePos;
            if (away.sqrMagnitude <= 1e-6f) away = Vector2.right;   // overlapping: deterministic fallback
            forcedDir = AimQuant.Decode(AimQuant.Encode(away));
        }
        StatusLogic.Apply(target, def, tick, self: false, scale: scale, durationOverride: durationOverride, forcedDir: forcedDir);
    }
}
