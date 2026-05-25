using System.Collections.Generic;
using UnityEngine;

// Server-authoritative in-instance step: drain contiguous InputCommands per player, run the shared InstanceStep
// (attack + lunge + movement) so position is server-derived, advance lastProcessedTick, and on attack phase
// transitions enqueue AttackEvents + call the OnStrike hit seam. Then resolve combatant-vs-combatant overlaps for
// every in-instance combatant per room. Server-only.
//
// The hit seam (OnStrike) and the collision pass operate on ALL combatants (PlayerRegistry.Combatants), so future
// AI is hittable + solid with no further wiring. Player INPUT integration (Phase A) is still player-only; AI's
// integration is the brain step and will feed the same Phase-B collision pass.
public static class AttackSimSystem
{
    static Game _gm;
    static PlayerRegistry _reg;   // set each StepInstanceFixed so OnStrike can sweep same-room combatants
    static readonly System.Func<Vector2, bool> _walkAt = p => { var c = _gm.WorldToCell(p); return _gm.IsWalkable(c.x, c.y); };

    const float MovedEps = 1e-6f;   // sq-distance below which a combatant is "pinned" (didn't move this tick)

    struct Pending { public ulong id; public Combatant c; public Vector2Int region; public Vector2 startPos; public float invMass; }
    static readonly List<Pending> _pending = new();
    static CollisionBody[] _bodies = new CollisionBody[8];
    static readonly System.Comparison<Pending> _byRegionThenId = (a, b) =>
    {
        int c = a.region.x.CompareTo(b.region.x); if (c != 0) return c;
        c = a.region.y.CompareTo(b.region.y);     if (c != 0) return c;
        return a.id.CompareTo(b.id);
    };

    public static void StepInstanceFixed(PlayerRegistry reg, WeaponCatalog catalog, MovementSettings cfg, float dt)
    {
        var gm = Game.Instance; if (gm == null) return;
        _gm = gm;
        _reg = reg;
        var statusCat = gm.StatusCatalog;
        var statusDefs = statusCat != null ? statusCat.Defs : System.Array.Empty<StatusEffectDef>();

        // ---- Pre-pass: snapshot start positions for every in-instance combatant (for mover-yields invMass) ----
        _pending.Clear();
        foreach (var c in reg.Combatants)
        {
            if (!c.inInstance) continue;
            _pending.Add(new Pending { id = c.entityId, c = c, region = c.regionKey, startPos = c.worldPos });
        }

        // ---- Phase A: integrate each in-instance PLAYER (attack + lunge + movement vs walls), as before. AI
        //      integration (the brain step) will run here too and mutate its own worldPos before Phase B. ----
        foreach (var kv in reg.Players)
        {
            var sp = kv.Value;
            if (!sp.inInstance) continue;
            if (sp.serverInputs != null)
            {
                var def = catalog != null ? catalog.Get(sp.weaponId) : null;
                while (sp.serverInputs.TryGet(sp.lastProcessedTick + 1, out var c))
                {
                    if (def != null)
                    {
                        var ctx = new InstanceCtx { timeline = def.Timeline, scales = sp.attackScales, dt = dt, speed = cfg.moveSpeed, walkable = _walkAt, defs = statusDefs };
                        sp.prevAttackPhase = sp.attackState.phase;
                        var atk = sp.attackState; var pos = sp.worldPos;
                        InstanceStep.Step(ref atk, sp.status, ref pos, new InstanceInput { rawMove = c.rawMove, attack = ToIntent(c) }, ctx, out var res);
                        sp.attackState = atk; sp.worldPos = pos;
                        ApplyDamage(sp, res.periodicDamage);
                        EmitTransitions(sp, def, c.tick, res.feinted);
                    }
                    else
                    {
                        // No weapon: still age status so slows/roots/DOT tick, and gate free-move.
                        var gate = GateMod.Quantize(StatusLogic.Step(sp.status, statusDefs, out int dmg));
                        ApplyDamage(sp, dmg);
                        sp.worldPos = MovementStep.Step(sp.worldPos, InstanceStep.FreeMove(c.rawMove, gate), dt, cfg.moveSpeed, _walkAt);
                    }
                    sp.lastInput = c.rawMove;
                    sp.lastProcessedTick++;
                }
            }
        }

        // ---- mover-yields weighting: moved this tick -> mover (1, absorbs the push), otherwise pinned (0) ----
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            p.invMass = (p.c.worldPos - p.startPos).sqrMagnitude > MovedEps ? 1f : 0f;
            _pending[i] = p;
        }

