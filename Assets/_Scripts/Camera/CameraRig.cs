using UnityEngine;
using UnityEngine.InputSystem;

// LoL-style 2D camera control: wheel-zoom anchored to cursor, drag/keyboard pan, spacebar recenter.
// Clamped so the viewport never leaves a world rect of [-halfExtent, +halfExtent] on either axis.
// Plain C# (not a MonoBehaviour). Driven from the orchestrator's Update via Tick(dt).
public sealed class CameraRig
{
    public struct Config
    {
        public int minCellsVisible;
        public int startCellsVisible;
        public float keyboardPanSpeed;
        public float recenterDuration;
    }

    readonly Camera cam;
    readonly Config cfg;
    readonly float cellWorld;         // world units per cell, for CellsToOrtho
    const float PixelsPerUnit = 16f;  // art texels per world unit (16px floor tiles per 1-unit cell; 16-PPU character at scale 1)

    // World rect the camera viewport must stay inside (the loaded vision window). Set by WorldView.
    public Rect Bounds { private get; set; }

    // The local player's world position; the camera follows it so the player can't leave the viewport
    // (which also keeps the minimap's viewport box centered). Null when there's no local player. Set by Game.
    public Vector2? FollowTarget { private get; set; }
    const float FollowEdgeInset = 0.15f;   // keep the player at least this fraction of the viewport in from the edge

    bool dragPanning;
    Vector3 dragAnchorWorld;
    bool recentering;
    Vector3 recenterFrom;
    float recenterT;
    int lastScreenW, lastScreenH;

    public CameraRig(Camera cam, Config cfg, float cellWorld)
    {
        this.cam = cam; this.cfg = cfg; this.cellWorld = cellWorld;
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;
        cam.orthographicSize = ClampOrtho(CellsToOrtho(cfg.startCellsVisible));
    }

    public void Tick(float dt)
    {
        if (Screen.width != lastScreenW || Screen.height != lastScreenH)
        {
            lastScreenW = Screen.width; lastScreenH = Screen.height;
            cam.orthographicSize = ClampOrtho(cam.orthographicSize);
        }

        if (SpacePressed()) { recentering = true; recenterT = 0f; recenterFrom = cam.transform.position; }

        if (recentering) TickRecenter(dt);
        else HandlePan(dt);

        SnapOrthoPixelPerfect();
        KeepInView();               // follow the player so they can't scroll off-screen
        SnapPosPixelPerfect();
        ClampPos();                 // last, so the viewport never shows unloaded (black) area
    }

    // ---- Orthographic size ----

    float CellsToOrtho(int cells) => cells * cellWorld * 0.5f / Mathf.Min(1f, Mathf.Max(cam.aspect, 0.0001f));

    float ClampOrtho(float ortho) => Mathf.Max(ortho, CellsToOrtho(cfg.minCellsVisible));   // infinite world: only a min-zoom floor

    // ---- Pixel-perfect snapping ----
    // Keep each art texel an integer number of screen pixels (crisp, no shimmer) and keep the camera
    // on the texel grid so the world aligns to whole screen pixels. Reference density is 16 texels/unit
    // (16px tiles across 1-unit cells; the 16-PPU character at scale 1 matches). Stays inside the
    // zoom range so the world never shows black past its edge.
    void SnapOrthoPixelPerfect()
    {
        float sh = Mathf.Max(1f, Screen.height);
        float minOrtho = CellsToOrtho(cfg.minCellsVisible);
        float ppt = Mathf.Round(sh / (2f * cam.orthographicSize * PixelsPerUnit));   // screen pixels per texel
        float pptHi = Mathf.Max(1f, Mathf.Floor(sh / (2f * PixelsPerUnit * minOrtho)));   // smallest ortho (most zoomed-in)
        ppt = Mathf.Clamp(ppt, 1f, pptHi);
        cam.orthographicSize = sh / (2f * PixelsPerUnit * ppt);
    }

    void SnapPosPixelPerfect()
    {
        float wpt = 1f / PixelsPerUnit;                                              // world units per texel
        var p = cam.transform.position;
        p.x = Mathf.Round(p.x / wpt) * wpt;
        p.y = Mathf.Round(p.y / wpt) * wpt;
        cam.transform.position = p;
    }

