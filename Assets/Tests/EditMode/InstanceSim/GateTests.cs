using NUnit.Framework;
using UnityEngine;

// GateMod wire round-trip, AttackLogic start-block + interrupt, and InstanceStep movement gating driven by a
// StatusState (effects reduce to the gate). AbilityGate reduction is covered by StatusLogicTests now.
public class GateTests
{
    // ---- GateMod value + wire ----

    [Test]
    public void None_IsFullyEnabled()
    {
        var n = GateMod.None;
        Assert.IsTrue(n.CanMove);
        Assert.IsTrue(n.CanAttack);
        Assert.AreEqual(1f, n.moveScale, 1e-4f);
    }

    [Test]
    public void PackUnpack_RoundTrips()
    {
        var g = GateMod.Unpack(GateMod.Pack(new GateMod { blocksMove = true, blocksAttack = false, moveScale = 0.5f }));
        Assert.IsFalse(g.CanMove);
        Assert.IsTrue(g.CanAttack);
        Assert.AreEqual(0.5f, g.moveScale, 0.02f);   // 6-bit quantization tolerance

        var full = GateMod.Unpack(GateMod.Pack(GateMod.None));
        Assert.IsTrue(full.CanMove);
        Assert.IsTrue(full.CanAttack);
        Assert.AreEqual(1f, full.moveScale, 1e-4f);
    }

    // ---- AttackLogic gating: block new starts, interrupt in-progress ----

    static AttackTimeline Tl() => new AttackTimeline
    {
        anticipation    = new[] { new TimedFrame { column = 0, duration = 0.1f } },
        tapAnticipation = new[] { new TimedFrame { column = 0, duration = 0.1f } },
        hit             = new[] { new TimedFrame { column = 1, duration = 0.1f } },
        followThrough   = new[] { new TimedFrame { column = 2, duration = 0.1f } },
        directions      = new[] { new DirectionEntry { canonicalDir = new Vector2(1, 0), row = 0 } },
        dirs            = new[] { new Vector2(1, 0) },
        lungeCurve      = AnimationCurve.Constant(0, 1, 1f),
    };

    static AttackIntent Press() => new AttackIntent { pressed = true, held = true, aimDir = new Vector2(1, 0) };

    [Test]
    public void GatedAttack_BlocksStart()
    {
        var s = AttackLogic.Step(default, Press(), Tl(), PhaseScales.One, 0.016f, canAttack: false);
        Assert.AreEqual(AttackPhase.Idle, s.phase);
    }

    [Test]
    public void GatedAttack_InterruptsInProgress()
    {
        var tl = Tl();
        var s = AttackLogic.Step(default, Press(), tl, PhaseScales.One, 0f);   // -> Anticipation
        Assert.AreEqual(AttackPhase.Anticipation, s.phase);
        s = AttackLogic.Step(s, new AttackIntent { held = true, aimDir = new Vector2(1, 0) }, tl, PhaseScales.One, 0.016f, canAttack: false);
        Assert.AreEqual(AttackPhase.Idle, s.phase);
    }

    // ---- InstanceStep gating: the gate is reduced from the player's StatusState ----

    // Minimal catalog: [0] root+silence, [1] slow 0.5, [2] paralyze (root+silence+0 scale).
    static StatusEffectDef[] Defs() => new[]
    {
        new StatusEffectDef { id = 0, blocksMove = true, blocksAttack = true, moveScale = 0f, durationTicks = 100, policy = StackPolicy.Refresh, maxStacks = 1 },
        new StatusEffectDef { id = 1, moveScale = 0.5f, durationTicks = 100, policy = StackPolicy.Refresh, maxStacks = 1 },
        new StatusEffectDef { id = 2, blocksMove = true, blocksAttack = true, moveScale = 0f, durationTicks = 100, policy = StackPolicy.Refresh, maxStacks = 1 },
    };

    static InstanceCtx Ctx(AttackTimeline tl) => new InstanceCtx
    {
        timeline = tl, scales = PhaseScales.One, dt = 0.02f, speed = 4f, walkable = _ => true, defs = Defs(),
    };

    static InstanceInput Move(Vector2 m) => new InstanceInput { rawMove = m, attack = new AttackIntent { aimDir = new Vector2(1, 0) } };

    [Test]
    public void BlockedMove_DoesNotWalk()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero; var st = new StatusState();
        StatusLogic.Apply(st, Defs()[0], 0u, false);   // root
        InstanceStep.Step(ref atk, st, ref pos, Move(new Vector2(1, 0)), Ctx(tl), out _);
        Assert.AreEqual(0f, pos.x, 1e-4f);
    }

    [Test]
    public void MoveScale_HalvesDistance()
    {
        var tl = Tl();
        var fa = default(AttackState); Vector2 full = Vector2.zero;
        InstanceStep.Step(ref fa, new StatusState(), ref full, Move(new Vector2(1, 0)), Ctx(tl), out _);
        var ha = default(AttackState); Vector2 half = Vector2.zero; var st = new StatusState();
        StatusLogic.Apply(st, Defs()[1], 0u, false);   // slow 0.5
        InstanceStep.Step(ref ha, st, ref half, Move(new Vector2(1, 0)), Ctx(tl), out _);
        Assert.AreEqual(full.x * 0.5f, half.x, 1e-3f);
    }

    [Test]
    public void GatedAttack_InterruptsLunge_AndParalyzeRoots()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero; var none = new StatusState();
        InstanceStep.Step(ref atk, none, ref pos, new InstanceInput { attack = new AttackIntent { pressed = true, held = true, aimDir = new Vector2(1, 0) } }, Ctx(tl), out _);
        InstanceStep.Step(ref atk, none, ref pos, new InstanceInput { attack = new AttackIntent { released = true, aimDir = new Vector2(1, 0) } }, Ctx(tl), out _);
        for (int i = 0; i < 10 && atk.phase != AttackPhase.Hit; i++)
            InstanceStep.Step(ref atk, none, ref pos, Move(Vector2.zero), Ctx(tl), out _);
        Assert.AreEqual(AttackPhase.Hit, atk.phase);

        float before = pos.x;
        var st = new StatusState(); StatusLogic.Apply(st, Defs()[2], 0u, false);   // paralyze
        InstanceStep.Step(ref atk, st, ref pos, Move(Vector2.zero), Ctx(tl), out _);
        Assert.AreEqual(AttackPhase.Idle, atk.phase);    // lunge interrupted
        Assert.AreEqual(before, pos.x, 1e-4f);           // and rooted: no lunge displacement
    }
}
