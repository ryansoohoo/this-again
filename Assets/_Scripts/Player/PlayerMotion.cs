using System.Collections.Generic;
using UnityEngine;

// Data: server-side tile-movement state for one player. Logic (PlayerMovement) writes it; it is never
// replicated (clients see only the authoritative transform). Plain data — no behavior.
public sealed class PlayerMotion
{
    public Vector2Int cell;
    public bool moving;
    public Vector2Int toCell;
    public Vector2 fromPos, toPos;
    public float moveT, stepDuration;
    public bool hasTarget;
    public Vector2Int targetCell;
    public readonly List<Vector2Int> path = new();   // A* route (cells to step onto, in order)
    public int pathIndex;
}
