using System.Collections.Generic;
using UnityEngine;

// Server authoritative in-instance step: drain contiguous InputCommands, run the shared InstanceStep (attack +
// lunge + movement) so position is server-derived, advance lastProcessedTick, and on attack phase transitions
// enqueue AttackEvents + call the OnStrike hit seam. Replaces PlayerSimSystem's in-instance branch. Server-only.
public static class AttackSimSystem
{
    static Game _gm;
    static PlayerRegistry _reg;   // set each StepInstanceFixed so OnStrike can sweep same-room players
    static readonly System.Func<Vector2, bool> _walkAt = p => { var c = _gm.WorldToCell(p); return _gm.IsWalkable(c.x, c.y); };

    const float MovedEps = 1e-6f;   // sq-distance below which a player is "pinned" (didn't move this tick)

    struct Pending { public ulong id; public ServerPlayer sp; public Vector2Int region; public float invMass; }
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

        // ---- Phase A: integrate each in-instance player (attack + lunge + movement vs walls), as before ----
        _pending.Clear();
        foreach (var kv in reg.Players)
        {
            var sp = kv.Value;
            if (!sp.inInstance) continue;
            Vector2 startPos = sp.worldPos;
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
                        EmitTransitions(kv.Key, sp, def, c.tick, res.feinted);
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
            // mover-yields weighting: moved this tick -> mover (1), otherwise pinned (0, holds ground)
            float invMass = (sp.worldPos - startPos).sqrMagnitude > MovedEps ? 1f : 0f;
            _pending.Add(new Pending { id = kv.Key, sp = sp, region = sp.regionKey, invMass = invMass });
        }

        // ---- Phase B: resolve player-vs-player overlaps per room (deterministic: sorted by region then id) ----
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
                    _bodies[k] = new CollisionBody { id = pp.id, pos = pp.sp.worldPos, radius = cfg.collisionRadius, invMass = pp.invMass };
                }
                CollisionStep.Resolve(_bodies, n, _walkAt, cfg.collisionIterations);
                for (int k = 0; k < n; k++) _pending[start + k].sp.worldPos = _bodies[k].pos;
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

    static void EmitTransitions(ulong id, ServerPlayer sp, AttackDefinition def, uint tick, bool feinted)
    {
        var prev = sp.prevAttackPhase; var now = sp.attackState.phase;
        if (prev == now && !feinted) return;   // only on a phase change (or a feint)
        sp.pendingEvents ??= new System.Collections.Generic.Queue<AttackEvent>();
        if (prev == AttackPhase.Idle && now == AttackPhase.Anticipation)
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Started, sp, tick));
        if (now == AttackPhase.Hit)
        {
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Struck, sp, tick));
            OnStrike(id, sp, def, tick);   // the forward hitbox seam
        }
        if (feinted)
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Feinted, sp, tick));
    }

    // Server-authoritative damage: clamp at 0, log once on reaching 0 (no death/respawn in v1).
    static void ApplyDamage(ServerPlayer sp, int dmg)
    {
        if (dmg <= 0 || sp.hp <= 0) return;
        sp.hp = Mathf.Max(0, sp.hp - dmg);
        if (sp.hp == 0) Debug.Log("[combat] player reached 0 HP (no death/respawn yet)");
    }

    static AttackEvent Evt(ulong id, byte kind, ServerPlayer sp, uint tick) => new AttackEvent
    {
        attackerId = id, kind = kind, weaponId = sp.weaponId, tick = tick, aimAngle = AimQuant.Encode(sp.attackState.lockedAim),
    };

    // Broadphase hit query (PLACEHOLDER for the deferred pixel narrowphase): same-region players within the
    // weapon's range + forward arc (from lockedAim) take damage + the weapon's on-hit effects. Server-only.
    static void OnStrike(ulong id, ServerPlayer sp, AttackDefinition def, uint tick)
    {
        if (_reg == null) return;
        var tl = def.Timeline;
        var statusCat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        var defs = statusCat != null ? statusCat.Defs : null;
        Vector2 origin = sp.worldPos;
        Vector2 aim = sp.attackState.lockedAim.sqrMagnitude > 1e-6f ? sp.attackState.lockedAim.normalized : Vector2.right;
        bool full = sp.attackState.windupComplete;   // full (charged) strike vs tap → scales the hitstun duration
        int hitstunTicks = full ? tl.hitstunTicks : tl.hitstunTapTicks;
        float range2 = tl.hitRange * tl.hitRange;
        foreach (var kv in _reg.Players)
        {
            if (kv.Key == id) continue;
            var victim = kv.Value;
            if (!victim.inInstance || victim.regionKey != sp.regionKey) continue;
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
            // (debug) the attacker's 'enchant' command override — an extra on-strike effect on top of the weapon's own.
            if (defs != null && sp.enchantDefId >= 0 && sp.enchantDefId < defs.Length)
                CombatEffects.ApplyEffect(victim.status, victim.worldPos, origin, defs[sp.enchantDefId], tick);
            Debug.Log($"[attack] HIT {id} -> {kv.Key} dmg={tl.damage} hp={victim.hp} hitstun={hitstunTicks}t ({(full ? "full" : "tap")})");
        }
    }
}
