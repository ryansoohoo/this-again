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

    [Header("Camera follow")]
    [Range(0f, 0.5f)] public float followSmoothTime = 0.12f;   // SmoothDamp seconds toward the player (0 = instant lock)

    // Snap the camera to the texel grid for crisp pixels. Downside: it steps a smoothly-moving follow camera in
    // whole-pixel jumps, so the sub-pixel player wobbles against it = jitter while moving. Off = smooth sub-pixel
    // follow (slightly softer moving sprites). For crisp AND smooth, render to a low-res target (pixel-perfect cam).
    public bool pixelPerfect = false;
}
