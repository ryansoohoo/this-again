using UnityEngine;
using UnityEngine.InputSystem;

// Logic: LoL-style 2D camera control — wheel-zoom to cursor, drag/keyboard pan, spacebar recenter, bounds
// clamp, pixel-perfect snap. Reads input + CameraState + the camera's projection (aspect/ScreenToWorld), and
// writes the desired Position/OrthoSize into CameraState. Never writes the camera — CameraView does that.
public sealed class CameraSystem
{
    public struct Config
    {
        public float keyboardPanSpeed;
        public float recenterDuration;
    }

    readonly Camera cam;
    readonly Config cfg;
    readonly ViewSettings vs;
    readonly float cellWorld;
    readonly CameraState state;
    const float PixelsPerUnit = 16f;

    bool dragPanning;
    Vector3 dragAnchorWorld;
    bool recentering;
    Vector3 recenterFrom;
    float recenterT;
    int lastScreenW, lastScreenH;

    public CameraSystem(Camera cam, Config cfg, ViewSettings vs, float cellWorld, CameraState state)
    {
        this.cam = cam; this.cfg = cfg; this.vs = vs; this.cellWorld = cellWorld; this.state = state;
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;
        state.Position = cam.transform.position;                       // seed from the camera's boot position
        state.OrthoSize = ClampOrtho(CellsToOrtho(vs.overworldCellsTall));
    }

    public void Tick(float dt)
    {
        if (Screen.width != lastScreenW || Screen.height != lastScreenH)
        {
            lastScreenW = Screen.width; lastScreenH = Screen.height;
            state.OrthoSize = ClampOrtho(state.OrthoSize);
        }

        if (SpacePressed()) { recentering = true; recenterT = 0f; recenterFrom = state.Position; }

        if (recentering) TickRecenter(dt);
        else HandlePan(dt);

        SnapOrthoPixelPerfect();
        KeepInView();
        SnapPosPixelPerfect();
        ClampPos();
    }

    float CellsToOrtho(int cells) => cells * cellWorld * 0.5f / Mathf.Min(1f, Mathf.Max(cam.aspect, 0.0001f));
    float ClampOrtho(float ortho) => Mathf.Max(ortho, CellsToOrtho(vs.minCellsVisible));

    // Set the viewport zoom to a given cells-tall (called on region change + when the View tuner changes it).
    public void ApplyZoom(int cellsTall) => state.OrthoSize = ClampOrtho(CellsToOrtho(cellsTall));

    // X×Y view extents in cells (read-only), derived from the live ortho + aspect + inset.
    public Vector2 ViewportCells => new(2f * state.OrthoSize * cam.aspect / cellWorld, 2f * state.OrthoSize / cellWorld);
    public Vector2 MaxPanCells
    {
        get { var v = ViewportCells; return new Vector2(v.x * (2f - vs.followEdgeInset.x), v.y * (2f - vs.followEdgeInset.y)); }
    }

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

    void KeepInView()
    {
        if (!state.FollowTarget.HasValue) return;
        Vector2 t = state.FollowTarget.Value;
        float h = state.OrthoSize, w = h * cam.aspect;
        float ix = w * vs.followEdgeInset.x, iy = h * vs.followEdgeInset.y;
        var p = state.Position;
        p.x = Mathf.Clamp(p.x, t.x - (w - ix), t.x + (w - ix));
        p.y = Mathf.Clamp(p.y, t.y - (h - iy), t.y + (h - iy));
        state.Position = p;
    }

    void HandlePan(float dt)
    {
        Vector3 screen = PointerScreen();
        if (PanDown()) { dragPanning = true; dragAnchorWorld = cam.ScreenToWorldPoint(screen); }
        if (PanUp()) dragPanning = false;
        if (dragPanning) state.Position += dragAnchorWorld - cam.ScreenToWorldPoint(screen);

        Vector2 kb = KeyboardPan();
        if (kb.sqrMagnitude > 0f)
            state.Position += (Vector3)kb * (cfg.keyboardPanSpeed * (state.OrthoSize / 8f) * dt);
    }

    void TickRecenter(float dt)
    {
        if (PanDown() || KeyboardPan().sqrMagnitude > 0f) { recentering = false; return; }
        Vector3 to = state.FollowTarget.HasValue
            ? new Vector3(state.FollowTarget.Value.x, state.FollowTarget.Value.y, state.Position.z)
            : new Vector3(state.Bounds.center.x, state.Bounds.center.y, state.Position.z);
        if (cfg.recenterDuration <= 0f) { state.Position = to; recentering = false; return; }
        recenterT += dt / cfg.recenterDuration;
        if (recenterT >= 1f) { state.Position = to; recentering = false; return; }
        float t = 1f - Mathf.Pow(1f - recenterT, 3f);
        state.Position = Vector3.LerpUnclamped(recenterFrom, to, t);
    }

    Vector3 PointerScreen()
    {
        var m = Mouse.current;
        if (m == null) return Vector3.zero;
        var p = m.position.ReadValue();
        return new Vector3(p.x, p.y, 0f);
    }

    bool PanDown() { var m = Mouse.current; return m != null && m.rightButton.wasPressedThisFrame; }
    bool PanUp()   { var m = Mouse.current; return m != null && m.rightButton.wasReleasedThisFrame; }

    Vector2 KeyboardPan()
    {
        Vector2 v = Vector2.zero;
        if (InputState.Typing) return v;
        var kb = Keyboard.current;
        if (kb == null) return v;
        if (kb.leftArrowKey.isPressed) v.x -= 1f;
        if (kb.rightArrowKey.isPressed) v.x += 1f;
        if (kb.downArrowKey.isPressed) v.y -= 1f;
        if (kb.upArrowKey.isPressed) v.y += 1f;
        return v;
    }

    bool SpacePressed()
    {
        if (InputState.Typing) return false;
        var kb = Keyboard.current;
        return kb != null && kb.spaceKey.wasPressedThisFrame;
    }
}