        // ---- Phase B: resolve combatant-vs-combatant overlaps per room (deterministic: sorted by region then id) ----
        if (_pending.Count < 2) return;
        _pending.Sort(_byRegionThenId);
        int start = 0;
        while (start < _pending.Count)
        {
            int end = start + 1;
            while (end < _pending.Count && _pending[end].region == _pending[start].region) end++;
            int n = end - start;
            if (n > 1)
            {
                if (_bodies.Length < n) _bodies = new CollisionBody[Mathf.NextPowerOfTwo(n)];
                for (int k = 0; k < n; k++)
                {
                    var pp = _pending[start + k];
                    _bodies[k] = new CollisionBody { id = pp.id, pos = pp.c.worldPos, radius = cfg.collisionRadius, invMass = pp.invMass };
                }
                CollisionStep.Resolve(_bodies, n, _walkAt, cfg.collisionIterations);
                for (int k = 0; k < n; k++) _pending[start + k].c.worldPos = _bodies[k].pos;
            }
            start = end;
        }
    }

    static AttackIntent ToIntent(InputCommand c) => new AttackIntent
    {
        pressed = (c.attackBits & InputCommand.Pressed) != 0,
        held = (c.attackBits & InputCommand.Held) != 0,
        released = (c.attackBits & InputCommand.Released) != 0,
        feint = (c.attackBits & InputCommand.Feint) != 0,
        aimDir = AimQuant.Decode(c.aimAngle),
    };

    static void EmitTransitions(Combatant attacker, AttackDefinition def, uint tick, bool feinted)
    {
        var prev = attacker.prevAttackPhase; var now = attacker.attackState.phase;
        if (prev == now && !feinted) return;   // only on a phase change (or a feint)
        attacker.pendingEvents ??= new System.Collections.Generic.Queue<AttackEvent>();
        if (prev == AttackPhase.Idle && now == AttackPhase.Anticipation)
            attacker.pendingEvents.Enqueue(Evt(attacker, AttackEvent.Started, tick));
        if (now == AttackPhase.Hit)
        {
            attacker.pendingEvents.Enqueue(Evt(attacker, AttackEvent.Struck, tick));
            OnStrike(attacker, def, tick);   // the forward hitbox seam
        }
        if (feinted)
            attacker.pendingEvents.Enqueue(Evt(attacker, AttackEvent.Feinted, tick));
    }

    // Server-authoritative damage: clamp at 0, log once on reaching 0 (no death/respawn in v1).
    static void ApplyDamage(Combatant c, int dmg)
    {
        if (dmg <= 0 || c.hp <= 0) return;
        c.hp = Mathf.Max(0, c.hp - dmg);
        if (c.hp == 0) Debug.Log($"[combat] combatant {c.entityId} reached 0 HP (no death/respawn yet)");
    }

    static AttackEvent Evt(Combatant attacker, byte kind, uint tick) => new AttackEvent
    {
        attackerId = attacker.entityId, kind = kind, weaponId = attacker.weaponId, tick = tick,
        aimAngle = AimQuant.Encode(attacker.attackState.lockedAim),
    };

    // Broadphase hit query (PLACEHOLDER for the deferred pixel narrowphase): same-region combatants within the
    // weapon's range + forward arc (from lockedAim), gated by faction (CombatRules.CanHit), take damage + the
    // weapon's on-hit effects. The inInstance/region/self checks ARE the underworld-only gate. Server-only.
    static void OnStrike(Combatant attacker, AttackDefinition def, uint tick)
    {
        if (_reg == null) return;
        var tl = def.Timeline;
        var statusCat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        var defs = statusCat != null ? statusCat.Defs : null;
        Vector2 origin = attacker.worldPos;
        Vector2 aim = attacker.attackState.lockedAim.sqrMagnitude > 1e-6f ? attacker.attackState.lockedAim.normalized : Vector2.right;
        bool full = attacker.attackState.windupComplete;   // full (charged) strike vs tap → scales the hitstun duration
        int hitstunTicks = full ? tl.hitstunTicks : tl.hitstunTapTicks;
        float range2 = tl.hitRange * tl.hitRange;
        foreach (var victim in _reg.Combatants)
        {
            if (victim == attacker) continue;
            if (!victim.inInstance || victim.regionKey != attacker.regionKey) continue;   // underworld-only + same room
            if (!CombatRules.CanHit(attacker.faction, victim.faction)) continue;          // friend/foe gate
            Vector2 to = victim.worldPos - origin;
            if (to.sqrMagnitude > range2 || to.sqrMagnitude < 1e-6f) continue;
            if (Vector2.Dot(to.normalized, aim) < tl.hitArcCos) continue;     // outside the forward arc
            ApplyDamage(victim, tl.damage);
            // Always apply charge-scaled HitStun (the base hurt).
            if (defs != null && (int)StatusKind.HitStun < defs.Length)
                StatusLogic.Apply(victim.status, defs[(int)StatusKind.HitStun], tick, self: false, durationOverride: hitstunTicks);
            // The weapon's on-hit effects, through the source-agnostic seam (attacker is the source).
            if (defs != null && tl.onHit != null)
                foreach (var oh in tl.onHit)
                {
                    if (oh.defId >= defs.Length) continue;
                    CombatEffects.ApplyEffect(victim.status, victim.worldPos, origin, defs[oh.defId], tick, scale: oh.scale);
                }
            Debug.Log($"[attack] HIT {attacker.entityId} -> {victim.entityId} dmg={tl.damage} hp={victim.hp} hitstun={hitstunTicks}t ({(full ? "full" : "tap")})");
        }
    }
}
