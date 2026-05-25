using NUnit.Framework;

public class StatsTests
{
    static Stats WithMaxHp(float v) { var s = new Stats(); s.SetBase(StatKind.MaxHp, v); return s; }

    [Test]
    public void BaseOnly()
    {
        Assert.AreEqual(100, WithMaxHp(100).GetInt(StatKind.MaxHp));
    }

    [Test]
    public void AddModifier()
    {
        var s = WithMaxHp(100);
        s.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Add, value = 20, sourceId = 1 });
        Assert.AreEqual(120, s.GetInt(StatKind.MaxHp));
    }

    [Test]
    public void MulModifier()
    {
        var s = WithMaxHp(100);
        s.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Mul, value = 1.5f, sourceId = 1 });
        Assert.AreEqual(150, s.GetInt(StatKind.MaxHp));
    }

    [Test]
    public void AddThenMul_OrderIndependent()
    {
        var a = WithMaxHp(100);
        a.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Add, value = 50, sourceId = 1 });
        a.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Mul, value = 2f, sourceId = 2 });

        var b = WithMaxHp(100);
        b.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Mul, value = 2f, sourceId = 2 });
        b.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Add, value = 50, sourceId = 1 });

        Assert.AreEqual(300, a.GetInt(StatKind.MaxHp));                       // (100 + 50) * 2
        Assert.AreEqual(a.GetInt(StatKind.MaxHp), b.GetInt(StatKind.MaxHp));  // order-independent
    }

    [Test]
    public void RemoveBySource()
    {
        var s = WithMaxHp(100);
        s.AddModifier(new StatModifier { stat = StatKind.MaxHp, op = ModOp.Add, value = 20, sourceId = 7 });
        s.RemoveBySource(7);
        Assert.AreEqual(100, s.GetInt(StatKind.MaxHp));
    }
}
