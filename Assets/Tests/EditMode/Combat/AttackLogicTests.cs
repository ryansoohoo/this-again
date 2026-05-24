using NUnit.Framework;
using UnityEngine;

public class AttackLogicTests
{
    static AttackTimeline MakeTimeline() => new AttackTimeline
    {
        anticipation    = new[] { new TimedFrame { column = 0, duration = 0.1f }, new TimedFrame { column = 1, duration = 0.1f } },
        tapAnticipation = new[] { new TimedFrame { column = 0, duration = 0.1f } },
        hit             = new[] { new TimedFrame { column = 2, duration = 0.1f } },
        followThrough   = new[] { new TimedFrame { column = 3, duration = 0.1f } },
        directions      = new[] { new DirectionEntry { canonicalDir = new Vector2(1, 0), row = 0 },
                                  new DirectionEntry { canonicalDir = new Vector2(0, 1), row = 1 } },
        dirs            = new[] { new Vector2(1, 0), new Vector2(0, 1) },
        feintCooldown   = 0.5f,
    };

    static AttackIntent Press(Vector2 aim) => new AttackIntent { pressed = true, held = true, aimDir = aim };
    static AttackIntent Hold(Vector2 aim) => new AttackIntent { held = true, aimDir = aim };
    static AttackIntent Release(Vector2 aim) => new AttackIntent { released = true, held = false, aimDir = aim };
    static AttackIntent Feint(Vector2 aim) => new AttackIntent { held = true, feint = true, aimDir = aim };
    static AttackIntent Idle() => new AttackIntent { aimDir = new Vector2(1, 0) };

