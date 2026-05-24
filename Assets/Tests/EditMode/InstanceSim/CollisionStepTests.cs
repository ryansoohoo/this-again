using NUnit.Framework;
using UnityEngine;

public class CollisionStepTests
{
    static readonly System.Func<Vector2, bool> Open = _ => true;

    static CollisionBody Body(ulong id, Vector2 pos, float invMass, float radius = 0.5f)
        => new CollisionBody { id = id, pos = pos, radius = radius, invMass = invMass };

    // --- mover-yields ---

    [Test]
    public void MoverIntoPinned_PinnedHolds_MoverPushedToTouching()
    {
        var bodies = new[]
        {
            Body(1, Vector2.zero, 0f),            // pinned
            Body(2, new Vector2(0.6f, 0f), 1f),   // mover, overlapping (0.6 < 1.0 sum-of-radii)
        };
        CollisionStep.Resolve(bodies, 2, Open, 4);
        Assert.AreEqual(Vector2.zero, bodies[0].pos);                  // pinned held its ground
        Assert.AreEqual(1.0f, bodies[1].pos.x, 1e-3f);                // mover pushed out to exactly touching
        Assert.AreEqual(0f, bodies[1].pos.y, 1e-3f);
    }

    [Test]
    public void TwoMovers_HeadOn_SplitEqually()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(-0.3f, 0f), 1f),
            Body(2, new Vector2( 0.3f, 0f), 1f),
        };
        CollisionStep.Resolve(bodies, 2, Open, 4);
        Assert.AreEqual(1.0f, bodies[1].pos.x - bodies[0].pos.x, 1e-3f);   // separated to sum-of-radii
        Assert.AreEqual(-bodies[0].pos.x, bodies[1].pos.x, 1e-3f);         // symmetric about the midpoint
    }

    [Test]
    public void Mover_GrazingPinned_KeepsTangentialMotion_SlidesAround()
    {
        var bodies = new[]
        {
            Body(1, Vector2.zero, 0f),
            Body(2, new Vector2(0.6f, 0.2f), 1f),
        };
        CollisionStep.Resolve(bodies, 2, Open, 4);
        Assert.Greater(bodies[1].pos.y, 0.15f);                           // tangential y not collapsed -> slid around
        Assert.GreaterOrEqual((bodies[1].pos - bodies[0].pos).magnitude, 1.0f - 1e-3f);  // no longer overlapping
    }

    [Test]
    public void BothPinned_SpawnOverlap_StillSeparate()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(-0.1f, 0f), 0f),
            Body(2, new Vector2( 0.1f, 0f), 0f),
        };
        CollisionStep.Resolve(bodies, 2, Open, 6);
        Assert.GreaterOrEqual((bodies[1].pos - bodies[0].pos).magnitude, 1.0f - 1e-3f);
    }

    [Test]
    public void ExactSamePosition_DeterministicTieBreak_Separates()
    {
        var bodies = new[]
        {
            Body(1, Vector2.zero, 1f),
            Body(2, Vector2.zero, 1f),
        };
        CollisionStep.Resolve(bodies, 2, Open, 6);
        Assert.Less(bodies[0].pos.x, bodies[1].pos.x);                    // lower id pushed -x (fixed-axis tie-break)
        Assert.AreEqual(0f, bodies[0].pos.y, 1e-4f);
        Assert.AreEqual(0f, bodies[1].pos.y, 1e-4f);
    }

    // --- walls (walls win over the push) ---

    [Test]
    public void MoverBlockedByWall_ResidualOverlapTolerated()
    {
        System.Func<Vector2, bool> wall = p => p.x <= 0.5f;   // x > 0.5 is non-walkable
        var bodies = new[]
        {
            Body(1, new Vector2(0.2f, 0f), 0f),   // pinned
            Body(2, new Vector2(0.5f, 0f), 1f),   // mover on the wall edge; push would be +x into the wall
        };
        CollisionStep.Resolve(bodies, 2, wall, 4);
        Assert.AreEqual(0.5f, bodies[1].pos.x, 1e-4f);                    // blocked at the wall, not shoved through
        Assert.AreEqual(new Vector2(0.2f, 0f), bodies[0].pos);           // pinned unmoved
    }

    [Test]
    public void MoverPushedTowardWall_SlidesAlong_NeverEntersWall()
    {
        System.Func<Vector2, bool> wall = p => p.x <= 1.0f;   // x > 1.0 is non-walkable
        var bodies = new[]
        {
            Body(1, new Vector2(0.6f, 0f), 0f),   // pinned
            Body(2, new Vector2(1.0f, 0.1f), 1f), // mover wedged against the wall at x = 1
        };
        CollisionStep.Resolve(bodies, 2, wall, 4);
        Assert.LessOrEqual(bodies[1].pos.x, 1.0f + 1e-4f);               // never crossed into the wall
        Assert.Greater(bodies[1].pos.y, 0.2f);                          // slid along the wall in +y instead
    }

    // --- stacks / determinism / no-op ---

    [Test]
    public void ThreeBodyLine_RelaxesTowardNonOverlap()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(0.0f, 0f), 1f),
            Body(2, new Vector2(0.4f, 0f), 1f),
            Body(3, new Vector2(0.8f, 0f), 1f),
        };
        CollisionStep.Resolve(bodies, 3, Open, 24);
        Assert.GreaterOrEqual((bodies[1].pos - bodies[0].pos).magnitude, 1.0f - 0.05f);
        Assert.GreaterOrEqual((bodies[2].pos - bodies[1].pos).magnitude, 1.0f - 0.05f);
    }

    [Test]
    public void NonOverlapping_Untouched()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(0f, 0f), 1f),
            Body(2, new Vector2(2f, 0f), 1f),     // 2.0 apart, radii sum 1.0 -> no overlap
        };
        CollisionStep.Resolve(bodies, 2, Open, 4);
        Assert.AreEqual(new Vector2(0f, 0f), bodies[0].pos);
        Assert.AreEqual(new Vector2(2f, 0f), bodies[1].pos);
    }

    [Test]
    public void Deterministic_SortedInput_SameResultRegardlessOfArrayOrder()
    {
        CollisionBody[] Make(params CollisionBody[] b)
        {
            System.Array.Sort(b, (x, y) => x.id.CompareTo(y.id));   // caller sorts by id
            return b;
        }
        var a = Make(Body(1, new Vector2(0f, 0f), 1f), Body(2, new Vector2(0.3f, 0f), 1f), Body(3, new Vector2(0.6f, 0.1f), 1f));
        var b = Make(Body(3, new Vector2(0.6f, 0.1f), 1f), Body(1, new Vector2(0f, 0f), 1f), Body(2, new Vector2(0.3f, 0f), 1f));
        CollisionStep.Resolve(a, 3, Open, 6);
        CollisionStep.Resolve(b, 3, Open, 6);
        for (int i = 0; i < 3; i++) { Assert.AreEqual(a[i].id, b[i].id); Assert.AreEqual(a[i].pos, b[i].pos); }
    }

    [Test]
    public void Overlap_ReturnsOnlyBodiesWithinProbe()
    {
        var bodies = new[]
        {
            Body(1, new Vector2(0f, 0f), 0f),     // radius 0.5
            Body(2, new Vector2(0.8f, 0f), 0f),   // radius 0.5
            Body(3, new Vector2(5f, 0f), 0f),     // far away
        };
        var results = new int[8];
        int n = CollisionStep.Overlap(bodies, 3, new Vector2(0.2f, 0f), 0.3f, results);
        Assert.AreEqual(2, n);          // body0 (dist 0.2 < 0.8) and body1 (dist 0.6 < 0.8); body2 misses
        Assert.AreEqual(0, results[0]);
        Assert.AreEqual(1, results[1]);
    }
}
