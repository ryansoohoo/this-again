using System.Collections.Generic;
using UnityEngine;

// Server authoritative in-instance step: drain contiguous InputCommands, run the shared InstanceStep (attack +
// lunge + movement) so position is server-derived, advance lastProcessedTick, and on attack phase transitions
// enqueue AttackEvents + call the OnStrike hit seam. Replaces PlayerSimSystem's in-instance branch. Server-only.
public static class AttackSimSystem
{
    static Game _gm;
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

        // ---- Phase A: integrate each in-instance player (attack + lunge + movement vs walls), as before ----
        _pending.Clear();
        foreach (var kv in reg.Players)
        {
            var sp = kv.Value;
            if (!sp.inInstance) continue;
            // Quantize the effective gate so the server sims with the exact value it ships to the owner.
            var gate = GateMod.Quantize(sp.gate.Effective);
            Vector2 startPos = sp.worldPos;
            if (sp.serverInputs != null)
            {
                var def = catalog != null ? catalog.Get(sp.weaponId) : null;
                while (sp.serverInputs.TryGet(sp.lastProcessedTick + 1, out var c))
                {
                    if (def != null)
                    {
                        var ctx = new InstanceCtx { timeline = def.Timeline, scales = sp.attackScales, dt = dt, speed = cfg.moveSpeed, walkable = _walkAt, gate = gate };
                        sp.prevAttackPhase = sp.attackState.phase;
                        var atk = sp.attackState; var pos = sp.worldPos;
                        InstanceStep.Step(ref atk, ref pos, new InstanceInput { rawMove = c.rawMove, attack = ToIntent(c) }, ctx);
                        sp.attackState = atk; sp.worldPos = pos;
                        EmitTransitions(kv.Key, sp, def, c.tick);
                    }
                    else
                    {
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

    static void EmitTransitions(ulong id, ServerPlayer sp, AttackDefinition def, uint tick)
    {
        var prev = sp.prevAttackPhase; var now = sp.attackState.phase;
        if (prev == now) return;   // only on a phase change
        sp.pendingEvents ??= new System.Collections.Generic.Queue<AttackEvent>();
        if (prev == AttackPhase.Idle && now == AttackPhase.Anticipation)
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Started, sp, tick));
        if (now == AttackPhase.Hit)
        {
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Struck, sp, tick));
            OnStrike(id, sp, def, tick);   // the forward hitbox seam
        }
        if (prev == AttackPhase.Anticipation && now == AttackPhase.Idle && sp.attackState.cooldown > 0f)
            sp.pendingEvents.Enqueue(Evt(id, AttackEvent.Feinted, sp, tick));
    }

    static AttackEvent Evt(ulong id, byte kind, ServerPlayer sp, uint tick) => new AttackEvent
    {
        attackerId = id, kind = kind, weaponId = sp.weaponId, tick = tick, aimAngle = AimQuant.Encode(sp.attackState.lockedAim),
    };

    // Hit seam. v1: log. Later: sweep same-regionKey players within a weapon-derived volume (the server holds
    // every position) and route to a damage spec — no wire/client change needed to add it.
    static void OnStrike(ulong id, ServerPlayer sp, AttackDefinition def, uint tick)
    {
        Debug.Log($"[attack] STRIKE id={id} weapon={def.attackId} tick={tick} pos={sp.worldPos}");
    }
}
