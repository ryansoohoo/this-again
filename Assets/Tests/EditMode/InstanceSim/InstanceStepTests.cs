using NUnit.Framework;
using UnityEngine;

public class InstanceStepTests
{
    static readonly StatusEffectDef[] NoDefs = System.Array.Empty<StatusEffectDef>();

    static InstanceCtx Ctx(AttackTimeline tl) => new InstanceCtx
    {
        timeline = tl, scales = PhaseScales.One, dt = 0.02f, speed = 4f,
        walkable = _ => true, defs = NoDefs,
    };

    static AttackTimeline Tl()
    {
        return new AttackTimeline
        {
            anticipation    = new[] { new TimedFrame { column = 0, duration = 0.1f } },
            tapAnticipation = new[] { new TimedFrame { column = 0, duration = 0.1f } },
            hit             = new[] { new TimedFrame { column = 1, duration = 0.1f } },
            followThrough   = new[] { new TimedFrame { column = 2, duration = 0.1f } },
            directions      = new[] { new DirectionEntry { canonicalDir = new Vector2(1, 0), row = 0 } },
            dirs            = new[] { new Vector2(1, 0) },
            lungeCurve      = AnimationCurve.Constant(0, 1, 1f),
        };
    }

    static InstanceInput Move(Vector2 m) => new InstanceInput { rawMove = m, attack = new AttackIntent { aimDir = new Vector2(1, 0) } };

    [Test]
    public void Deterministic_SameInputs_SameOutput()
    {
        var tl = Tl();
        var a1 = default(AttackState); Vector2 p1 = Vector2.zero; var s1 = new StatusState();
        var a2 = default(AttackState); Vector2 p2 = Vector2.zero; var s2 = new StatusState();
        for (int i = 0; i < 50; i++)
        {
            InstanceStep.Step(ref a1, s1, ref p1, Move(new Vector2(1, 0)), Ctx(tl), out _);
            InstanceStep.Step(ref a2, s2, ref p2, Move(new Vector2(1, 0)), Ctx(tl), out _);
        }
        Assert.AreEqual(p1, p2);
        Assert.AreEqual(a1.phase, a2.phase);
    }

    [Test]
    public void NoAttack_MovesByRawWASD()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero; var st = new StatusState();
        InstanceStep.Step(ref atk, st, ref pos, Move(new Vector2(1, 0)), Ctx(tl), out _);
        Assert.Greater(pos.x, 0f);   // walked east
    }

    [Test]
    public void Windup_RootsMovement()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero; var st = new StatusState();
        var press = new InstanceInput { rawMove = new Vector2(1, 0), attack = new AttackIntent { pressed = true, held = true, aimDir = new Vector2(1, 0) } };
        InstanceStep.Step(ref atk, st, ref pos, press, Ctx(tl), out _); // -> Anticipation (rooted)
        Assert.AreEqual(AttackPhase.Anticipation, atk.phase);
        Assert.AreEqual(0f, pos.x, 1e-4f);   // rooted despite rawMove east
    }

    [Test]
    public void Hit_LungesAlongLockedAim()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero; var st = new StatusState();
        var press = new InstanceInput { rawMove = Vector2.zero, attack = new AttackIntent { pressed = true, held = true, aimDir = new Vector2(1, 0) } };
        InstanceStep.Step(ref atk, st, ref pos, press, Ctx(tl), out _);
        var release = new InstanceInput { rawMove = Vector2.zero, attack = new AttackIntent { released = true, aimDir = new Vector2(1, 0) } };
        InstanceStep.Step(ref atk, st, ref pos, release, Ctx(tl), out _); // commit -> TapWindup
        for (int i = 0; i < 10 && atk.phase != AttackPhase.Hit; i++)
            InstanceStep.Step(ref atk, st, ref pos, new InstanceInput { attack = new AttackIntent { aimDir = new Vector2(1, 0) } }, Ctx(tl), out _);
        Assert.AreEqual(AttackPhase.Hit, atk.phase);
        float before = pos.x;
        InstanceStep.Step(ref atk, st, ref pos, new InstanceInput { rawMove = new Vector2(0, 1), attack = new AttackIntent { aimDir = new Vector2(1, 0) } }, Ctx(tl), out _);
        Assert.Greater(pos.x, before);   // lunged east (locked aim), not north (rawMove)
        Assert.AreEqual(0f, pos.y, 1e-4f);
    }

    [Test]
    public void Feared_FleesFrozenDir_OverridesWasd_AndReportsMoveApplied()
    {
        var tl = Tl();
        var defs = new StatusEffectDef[8];
        defs[(int)StatusKind.Fear] = new StatusEffectDef
        {
            id = (byte)StatusKind.Fear, durationTicks = 60, blocksMove = true, blocksAttack = true,
            moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1,
            forcedMove = ForcedMoveKind.FleeFrozen, forcedMoveScale = 0.5f,
        };
        var ctx = new InstanceCtx { timeline = tl, scales = PhaseScales.One, dt = 0.02f, speed = 4f, walkable = _ => true, defs = defs };
        var st = new StatusState();
        StatusLogic.Apply(st, defs[(int)StatusKind.Fear], tick: 1, self: false, forcedDir: new Vector2(1, 0));
        var atk = default(AttackState); Vector2 pos = Vector2.zero;
        InstanceStep.Step(ref atk, st, ref pos, new InstanceInput { rawMove = new Vector2(0, 1), attack = new AttackIntent { aimDir = new Vector2(1, 0) } }, ctx, out var res);
        Assert.Greater(pos.x, 0f);                 // fled east (frozen dir), not north (rawMove)
        Assert.AreEqual(0f, pos.y, 1e-4f);
        Assert.Greater(res.moveApplied.x, 0f);     // reported the applied flee vector
        Assert.AreEqual(0f, res.moveApplied.y, 1e-4f);
    }
}
