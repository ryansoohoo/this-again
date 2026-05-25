using UnityEngine;

// A tick-stamped value a RingBuffer can index by tick. Implemented by the client InputFrame and the server
// InputCommand so one ring serves both — and so Minifantasy.Movement never depends on Netcode: the interface
// lives here, and the Netcode-bearing InputCommand implements it from the Net assembly.
public interface ITick { uint Tick { get; } }

// One sampled input + the position it predicted, keyed by client tick. Plain data.
public struct InputFrame : ITick
{
    public uint tick;
    public Vector2 input;
    public Vector2 predictedPos;
    public uint Tick => tick;
}

// Fixed-capacity (power-of-two) ring of tick-stamped structs, indexed by tick & mask. A slot "matches" only if
// its stored tick equals the requested one, so a lapped (overwritten) tick reads as absent. The struct + ITick
// constraint makes the Tick read a direct (non-boxing) call, so this allocates nothing per frame. Client
// predict/replay store InputFrames; the server buffers received InputCommands in the same ring.
public sealed class RingBuffer<T> where T : struct, ITick
{
    readonly T[] buf;
    readonly uint mask;

    public RingBuffer(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new System.ArgumentException("capacity must be a power of two >= 2", nameof(capacity));
        buf = new T[capacity];
        mask = (uint)(capacity - 1);
    }

    public void Store(T f) => buf[f.Tick & mask] = f;

    public bool TryGet(uint tick, out T f)
    {
        f = buf[tick & mask];
        return f.Tick == tick;
    }
}
