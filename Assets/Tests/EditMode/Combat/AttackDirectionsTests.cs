using NUnit.Framework;
using UnityEngine;

public class AttackDirectionsTests
{
    static readonly Vector2[] Cardinal = { new Vector2(1, 0), new Vector2(0, 1), new Vector2(-1, 0), new Vector2(0, -1) };
    static readonly Vector2[] Diagonal = { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(-1, -1), new Vector2(1, -1) };

    [Test]
    public void ExactCardinalHit_ZeroResidual()
    {
        var (i, r) = AttackDirections.Pick(Cardinal, new Vector2(0, 1));
        Assert.AreEqual(1, i);
        Assert.AreEqual(0f, r, 1e-3f);
    }

    [Test]
    public void NearCardinal_PicksNearest_SmallResidual()
    {
        var (i, r) = AttackDirections.Pick(Cardinal, new Vector2(0.3f, 1f)); // up-ish, slightly right
        Assert.AreEqual(1, i);                 // North
        Assert.Less(Mathf.Abs(r), 45f);
    }

    [Test]
    public void DiagonalSet_ExactHit_ZeroResidual()
    {
        var (i, r) = AttackDirections.Pick(Diagonal, new Vector2(1, 1));
        Assert.AreEqual(0, i);
        Assert.AreEqual(0f, r, 1e-3f);
    }

    [Test]
    public void FourSet_MaxResidual_AtMidpoint_IsAbout45()
    {
        // Aim exactly between East(0) and North(1): 45 degrees. Nearest is either; |residual| ~ 45.
        var (i, r) = AttackDirections.Pick(Cardinal, new Vector2(1, 1));
        Assert.IsTrue(i == 0 || i == 1);
        Assert.AreEqual(45f, Mathf.Abs(r), 0.5f);
    }

    [Test]
    public void ZeroAim_ReturnsZero()
    {
        var (i, r) = AttackDirections.Pick(Cardinal, Vector2.zero);
        Assert.AreEqual(0, i);
        Assert.AreEqual(0f, r, 1e-3f);
    }
}
