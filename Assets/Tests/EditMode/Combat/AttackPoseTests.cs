using NUnit.Framework;

public class AttackPoseTests
{
    [Test]
    public void Pack_Unpack_RoundTrips()
    {
        byte b = AttackPose.Pack(AttackPhase.Hit, 5, 3);
        AttackPose.Unpack(b, out var ph, out var fr, out var dr);
        Assert.AreEqual(AttackPhase.Hit, ph);
        Assert.AreEqual(5, fr);
        Assert.AreEqual(3, dr);
    }

    [Test]
    public void Pack_Unpack_AllPhases()
    {
        foreach (AttackPhase p in System.Enum.GetValues(typeof(AttackPhase)))
        {
            AttackPose.Unpack(AttackPose.Pack(p, 0, 0), out var ph, out _, out _);
            Assert.AreEqual(p, ph);
        }
    }
}
