using UnityEngine;

// Deterministic "underworld" geometry: a band of flat-plains rooms parked off-map (~16k cells away), one
// slot per dungeon. Pure functions -> every Netcode client computes identical rooms with no replication, exactly
// like the overworld terrain. World delegates band cells here (a square of flat ground surrounded by a wide
// water moat that acts as walls); PlayerMovement teleports a player to RegionOriginForSite(...) on enter and
// back on leave. Isolation is by distance only: rooms are far enough apart that no other room ever enters the
// view window, so you only see whoever shares your room.
public static class Underworld
{
    public const int RoomSize = 24;                 // interior plains is RoomSize x RoomSize ground cells
    public const int Margin   = 64;                 // water-moat ring (also keeps neighbour rooms outside the view radius)
    public const int Stride   = RoomSize + Margin * 2;   // full per-room slot footprint (152)

    // The band sits at modest coordinates ON PURPOSE: a pixel-perfect 2D camera needs sub-pixel float precision,
    // and float32 ULP reaches ~1px at ~1,000,000 units (16 px/unit) -> visible camera jitter. Keeping every room
    // cell under ~32,768 holds ULP at ~1/32 px (smooth), while still landing far past where overworld play reaches.
    const int BaseX = 16_384, BaseY = 16_384;            // band origin
    const int GridSpan = 100;                             // slots per axis (100x100 rooms; furthest cell ~31,584)
    const int BandExtent = GridSpan * Stride;            // band width in cells (15,200)

    // Is this cell inside the underworld band at all?
    public static bool Contains(int cx, int cy) =>
        cx >= BaseX && cy >= BaseY && cx < BaseX + BandExtent && cy < BaseY + BandExtent;

    // Within the band: true on a room's interior plains, false on its moat ring (so the moat is non-walkable water).
    public static bool IsGround(int cx, int cy)
    {
        int lx = Mod(cx - BaseX, Stride), ly = Mod(cy - BaseY, Stride);
        return lx >= Margin && lx < Margin + RoomSize && ly >= Margin && ly < Margin + RoomSize;
    }

    // The interior min-corner cell of the room a dungeon site maps to (deterministic; collisions vanishingly rare).
    public static Vector2Int RegionOriginForSite(int siteX, int siteY)
    {
        int slotX = (int)(Hash(siteX, siteY, 9001) % GridSpan);
        int slotY = (int)(Hash(siteX, siteY, 9002) % GridSpan);
        return new Vector2Int(BaseX + slotX * Stride + Margin, BaseY + slotY * Stride + Margin);
    }

    // Where a player lands in a room: clustered near the centre, nudged per seat so several arrivals don't stack.
    public static Vector2Int SpawnCell(Vector2Int origin, int seat)
    {
        int half = RoomSize / 2;
        int ox = (seat % 5) - 2;
        int oy = ((seat / 5) % 5) - 2;
        int x = Mathf.Clamp(origin.x + half + ox, origin.x, origin.x + RoomSize - 1);
        int y = Mathf.Clamp(origin.y + half + oy, origin.y, origin.y + RoomSize - 1);
        return new Vector2Int(x, y);
    }

    // Non-negative modulo (band coords are >= Base, so this is belt-and-suspenders).
    static int Mod(int a, int m) { int r = a % m; return r < 0 ? r + m : r; }

    // Same hash family as World/StructureGenerator, returning a raw uint for slot indexing.
    static uint Hash(int x, int y, int salt)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(salt * 2654435761);
            h ^= h >> 13; h *= 0x85ebca6b; h ^= h >> 16;
            return h;
        }
    }
}
