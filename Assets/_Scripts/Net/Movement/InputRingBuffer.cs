using UnityEngine;

// One sampled input + the position it predicted, keyed by client tick. Plain data.
public struct InputFrame
{
    public uint tick;
    public Vector2 input;
    public Vector2 predictedPos;
}

// Fixed-capacity (power-of-two) ring of InputFrames, indexed by tick & mask. Zero per-frame allocation.
// A slot only "matches" if its stored tick equals the requested tick, so a lapped (overwritten) tick reads
// as absent. Used by the client for predict/replay; the server uses it to buffer received inputs.
public sealed class InputRingBuffer
{
    readonly InputFrame[] buf;
    readonly uint mask;

    public InputRingBuffer(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new System.ArgumentException("capacity must be a power of two >= 2", nameof(capacity));
        buf = new InputFrame[capacity];
        mask = (uint)(capacity - 1);
    }

    public void Store(InputFrame f) => buf[f.tick & mask] = f;

    public bool TryGet(uint tick, out InputFrame f)
    {
        f = buf[tick & mask];
        return f.tick == tick;
    }
}
