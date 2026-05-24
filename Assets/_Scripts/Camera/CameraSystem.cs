using UnityEngine;

// Logic: 2D camera locked on the player — centers on CameraState.FollowTarget every frame, with a min-zoom
// clamp, pixel-perfect snap, and a bounds clamp (so unloaded area can't show). No panning / recenter. Reads
// CameraState + the camera's projection (aspect); writes the desired Position/OrthoSize into CameraState.
// Never writes the camera — CameraView does that.
public sealed class CameraSystem
{
    readonly Camera cam;
    readonly ViewSettings vs;
    readonly float cellWorld;
    readonly CameraState state;
    const float PixelsPerUnit = 16f;

    int lastScreenW, lastScreenH;
    Vector3 smoothPos, followVel;                 // unsnapped smooth-follow source (state.Position is its snapped copy)
    const float TeleportSnapDist = 30f;           // cells; a jump larger than this is a teleport (snap, don't pan)

    public CameraSystem(Camera cam, ViewSettings vs, float cellWorld, CameraState state)
    {
        this.cam = cam; this.vs = vs; this.cellWorld = cellWorld; this.state = state;
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;
        state.Position = cam.transform.position;                       // seed from the camera's boot position
        smoothPos = state.Position;
        state.OrthoSize = ClampOrtho(CellsToOrtho(vs.overworldCellsTall));
    }

    public void Tick(float dt)
    {
        if (Screen.width != lastScreenW || Screen.height != lastScreenH)
        {
            lastScreenW = Screen.width; lastScreenH = Screen.height;
            state.OrthoSize = ClampOrtho(state.OrthoSize);
        }

        if (state.FollowTarget.HasValue)                              // smooth-follow the player
        {
            Vector3 target = new Vector3(state.FollowTarget.Value.x, state.FollowTarget.Value.y, smoothPos.z);
            if ((target - smoothPos).sqrMagnitude > TeleportSnapDist * TeleportSnapDist)
            { smoothPos = target; followVel = Vector3.zero; }         // teleport: snap, don't pan across the world
            else
                smoothPos = Vector3.SmoothDamp(smoothPos, target, ref followVel,
                                               Mathf.Max(vs.followSmoothTime, 0f), Mathf.Infinity, Mathf.Max(dt, 1e-5f));
            state.Position = smoothPos;                               // SnapPosPixelPerfect snaps the rendered copy below
        }

        SnapOrthoPixelPerfect();
        SnapPosPixelPerfect();
        ClampPos();
    }

    float CellsToOrtho(int cells) => cells * cellWorld * 0.5f / Mathf.Min(1f, Mathf.Max(cam.aspect, 0.0001f));
    float ClampOrtho(float ortho) => Mathf.Max(ortho, CellsToOrtho(vs.minCellsVisible));

    // Set the viewport zoom to a given cells-tall (called on region change + when the View tuner changes it).
    public void ApplyZoom(int cellsTall) => state.OrthoSize = ClampOrtho(CellsToOrtho(cellsTall));

    // X×Y viewport extent in cells (read-only), derived from the live ortho + aspect.
    public Vector2 ViewportCells => new(2f * state.OrthoSize * cam.aspect / cellWorld, 2f * state.OrthoSize / cellWorld);

    void SnapOrthoPixelPerfect()
    {
        float sh = Mathf.Max(1f, Screen.height);
        float minOrtho = CellsToOrtho(vs.minCellsVisible);
        float ppt = Mathf.Round(sh / (2f * state.OrthoSize * PixelsPerUnit));
        float pptHi = Mathf.Max(1f, Mathf.Floor(sh / (2f * PixelsPerUnit * minOrtho)));
        ppt = Mathf.Clamp(ppt, 1f, pptHi);
        state.OrthoSize = sh / (2f * PixelsPerUnit * ppt);
    }

    void SnapPosPixelPerfect()
    {
        float wpt = 1f / PixelsPerUnit;
        var p = state.Position;
        p.x = Mathf.Round(p.x / wpt) * wpt;
        p.y = Mathf.Round(p.y / wpt) * wpt;
        state.Position = p;
    }

    void ClampPos()
    {
        if (state.Bounds.width <= 0f || state.Bounds.height <= 0f) return;
        float h = state.OrthoSize, w = h * cam.aspect;
        var p = state.Position;
        float minX = state.Bounds.xMin + w, maxX = state.Bounds.xMax - w;
        float minY = state.Bounds.yMin + h, maxY = state.Bounds.yMax - h;
        p.x = minX > maxX ? state.Bounds.center.x : Mathf.Clamp(p.x, minX, maxX);
        p.y = minY > maxY ? state.Bounds.center.y : Mathf.Clamp(p.y, minY, maxY);
        state.Position = p;
    }
}
