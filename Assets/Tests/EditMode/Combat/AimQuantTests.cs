using NUnit.Framework;
using UnityEngine;

public class AimQuantTests
{
    [Test]
    public void RoundTrip_PreservesDirectionWithinTolerance()
    {
        foreach (var deg in new[] { 0f, 45f, 90f, 137f, 180f, -90f, 359f })
        {
            var v = new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
            ushort q = AimQuant.Encode(v);
            Vector2 r = AimQuant.Decode(q);
            Assert.Less(Vector2.Angle(v, r), 0.02f);   // < 0.02 degrees error
        }
    }

    [Test]
    public void Deterministic_SameVector_SameCode()
    {
        var v = new Vector2(0.3f, 0.7f);
        Assert.AreEqual(AimQuant.Encode(v), AimQuant.Encode(v));
    }

    [Test]
    public void ZeroVector_EncodesToZero()
    {
        Assert.AreEqual(0, AimQuant.Encode(Vector2.zero));
    }
}
