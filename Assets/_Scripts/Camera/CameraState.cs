using UnityEngine;

// Data: the camera's live state. Inputs Bounds (set by WorldView) and FollowTarget (set by Game); outputs
// Position + OrthoSize (computed by CameraSystem, applied by CameraView). Plain data — no behavior.
public sealed class CameraState
{
    public Rect Bounds;            // loaded vision window the viewport must stay inside
    public Vector2? FollowTarget;  // local player world position to keep on-screen (null = none)
    public Vector3 Position;       // authoritative camera position (CameraView writes it onto the Camera)
    public float OrthoSize;        // authoritative orthographic size
}
