using System;
using NUnit.Framework;
using UnityEngine;

public class MovementStepTests
{
    static readonly Func<Vector2, bool> Open = _ => true;

    [Test]
    public void MovesAtSpeedAlongInput()
    {
        var p = MovementStep.Step(Vector2.zero, new Vector2(1f, 0f), 0.1f, 4f, Open);
        Assert.AreEqual(0.4f, p.x, 1e-4f);
        Assert.AreEqual(0f, p.y, 1e-4f);
    }

    [Test]
    public void DiagonalInputIsNormalized()
    {
        var p = MovementStep.Step(Vector2.zero, new Vector2(1f, 1f), 0.25f, 4f, Open);
        Assert.AreEqual(1f, p.magnitude, 1e-4f);   // 4 * 0.25 = 1 unit total, not 1.41
    }

    [Test]
    public void ZeroInput_DoesNotMove()
    {
        var p = MovementStep.Step(new Vector2(3f, 3f), Vector2.zero, 0.1f, 4f, Open);
        Assert.AreEqual(new Vector2(3f, 3f), p);
    }

    [Test]
    public void Deterministic_SameInputsSamePath()
    {
        Vector2 a = Vector2.zero, b = Vector2.zero;
        var seq = new[] { new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), new Vector2(-1, 0) };
        foreach (var s in seq) { a = MovementStep.Step(a, s, 1f / 60f, 4f, Open); }
        foreach (var s in seq) { b = MovementStep.Step(b, s, 1f / 60f, 4f, Open); }
        Assert.AreEqual(a, b);
    }

    [Test]
    public void BlockedAhead_SlidesAlongWall()
    {
        // Wall: anything with x >= 1 is blocked. Moving +x is rejected; +y still allowed.
        Func<Vector2, bool> wall = pos => pos.x < 1f;
        var p = MovementStep.Step(new Vector2(0.9f, 0f), new Vector2(1f, 1f), 0.1f, 4f, wall);
        Assert.AreEqual(0.9f, p.x, 1e-4f);     // x move blocked
        Assert.Greater(p.y, 0f);               // y move slid through
    }
}
