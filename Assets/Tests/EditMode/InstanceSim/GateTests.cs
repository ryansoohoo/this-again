using NUnit.Framework;
using UnityEngine;

// Action-gate primitive: the source-keyed reduce (overlap / race safety), the wire quantization, the attack
// interrupt + start-block, and gated movement through the shared InstanceStep.
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

    // ---- AbilityGate reduce: overlapping sources compose, clearing one keeps the rest ----

    [Test]
    public void Empty_ReducesToNone()
    {
        var e = new AbilityGate().Effective;
        Assert.IsTrue(e.CanMove);
        Assert.IsTrue(e.CanAttack);
        Assert.AreEqual(1f, e.moveScale, 1e-4f);
    }

    [Test]
    public void OverlappingSlows_Multiply()
    {
        var g = new AbilityGate();
        g.Set(1, new GateMod { moveScale = 0.5f });
        g.Set(2, new GateMod { moveScale = 0.5f });
        Assert.AreEqual(0.25f, g.Effective.moveScale, 1e-4f);
    }

    [Test]
    public void IndependentBlocks_Compose()
    {
        var g = new AbilityGate();
        g.Set(1, new GateMod { blocksMove = true, moveScale = 1f });
        g.Set(2, new GateMod { blocksAttack = true, moveScale = 1f });
        Assert.IsFalse(g.Effective.CanMove);
        Assert.IsFalse(g.Effective.CanAttack);
    }

    [Test]
    public void ClearingOneSource_LeavesTheOther()
    {
        var g = new AbilityGate();
        g.Set(1, new GateMod { blocksMove = true, moveScale = 1f });
        g.Set(2, new GateMod { blocksAttack = true, moveScale = 1f });
        g.Clear(1);
        Assert.IsTrue(g.Effective.CanMove);     // move re-enabled (its only blocker cleared)
        Assert.IsFalse(g.Effective.CanAttack);  // attack still blocked by the surviving source
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
        Assert.AreEqual(0f, s.cooldown, 1e-4f);   // interrupt clears cooldown (not a feint)
    }

    // ---- InstanceStep gating: movement scaled/blocked, lunge interrupted ----

    static InstanceCtx Ctx(AttackTimeline tl, GateMod? gate) => new InstanceCtx
    {
        timeline = tl, scales = PhaseScales.One, dt = 0.02f, speed = 4f, walkable = _ => true, gate = gate,
    };

    static InstanceInput Move(Vector2 m) => new InstanceInput { rawMove = m, attack = new AttackIntent { aimDir = new Vector2(1, 0) } };

    [Test]
    public void BlockedMove_DoesNotWalk()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero;
        InstanceStep.Step(ref atk, ref pos, Move(new Vector2(1, 0)), Ctx(tl, new GateMod { blocksMove = true, moveScale = 1f }));
        Assert.AreEqual(0f, pos.x, 1e-4f);
    }

    [Test]
    public void MoveScale_HalvesDistance()
    {
        var tl = Tl();
        var fa = default(AttackState); Vector2 full = Vector2.zero;
        InstanceStep.Step(ref fa, ref full, Move(new Vector2(1, 0)), Ctx(tl, GateMod.None));
        var ha = default(AttackState); Vector2 half = Vector2.zero;
        InstanceStep.Step(ref ha, ref half, Move(new Vector2(1, 0)), Ctx(tl, new GateMod { moveScale = 0.5f }));
        Assert.AreEqual(full.x * 0.5f, half.x, 1e-4f);
    }

    [Test]
    public void GatedAttack_InterruptsLunge_AndParalyzeRoots()
    {
        var tl = Tl();
        var atk = default(AttackState); Vector2 pos = Vector2.zero;
        // Commit a tap so we reach the Hit lunge.
        InstanceStep.Step(ref atk, ref pos, new InstanceInput { attack = new AttackIntent { pressed = true, held = true, aimDir = new Vector2(1, 0) } }, Ctx(tl, GateMod.None));
        InstanceStep.Step(ref atk, ref pos, new InstanceInput { attack = new AttackIntent { released = true, aimDir = new Vector2(1, 0) } }, Ctx(tl, GateMod.None));
        for (int i = 0; i < 10 && atk.phase != AttackPhase.Hit; i++)
            InstanceStep.Step(ref atk, ref pos, Move(Vector2.zero), Ctx(tl, GateMod.None));
        Assert.AreEqual(AttackPhase.Hit, atk.phase);

        float before = pos.x;
        var paralyze = new GateMod { blocksMove = true, blocksAttack = true, moveScale = 0f };
        InstanceStep.Step(ref atk, ref pos, Move(Vector2.zero), Ctx(tl, paralyze));
        Assert.AreEqual(AttackPhase.Idle, atk.phase);    // lunge interrupted
        Assert.AreEqual(before, pos.x, 1e-4f);           // and rooted: no lunge displacement
    }
}
