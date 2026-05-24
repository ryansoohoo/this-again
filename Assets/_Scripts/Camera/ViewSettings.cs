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
    [Min(2)] public int startCellsVisible = 16;   // the fixed view height in cells
    [Min(2)] public int minCellsVisible  = 10;    // max zoom-in clamp (cells)

    [Header("Max pan — keep player in view (per-axis inset, 0..1)")]
    public Vector2 followEdgeInset = new Vector2(0.15f, 0.15f);   // 0 = pan to 2x viewport, 1 = locked on player
}
