using UnityEngine;

// Visual: the SOLE writer of the camera transform. Mirrors CameraState (computed by CameraSystem) onto the
// Camera, called by Game right after CameraSystem.Tick. Reads data; writes only the camera.
public sealed class CameraView
{
    public void Apply(Camera cam, CameraState s)
    {
        cam.transform.position = s.Position;
        cam.orthographicSize = s.OrthoSize;
    }
}
