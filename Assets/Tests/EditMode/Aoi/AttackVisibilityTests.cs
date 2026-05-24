using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class AttackVisibilityTests
{
    [Test]
    public void InInstance_SeesWholeRoom_RegardlessOfRadius()
    {
        var room = new Vector2Int(16384, 16384);
        var players = new List<AoiPlayer>
        {
            new AoiPlayer(1, new Vector2(0, 0), room),
            new AoiPlayer(2, new Vector2(1000, 1000), room),   // far beyond showRadius, same room
        };
        var s = new ReplicationSettings { showRadius = 10, hideRadius = 12 };
        var result = new HashSet<ulong>();
        AreaOfInterestSystem.VisibleFor(1, players, s, new HashSet<ulong>(), result);
        Assert.IsTrue(result.Contains(2));   // same room -> visible despite distance
    }

    [Test]
    public void Overworld_StillUsesRadius()
    {
        var ow = Vector2Int.zero;
        var players = new List<AoiPlayer>
        {
            new AoiPlayer(1, new Vector2(0, 0), ow),
            new AoiPlayer(2, new Vector2(1000, 0), ow),
        };
        var s = new ReplicationSettings { showRadius = 10, hideRadius = 12 };
        var result = new HashSet<ulong>();
        AreaOfInterestSystem.VisibleFor(1, players, s, new HashSet<ulong>(), result);
        Assert.IsFalse(result.Contains(2));   // far overworld -> not visible
    }

    [Test]
    public void DifferentRooms_NeverVisible()
    {
        var players = new List<AoiPlayer>
        {
            new AoiPlayer(1, new Vector2(0, 0), new Vector2Int(16384, 16384)),
            new AoiPlayer(2, new Vector2(0, 0), new Vector2Int(16536, 16384)),   // adjacent room
        };
        var s = new ReplicationSettings { showRadius = 96, hideRadius = 128 };
        var result = new HashSet<ulong>();
        AreaOfInterestSystem.VisibleFor(1, players, s, new HashSet<ulong>(), result);
        Assert.IsFalse(result.Contains(2));
    }
}
