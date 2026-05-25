using NUnit.Framework;
using UnityEngine;

public class StatusForcedMoveTests
{
    static StatusEffectDef FearDef() => new StatusEffectDef
    {
        id = (byte)StatusKind.Fear, durationTicks = 60, blocksMove = true, blocksAttack = true,
        moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1,
        forcedMove = ForcedMoveKind.FleeFrozen, forcedMoveScale = 0.8f,
    };

    static StatusEffectDef[] Defs()
    {
        var d = new StatusEffectDef[8];
        d[(int)StatusKind.Fear] = FearDef();
        return d;
    }

    [Test]
    public void Apply_StoresFrozenFleeDir()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Fear], tick: 1, self: false, forcedDir: new Vector2(1, 0));
        Assert.AreEqual(1, s.count);
        Assert.AreEqual(new Vector2(1, 0), s.effects[0].fleeDir);
    }

    [Test]
    public void ActiveForcedMove_ReturnsStoredDirAndScale()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.Fear], tick: 1, self: false, forcedDir: new Vector2(0, -1));
        bool any = StatusLogic.ActiveForcedMove(s, defs, out var dir, out var scale);
        Assert.IsTrue(any);
        Assert.AreEqual(new Vector2(0, -1), dir);
        Assert.AreEqual(0.8f, scale, 1e-4f);
    }

    [Test]
    public void ActiveForcedMove_FalseWhenNoForcedEffect()
    {
        var s = new StatusState();
        Assert.IsFalse(StatusLogic.ActiveForcedMove(s, Defs(), out _, out _));
    }
}
