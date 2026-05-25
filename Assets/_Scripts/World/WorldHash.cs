// The one deterministic integer hash mixer for world generation. Terrain (World), structure placement
// (StructureGenerator) and the off-map underworld band (Underworld) all hash through here, so a given
// (cell, seed, salt) yields the identical value on every Netcode client with no replication. Previously this
// mixer was copy-pasted into all three; centralizing it removes the silent-desync risk of the copies drifting.
public static class WorldHash
{
    // Raw 32-bit hash of (x, y, seed, salt). seed == 0 drops the seed term (a ^ 0 == a), which is exactly the
    // seedless variant the underworld band uses.
    public static uint Raw(int x, int y, int seed, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(seed * 83492791) ^ (uint)(salt * 2654435761);
            h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
            return h;
        }
    }

    // 0..1 float from the low 24 bits (the old World / StructureGenerator Hash01 result).
    public static float Unit(int x, int y, int seed, int salt) => (Raw(x, y, seed, salt) & 0xFFFFFF) / 16777216f;
}
