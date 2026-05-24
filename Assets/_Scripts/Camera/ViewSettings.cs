using System;
using UnityEngine;

// Live-tunable view extents (serialized on Game; persisted via JsonPref "view.json" like the other settings
// groups). Held by reference by CameraSystem + WorldView so the tuner's edits take effect at runtime. Covers
// the three view sizes: the minimap overview, the camera viewport, and the keep-player-in-view pan range.
[Serializable]
public class ViewSettings
{
    [Header("Minimap overview (half-extent in cells; total = 2r+1)")]
    public Vector2Int minimapRadius = new Vector2Int(40, 40);

    [Header("Viewport (cells tall; width follows screen aspect)")]
    [Min(2)] public int overworldCellsTall  = 30;   // view height in the overworld
    [Min(2)] public int underworldCellsTall = 20;   // view height inside a dungeon instance
    [Min(2)] public int minCellsVisible     = 10;   // max zoom-in clamp (cells)
}
