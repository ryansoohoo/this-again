using UnityEngine;

// Pure, deterministic status-effect engine. Reduces a StatusState to the GateMod the sim consumes, applies new
// effects per stacking policy, and ages effects one fixed tick at a time (one Step() call == one tick; integer
// counts, no dt). Shared by server sim, owner predict, and (forward) replay. No Time/random/Netcode/scene.
public static class StatusLogic
{
    // OR the block flags, multiply the moveScales — order-independent, so overlapping effects compose and
    // clearing one never re-enables what another still blocks (the same math AbilityGate used).
    public static GateMod Reduce(StatusState s, StatusEffectDef[] defs)
    {
        bool blockMove = false, blockAttack = false; float scale = 1f;
        for (int i = 0; i < s.count; i++)
        {
            var d = defs[s.effects[i].defId];
            blockMove |= d.blocksMove;
            blockAttack |= d.blocksAttack;
            scale *= Mathf.Clamp01(d.moveScale);
        }
        return new GateMod { blocksMove = blockMove, blocksAttack = blockAttack, moveScale = Mathf.Clamp01(scale) };
    }

    // Age every effect one tick: gate is read BEFORE decrement (an effect is active the tick it hits zero),
    // periodic damage accrues into periodicDamage (× stacks), expired effects are swap-removed. Returns the gate.
    public static GateMod Step(StatusState s, StatusEffectDef[] defs, out int periodicDamage)
    {
        GateMod g = Reduce(s, defs);
        periodicDamage = 0;
        for (int i = 0; i < s.count; )
        {
            ref var e = ref s.effects[i];
            var d = defs[e.defId];
            if (d.periodTicks > 0)
            {
                e.sincePeriodTick++;
                while (e.sincePeriodTick >= d.periodTicks)
                {
                    e.sincePeriodTick -= d.periodTicks;
                    periodicDamage += d.amountPerTick * e.stacks;
                }
            }
            if (d.durationTicks > 0 || e.remainingTicks > 0)   // 0-duration AttackCooldown uses an override > 0
            {
                e.remainingTicks--;
                if (e.remainingTicks <= 0) { RemoveAt(s, i); continue; }
            }
            i++;
        }
        return g;
    }

    // Add or combine per stacking policy. durationOverride >= 0 wins (AttackCooldown passes the weapon's value).
    // forcedDir is the frozen flee direction stored on the instance (forced-move effects only).
    public static void Apply(StatusState s, in StatusEffectDef d, uint tick, bool self, float scale = 1f, int durationOverride = -1, Vector2 forcedDir = default)
    {
        int dur = durationOverride >= 0 ? durationOverride : Mathf.CeilToInt(d.durationTicks * scale);
        if (d.policy != StackPolicy.Independent)
        {
            for (int i = 0; i < s.count; i++)
            {
                if (s.effects[i].defId != d.id) continue;
                if (d.policy == StackPolicy.Stack)
                    s.effects[i].stacks = (byte)Mathf.Min(d.maxStacks, s.effects[i].stacks + 1);
                s.effects[i].remainingTicks = dur;
                s.effects[i].appliedTick = tick;
                s.effects[i].fleeDir = forcedDir;
                return;
            }
        }
        if (s.count >= StatusState.Cap) { if (!ReplaceWeakest(s, dur)) return; }
        s.effects[s.count++] = new ActiveEffect
        {
            defId = d.id, remainingTicks = dur, stacks = 1, sincePeriodTick = 0, appliedTick = tick, selfInflicted = self, fleeDir = forcedDir,
        };
    }

    // The highest-priority active forced-move effect's frozen direction + speed scale (first match wins; v1 has one).
    public static bool ActiveForcedMove(StatusState s, StatusEffectDef[] defs, out Vector2 dir, out float scale)
    {
        for (int i = 0; i < s.count; i++)
        {
            var d = defs[s.effects[i].defId];
            if (d.forcedMove != ForcedMoveKind.None)
            {
                dir = s.effects[i].fleeDir;
                scale = d.forcedMoveScale;
                return true;
            }
        }
        dir = default; scale = 0f; return false;
    }

    public static bool Remove(StatusState s, byte defId)
    {
        for (int i = 0; i < s.count; i++) if (s.effects[i].defId == defId) { RemoveAt(s, i); return true; }
        return false;
    }

    // One bit per active effect kind (defId), for the cosmetic remote wire.
    public static byte ActiveMask(StatusState s)
    {
        byte m = 0;
        for (int i = 0; i < s.count; i++) m |= (byte)(1 << s.effects[i].defId);
        return m;
    }

    static void RemoveAt(StatusState s, int i) { s.effects[i] = s.effects[--s.count]; }   // swap-remove

    // Over cap: replace the instance with the fewest remaining ticks if the newcomer would outlast it.
    static bool ReplaceWeakest(StatusState s, int newDur)
    {
        int weakest = 0;
        for (int i = 1; i < s.count; i++) if (s.effects[i].remainingTicks < s.effects[weakest].remainingTicks) weakest = i;
        if (s.effects[weakest].remainingTicks >= newDur) return false;
        RemoveAt(s, weakest);
        return true;
    }
}
