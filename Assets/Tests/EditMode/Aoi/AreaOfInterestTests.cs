using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// EditMode tests for the pure visibility query: self-always-visible, region isolation, radius in/out,
// and show/hide hysteresis. No scene / play-mode needed.
public class AreaOfInterestTests
{
    static ReplicationSettings Cfg() => new ReplicationSettings { showRadius = 10f, hideRadius = 20f, snapshotHz = 15 };

    static List<AoiPlayer> Players(params AoiPlayer[] ps) => new List<AoiPlayer>(ps);

    static HashSet<ulong> Visible(ulong viewer, List<AoiPlayer> ps, ReplicationSettings s, HashSet<ulong> prior)
    {
        var result = new HashSet<ulong>();
        AreaOfInterestSystem.VisibleFor(viewer, ps, s, prior, result);
        return result;
    }

    [Test]
    public void Viewer_AlwaysSeesItself()
    {
        var ps = Players(new AoiPlayer(1, Vector2.zero, Vector2Int.zero));
        var v = Visible(1, ps, Cfg(), new HashSet<ulong>());
        Assert.IsTrue(v.Contains(1));
        Assert.AreEqual(1, v.Count);
    }

    [Test]
    public void NearbyPlayer_SameRegion_IsVisible()
    {
        var ps = Players(new AoiPlayer(1, Vector2.zero, Vector2Int.zero),
                         new AoiPlayer(2, new Vector2(5f, 0f), Vector2Int.zero));
        Assert.IsTrue(Visible(1, ps, Cfg(), new HashSet<ulong>()).Contains(2));
    }

    [Test]
    public void FarPlayer_BeyondShowRadius_IsNotVisible()
    {
        var ps = Players(new AoiPlayer(1, Vector2.zero, Vector2Int.zero),
                         new AoiPlayer(2, new Vector2(15f, 0f), Vector2Int.zero));   // 15 > showRadius 10
        Assert.IsFalse(Visible(1, ps, Cfg(), new HashSet<ulong>()).Contains(2));
    }

    [Test]
    public void DifferentRegion_IsNeverVisible_EvenWhenClose()
    {
        var ps = Players(new AoiPlayer(1, Vector2.zero, Vector2Int.zero),
                         new AoiPlayer(2, new Vector2(1f, 0f), new Vector2Int(16384, 16384)));   // underworld
        Assert.IsFalse(Visible(1, ps, Cfg(), new HashSet<ulong>()).Contains(2));
    }

    [Test]
    public void Hysteresis_AlreadyVisible_StaysUntilHideRadius()
    {
        // 15 cells apart: outside showRadius(10) but inside hideRadius(20). Already-visible -> stays.
        var ps = Players(new AoiPlayer(1, Vector2.zero, Vector2Int.zero),
                         new AoiPlayer(2, new Vector2(15f, 0f), Vector2Int.zero));
        var prior = new HashSet<ulong> { 2 };
        Assert.IsTrue(Visible(1, ps, Cfg(), prior).Contains(2));
    }

    [Test]
    public void Hysteresis_AlreadyVisible_DropsBeyondHideRadius()
    {
        var ps = Players(new AoiPlayer(1, Vector2.zero, Vector2Int.zero),
                         new AoiPlayer(2, new Vector2(25f, 0f), Vector2Int.zero));   // 25 > hideRadius 20
        var prior = new HashSet<ulong> { 2 };
        Assert.IsFalse(Visible(1, ps, Cfg(), prior).Contains(2));
    }

    [Test]
    public void ViewerNotInList_ReturnsEmpty()
    {
        var ps = Players(new AoiPlayer(2, Vector2.zero, Vector2Int.zero));
        Assert.AreEqual(0, Visible(1, ps, Cfg(), new HashSet<ulong>()).Count);
    }
}