    [Test]
    public void Press_FromIdle_EntersAnticipation()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(0, 1)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.Anticipation, s.phase);
        Assert.AreEqual(1, s.dirIndex); // North
    }

    // (Cooldown-in-AttackLogic tests removed: the feint lockout is now the AttackCooldown status effect — its
    // duration/decrement is covered by StatusLogicTests, and the gate's start-block by GateTests.)

    [Test]
    public void Anticipation_HoldsLastFrame_WhenComplete()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        for (int k = 0; k < 30; k++) s = AttackLogic.Step(s, Hold(new Vector2(1, 0)), tl, PhaseScales.One, 0.1f);
        Assert.AreEqual(AttackPhase.Anticipation, s.phase);
        Assert.IsTrue(s.windupComplete);
        Assert.AreEqual(1, AttackLogic.CurrentColumn(s, tl)); // last anticipation column
    }

    [Test]
    public void Anticipation_ReAims()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        Assert.AreEqual(0, s.dirIndex);
        s = AttackLogic.Step(s, Hold(new Vector2(0, 1)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(1, s.dirIndex); // re-aimed to North
    }

    [Test]
    public void Feint_GoesIdle()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, Feint(new Vector2(1, 0)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.Idle, s.phase);   // feint cancels to Idle; the AttackCooldown effect enforces the lockout
    }

    [Test]
    public void ReleaseEarly_GoesToTapWindup()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f); // frame 0, not complete
        s = AttackLogic.Step(s, Release(new Vector2(1, 0)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.TapWindup, s.phase);
        Assert.AreEqual(0, s.frameIndex);
    }

    [Test]
    public void ReleaseAfterComplete_GoesStraightToHit()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        for (int k = 0; k < 30; k++) s = AttackLogic.Step(s, Hold(new Vector2(1, 0)), tl, PhaseScales.One, 0.1f);
        s = AttackLogic.Step(s, Release(new Vector2(1, 0)), tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.Hit, s.phase);
    }

    [Test]
    public void ButtonUp_NoReleasedEdge_StillCommits()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, new AttackIntent { held = false, aimDir = new Vector2(1, 0) }, tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(AttackPhase.TapWindup, s.phase);
    }

    [Test]
    public void FullSequence_Tap_HitThenFollowThroughThenIdle()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, Release(new Vector2(1, 0)), tl, PhaseScales.One, 0f); // -> TapWindup
        bool sawHit = false;
        for (int k = 0; k < 20; k++)
        {
            s = AttackLogic.Step(s, Idle(), tl, PhaseScales.One, 0.1f);
            if (s.phase == AttackPhase.Hit) sawHit = true;
        }
        Assert.IsTrue(sawHit);
        Assert.AreEqual(AttackPhase.Idle, s.phase);
    }

    [Test]
    public void DirectionLocks_AfterCommit()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, Release(new Vector2(1, 0)), tl, PhaseScales.One, 0f); // commit, dirIndex=0
        s = AttackLogic.Step(s, new AttackIntent { aimDir = new Vector2(0, 1) }, tl, PhaseScales.One, 0.016f);
        Assert.AreEqual(0, s.dirIndex); // did NOT re-aim to North
    }

    [Test]
    public void Scale_SlowsAnticipation()
    {
        var tl = MakeTimeline();
        var fast = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        fast = AttackLogic.Step(fast, Hold(new Vector2(1, 0)), tl, PhaseScales.One, 0.1f);
        Assert.AreEqual(1, fast.frameIndex); // advanced past frame 0 at scale 1

        var slow = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        var s2 = new PhaseScales { anticipation = 2f, hit = 1f, followThrough = 1f };
        slow = AttackLogic.Step(slow, Hold(new Vector2(1, 0)), tl, s2, 0.1f);
        Assert.AreEqual(0, slow.frameIndex); // 0.1s < 0.1*2, still on frame 0
    }

    [Test]
    public void TimeToHit_TapIsSumOfTapWindup_FullIsZero()
    {
        var tl = MakeTimeline();
        Assert.AreEqual(0.1f, AttackLogic.TimeToHit(tl, tapped: true, PhaseScales.One), 1e-4f);
        Assert.AreEqual(0.2f, AttackLogic.TimeToHit(tl, tapped: true, new PhaseScales { anticipation = 2f, hit = 1f, followThrough = 1f }), 1e-4f);
        Assert.AreEqual(0f, AttackLogic.TimeToHit(tl, tapped: false, PhaseScales.One), 1e-4f);
    }

    [Test]
    public void LungeDuration_SumsHitAndFollowThrough()
    {
        var tl = MakeTimeline(); // hit 0.1 + followThrough 0.1
        Assert.AreEqual(0.2f, AttackLogic.LungeDuration(tl), 1e-4f);
    }

    [Test]
    public void LungeProgress_ZeroOutsideHitWindow()
    {
        var tl = MakeTimeline();
        Assert.AreEqual(0f, AttackLogic.LungeProgress(new AttackState { phase = AttackPhase.Idle }, tl), 1e-4f);
        Assert.AreEqual(0f, AttackLogic.LungeProgress(new AttackState { phase = AttackPhase.Anticipation }, tl), 1e-4f);
    }

    [Test]
    public void LungeProgress_RampsAcrossHitThenFollowThrough()
    {
        var tl = MakeTimeline(); // hit 0.1, followThrough 0.1, total 0.2
        Assert.AreEqual(0f, AttackLogic.LungeProgress(new AttackState { phase = AttackPhase.Hit, phaseElapsed = 0f }, tl), 1e-4f);
        Assert.AreEqual(0.25f, AttackLogic.LungeProgress(new AttackState { phase = AttackPhase.Hit, phaseElapsed = 0.05f }, tl), 1e-4f);
        Assert.AreEqual(0.75f, AttackLogic.LungeProgress(new AttackState { phase = AttackPhase.FollowThrough, phaseElapsed = 0.05f }, tl), 1e-4f);
    }

    [Test]
    public void LockedAim_SetAtCommit_ToAimDirection()
    {
        var tl = MakeTimeline();
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, Release(new Vector2(0, 1)), tl, PhaseScales.One, 0f); // commit aiming North
        Assert.AreEqual(0f, s.lockedAim.x, 1e-3f);
        Assert.AreEqual(1f, s.lockedAim.y, 1e-3f);
    }

    [Test]
    public void LockedAim_IncludesResidualRotation()
    {
        var tl = MakeTimeline(); // dirs East (1,0), North (0,1)
        var s = AttackLogic.Step(default, Press(new Vector2(1, 0)), tl, PhaseScales.One, 0f);
        s = AttackLogic.Step(s, Release(new Vector2(1, 1)), tl, PhaseScales.One, 0f); // commit aiming NE: 45 deg off East
        var expected = new Vector2(1, 1).normalized;
        Assert.AreEqual(expected.x, s.lockedAim.x, 1e-3f);
        Assert.AreEqual(expected.y, s.lockedAim.y, 1e-3f);
    }

    static AttackTimeline TimelineWithCurve(AnimationCurve c)
    {
        var tl = MakeTimeline();
        tl.lungeCurve = c;
        return tl;
    }

    [Test]
    public void LungeVelocity_NullOutsideHitWindow()
    {
        var tl = TimelineWithCurve(AnimationCurve.Constant(0, 1, 1f));
        Assert.IsNull(AttackLogic.LungeVelocity(new AttackState { phase = AttackPhase.Idle }, tl));
    }

    [Test]
    public void LungeVelocity_ZeroDuringWindup()
    {
        var tl = TimelineWithCurve(AnimationCurve.Constant(0, 1, 1f));
        Assert.AreEqual(Vector2.zero, AttackLogic.LungeVelocity(new AttackState { phase = AttackPhase.Anticipation }, tl));
        Assert.AreEqual(Vector2.zero, AttackLogic.LungeVelocity(new AttackState { phase = AttackPhase.TapWindup }, tl));
    }

    [Test]
    public void LungeVelocity_InHit_IsLockedAimTimesCurve()
    {
        var tl = TimelineWithCurve(AnimationCurve.Constant(0, 1, 1f)); // curve = 1 everywhere
        var s = new AttackState { phase = AttackPhase.Hit, phaseElapsed = 0f, lockedAim = new Vector2(1, 0) };
        var v = AttackLogic.LungeVelocity(s, tl).Value;
        Assert.AreEqual(1f, v.x, 1e-3f);
        Assert.AreEqual(0f, v.y, 1e-3f);
    }

    [Test]
    public void LungeVelocity_NullCurve_IsZeroVector()
    {
        var tl = MakeTimeline(); // lungeCurve null
        var s = new AttackState { phase = AttackPhase.Hit, lockedAim = new Vector2(1, 0) };
        Assert.AreEqual(Vector2.zero, AttackLogic.LungeVelocity(s, tl).Value);
    }
}
