using UnityEngine;

// Minimal per-player input to the visibility query (no Netcode / engine-object deps) so
// AreaOfInterestSystem stays pure and unit-testable. The server builds one per player each tick.
public struct AoiPlayer
{
    public ulong id;
    public Vector2 pos;
    public Vector2Int regionKey;   // (0,0) = overworld; else the underworld room interior origin

    public AoiPlayer(ulong id, Vector2 pos, Vector2Int regionKey)
    {
        this.id = id; this.pos = pos; this.regionKey = regionKey;
    }
}
