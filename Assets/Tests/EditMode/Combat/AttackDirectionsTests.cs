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

    [Test]
    public void CanonicalDiagonal_IsRowOrdered_SE_SW_NE_NW()
    {
        var d = AttackDirections.Diagonal;
        Assert.AreEqual(4, d.Length);
        Assert.AreEqual(1f, d[0].x); Assert.AreEqual(-1f, d[0].y);   // row 0 SE
        Assert.AreEqual(-1f, d[1].x); Assert.AreEqual(-1f, d[1].y);  // row 1 SW
        Assert.AreEqual(1f, d[2].x); Assert.AreEqual(1f, d[2].y);    // row 2 NE
        Assert.AreEqual(-1f, d[3].x); Assert.AreEqual(1f, d[3].y);   // row 3 NW
    }

    [Test]
    public void CanonicalCardinal_IsRowOrdered_E_W_S_N()
    {
        var c = AttackDirections.Cardinal;
        Assert.AreEqual(4, c.Length);
        Assert.AreEqual(1f, c[0].x); Assert.AreEqual(0f, c[0].y);    // row 0 E
        Assert.AreEqual(-1f, c[1].x); Assert.AreEqual(0f, c[1].y);   // row 1 W
        Assert.AreEqual(0f, c[2].x); Assert.AreEqual(-1f, c[2].y);   // row 2 S
        Assert.AreEqual(0f, c[3].x); Assert.AreEqual(1f, c[3].y);    // row 3 N
    }

    [Test]
    public void Entries_AssignsRowEqualToIndex()
    {
        var e = AttackDirections.Entries(AttackDirections.Cardinal);
        Assert.AreEqual(AttackDirections.Cardinal.Length, e.Length);
        for (int i = 0; i < e.Length; i++)
        {
            Assert.AreEqual(i, e[i].row);
            Assert.AreEqual(AttackDirections.Cardinal[i].x, e[i].canonicalDir.x);
            Assert.AreEqual(AttackDirections.Cardinal[i].y, e[i].canonicalDir.y);
        }
    }
}
