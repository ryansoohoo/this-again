using NUnit.Framework;
using UnityEngine;

public class InputRingBufferTests
{
    [Test]
    public void StoreThenGet_ReturnsSameFrame()
    {
        var buf = new RingBuffer<InputFrame>(8);
        buf.Store(new InputFrame { tick = 3, input = new Vector2(1f, 0f), predictedPos = new Vector2(2f, 2f) });
        Assert.IsTrue(buf.TryGet(3, out var f));
        Assert.AreEqual(3u, f.tick);
        Assert.AreEqual(new Vector2(1f, 0f), f.input);
        Assert.AreEqual(new Vector2(2f, 2f), f.predictedPos);
    }

    [Test]
    public void TryGet_UnseenTick_ReturnsFalse()
    {
        var buf = new RingBuffer<InputFrame>(8);
        buf.Store(new InputFrame { tick = 3, input = Vector2.one });
        Assert.IsFalse(buf.TryGet(4, out _));
    }

    [Test]
    public void LappedTick_ReturnsFalse()
    {
        var buf = new RingBuffer<InputFrame>(8);          // capacity 8, mask 7
        buf.Store(new InputFrame { tick = 1, input = Vector2.one });
        buf.Store(new InputFrame { tick = 9, input = Vector2.zero });   // 9 & 7 == 1 -> overwrites slot of tick 1
        Assert.IsFalse(buf.TryGet(1, out _));
        Assert.IsTrue(buf.TryGet(9, out _));
    }

    [Test]
    public void NonPowerOfTwoCapacity_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => new RingBuffer<InputFrame>(10));
    }
}
