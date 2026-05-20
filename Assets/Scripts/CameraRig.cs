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
        public float zoomStep;
        public float keyboardPanSpeed;
        public float recenterDuration;
    }

    readonly Camera cam;
    readonly Config cfg;
    readonly float halfExtent;        // world half-extent on either axis (assumes square world)
    readonly float cellWorld;         // world units per cell, for CellsToOrtho

    bool dragPanning;
    Vector3 dragAnchorWorld;
    bool recentering;
    Vector3 recenterFrom;
    float recenterT;
    int lastScreenW, lastScreenH;

    public CameraRig(Camera cam, Config cfg, float halfExtent, float cellWorld)
    {
        this.cam = cam; this.cfg = cfg; this.halfExtent = halfExtent; this.cellWorld = cellWorld;
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;
        cam.orthographicSize = ClampOrtho(CellsToOrtho(cfg.startCellsVisible));
        ClampPos();
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
        else { HandleZoom(); HandlePan(dt); }

        ClampPos();
    }

    // ---- Zoom ----

    float CellsToOrtho(int cells) => cells * cellWorld * 0.5f / Mathf.Min(1f, Mathf.Max(cam.aspect, 0.0001f));

    float ClampOrtho(float ortho)
    {
        float a = Mathf.Max(cam.aspect, 0.0001f);
        float min = CellsToOrtho(cfg.minCellsVisible);
        float max = halfExtent / Mathf.Max(1f, a);                                  // longest viewport dim <= world: viewport stays inside the square map (no black past edge)
        if (min > max) min = max;                                                   // tiny world: ceiling wins
        return Mathf.Clamp(ortho, min, max);
    }

    void HandleZoom()
    {
        float scroll = ReadScroll();
        if (scroll == 0f) return;
        float target = ClampOrtho(cam.orthographicSize * (scroll > 0f ? 1f / cfg.zoomStep : cfg.zoomStep));
        if (Mathf.Approximately(target, cam.orthographicSize)) return;

        Vector3 screen = PointerScreen();
        var r = cam.pixelRect;
        bool inside = screen.x >= r.x && screen.x <= r.xMax && screen.y >= r.y && screen.y <= r.yMax;
        Vector3 anchor = inside ? screen : new Vector3(r.center.x, r.center.y, 0f);
        Vector3 before = cam.ScreenToWorldPoint(anchor);
        cam.orthographicSize = target;
        cam.transform.position += before - cam.ScreenToWorldPoint(anchor);
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

    void ClampPos()
    {
        float gh = halfExtent, h = cam.orthographicSize, w = h * cam.aspect;
        float minX = -gh + w, maxX = gh - w; if (minX > maxX) minX = maxX = 0f;     // viewport wider than world -> pin
        float minY = -gh + h, maxY = gh - h; if (minY > maxY) minY = maxY = 0f;
        var p = cam.transform.position;
        cam.transform.position = new Vector3(Mathf.Clamp(p.x, minX, maxX), Mathf.Clamp(p.y, minY, maxY), p.z);
    }

    // ---- Recenter ----

    void TickRecenter(float dt)
    {
        if (PanDown() || KeyboardPan().sqrMagnitude > 0f || ReadScroll() != 0f) { recentering = false; return; }
        Vector3 to = new(0f, 0f, cam.transform.position.z);
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

    bool PanDown()
    {
        var m = Mouse.current;
        return m != null && (m.middleButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame);
    }

    bool PanUp()
    {
        var m = Mouse.current;
        return m != null && (m.middleButton.wasReleasedThisFrame || m.rightButton.wasReleasedThisFrame);
    }

    Vector2 KeyboardPan()
    {
        Vector2 v = Vector2.zero;
        var kb = Keyboard.current;
        if (kb == null) return v;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v.x -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v.x += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v.y -= 1f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v.y += 1f;
        return v;
    }

    float ReadScroll()
    {
        var m = Mouse.current;
        return m != null ? m.scroll.ReadValue().y / 120f : 0f;
    }

    bool SpacePressed()
    {
        var kb = Keyboard.current;
        return kb != null && kb.spaceKey.wasPressedThisFrame;
    }
}
