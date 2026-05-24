// Fixed-capacity (power-of-two) ring of InputCommands, indexed by tick & mask. Mirrors InputRingBuffer but for
// the combined command (kept in Net so Minifantasy.Movement stays pure). A slot matches only if its tick equals,
// so a lapped (overwritten) tick reads as absent. Zero per-frame allocation.
public sealed class CommandRingBuffer
{
    readonly InputCommand[] buf;
    readonly uint mask;

    public CommandRingBuffer(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new System.ArgumentException("capacity must be a power of two >= 2", nameof(capacity));
        buf = new InputCommand[capacity];
        mask = (uint)(capacity - 1);
    }

    public void Store(InputCommand f) => buf[f.tick & mask] = f;

    public bool TryGet(uint tick, out InputCommand f)
    {
        f = buf[tick & mask];
        return f.tick == tick;
    }
}