    // Keep the viewport inside the loaded vision window so no unloaded (black) area shows. If the
    // viewport is larger than the window on an axis, pin to its center.
    void ClampPos()
    {
        if (Bounds.width <= 0f || Bounds.height <= 0f) return;
        float h = cam.orthographicSize, w = h * cam.aspect;
        var p = cam.transform.position;
        float minX = Bounds.xMin + w, maxX = Bounds.xMax - w;
        float minY = Bounds.yMin + h, maxY = Bounds.yMax - h;
        p.x = minX > maxX ? Bounds.center.x : Mathf.Clamp(p.x, minX, maxX);
        p.y = minY > maxY ? Bounds.center.y : Mathf.Clamp(p.y, minY, maxY);
        cam.transform.position = p;
    }

    // Moves the camera the minimum needed to keep the follow target (player) inside the viewport, so
    // the player never scrolls off-screen. Manual pan still works up to that limit.
    void KeepInView()
    {
        if (!FollowTarget.HasValue) return;
        Vector2 t = FollowTarget.Value;
        float h = cam.orthographicSize, w = h * cam.aspect;
        float ix = w * FollowEdgeInset, iy = h * FollowEdgeInset;
        var p = cam.transform.position;
        p.x = Mathf.Clamp(p.x, t.x - (w - ix), t.x + (w - ix));
        p.y = Mathf.Clamp(p.y, t.y - (h - iy), t.y + (h - iy));
        cam.transform.position = p;
    }

    // ---- Pan ----

    void HandlePan(float dt)
    {
        Vector3 screen = PointerScreen();
        if (PanDown()) { dragPanning = true; dragAnchorWorld = cam.ScreenToWorldPoint(screen); }
        if (PanUp()) dragPanning = false;
        if (dragPanning) cam.transform.position += dragAnchorWorld - cam.ScreenToWorldPoint(screen);

        Vector2 kb = KeyboardPan();
        if (kb.sqrMagnitude > 0f)
            cam.transform.position += (Vector3)kb * (cfg.keyboardPanSpeed * (cam.orthographicSize / 8f) * dt);
    }

    // ---- Recenter ----

    void TickRecenter(float dt)
    {
        if (PanDown() || KeyboardPan().sqrMagnitude > 0f) { recentering = false; return; }
        Vector3 to = FollowTarget.HasValue
            ? new Vector3(FollowTarget.Value.x, FollowTarget.Value.y, cam.transform.position.z)
            : new Vector3(Bounds.center.x, Bounds.center.y, cam.transform.position.z);
        if (cfg.recenterDuration <= 0f) { cam.transform.position = to; recentering = false; return; }
        recenterT += dt / cfg.recenterDuration;
        if (recenterT >= 1f) { cam.transform.position = to; recentering = false; return; }
        float t = 1f - Mathf.Pow(1f - recenterT, 3f);                               // ease-out cubic
        cam.transform.position = Vector3.LerpUnclamped(recenterFrom, to, t);
    }

    // ---- Input (new Input System; null-guarded for when no device is present) ----

    Vector3 PointerScreen()
    {
        var m = Mouse.current;
        if (m == null) return Vector3.zero;
        var p = m.position.ReadValue();
        return new Vector3(p.x, p.y, 0f);
    }

    // Right-click drag pans the camera (player auto-move is double-left-click; see PlayerController).
    bool PanDown()
    {
        var m = Mouse.current;
        return m != null && m.rightButton.wasPressedThisFrame;
    }

    bool PanUp()
    {
        var m = Mouse.current;
        return m != null && m.rightButton.wasReleasedThisFrame;
    }

    Vector2 KeyboardPan()
    {
        // Arrow keys only: WASD now drives the player (see PlayerController).
        Vector2 v = Vector2.zero;
        if (InputState.Typing) return v;          // command line open: arrows edit text, not pan
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
        if (InputState.Typing) return false;      // command line open: space types a space
        var kb = Keyboard.current;
        return kb != null && kb.spaceKey.wasPressedThisFrame;
    }
}
