using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Infinite grid of 16x16 pixel cells (black square with a single white pixel in the center).
/// Only cells inside the camera viewport are materialised — the rest sit in an inactive pool.
///
/// Camera behaviour (LoL-style):
///   - Mouse wheel zooms, anchored to the cursor.
///   - Zoom is clamped by **cell count**: at minimum zoom, at least `minCellsVisible` cells fit
///     in the SHORTER screen dimension; at maximum zoom, no more than `maxCellsVisible` cells fit
///     in the LONGER screen dimension. The clamp recomputes on window resize.
///   - Middle / right mouse drag pans. WASD / arrow keys pan (speed scales with zoom).
///   - Spacebar tweens the camera back to `recenterTarget` (default world origin).
///
/// Auto-bootstraps on Play: if no GridManager is in the scene, one is spawned.
/// </summary>
[DefaultExecutionOrder(-100)]
public class GridManager : MonoBehaviour
{
    [Header("Grid")]
    [Tooltip("World units per cell = cellSizePixels / pixelsPerUnit. Default 16/16 = 1 unit per cell.")]
    [SerializeField] int pixelsPerUnit = 16;
    [SerializeField] int cellSizePixels = 16;
    [Tooltip("Extra rings of cells to keep loaded outside the viewport (hides pop-in during pan).")]
    [SerializeField] int viewPadding = 1;
    [Tooltip("Hard cap on simultaneously active cells. If a frame would exceed this, the spawn step skips.")]
    [SerializeField] int maxActiveCells = 10000;

    [Header("Zoom (cell-count clamps)")]
    [Tooltip("Most zoomed in: at least this many cells visible in the SHORTER screen dimension.")]
    [SerializeField] int minCellsVisible = 10;
    [Tooltip("Most zoomed out: at most this many cells visible in the LONGER screen dimension.")]
    [SerializeField] int maxCellsVisible = 30;
    [Tooltip("Initial cells visible in the shorter dimension when the game starts.")]
    [SerializeField] int startCellsVisible = 16;
    [Tooltip("Multiplicative step per wheel notch (1.2 = 20% per notch).")]
    [SerializeField] float zoomStep = 1.2f;
    [Tooltip("Keep the world point under the cursor fixed while zooming.")]
    [SerializeField] bool zoomToCursor = true;

    [Header("Pan")]
    [SerializeField] bool enablePan = true;
    [Tooltip("Units/sec at orthoSize=8; scales with zoom so it feels consistent.")]
    [SerializeField] float keyboardPanSpeed = 20f;

    [Header("Recenter (Spacebar)")]
    [Tooltip("World point Spacebar tweens the camera back to.")]
    [SerializeField] Vector2 recenterTarget = Vector2.zero;
    [Tooltip("Seconds for the recenter tween. Set <= 0 for instant snap.")]
    [SerializeField] float recenterDuration = 0.25f;

    Camera cam;
    Sprite cellSprite;
    Transform cellsRoot;

    readonly Dictionary<Vector2Int, GameObject> active = new();
    readonly Queue<GameObject> pool = new();
    readonly List<Vector2Int> stale = new();

    bool dragPanning;
    Vector3 dragPanAnchorWorld;

    bool recentering;
    Vector3 recenterFrom;
    Vector3 recenterTo;
    float recenterT;

    int lastScreenW, lastScreenH;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBoot()
    {
        if (FindFirstObjectByType<GridManager>() != null) return;
        var go = new GameObject("GridManager");
        go.AddComponent<GridManager>();
    }

    void Awake()
    {
        cam = Camera.main;
        if (cam == null)
        {
            Debug.LogError("[GridManager] No Main Camera in scene. Disabling.");
            enabled = false;
            return;
        }

        cam.orthographic = true;
        cam.backgroundColor = Color.black;
        cam.clearFlags = CameraClearFlags.SolidColor;
        if (cam.transform.position.z >= 0f)
            cam.transform.position = new Vector3(cam.transform.position.x, cam.transform.position.y, -10f);

        cellSprite = BuildCellSprite();
        cellsRoot = new GameObject("Cells").transform;
        cellsRoot.SetParent(transform, false);

        // Snapshot screen size, set initial zoom based on shorter-dimension cell count.
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;
        cam.orthographicSize = CellsToOrthoSizeShorter(startCellsVisible);
        cam.orthographicSize = ClampOrthoToCellBounds(cam.orthographicSize);
    }

    Sprite BuildCellSprite()
    {
        var tex = new Texture2D(cellSizePixels, cellSizePixels, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            anisoLevel = 0,
            name = "CellTex"
        };
        var pixels = new Color32[cellSizePixels * cellSizePixels];
        var black = new Color32(0, 0, 0, 255);
        for (int i = 0; i < pixels.Length; i++) pixels[i] = black;
        int cx = cellSizePixels / 2;
        int cy = cellSizePixels / 2;
        pixels[cy * cellSizePixels + cx] = new Color32(255, 255, 255, 255);
        tex.SetPixels32(pixels);
        tex.Apply(false, true);

        var rect = new Rect(0, 0, cellSizePixels, cellSizePixels);
        return Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), pixelsPerUnit, 0, SpriteMeshType.FullRect);
    }

    void Update()
    {
        // If the window resized, the aspect changed, so re-clamp ortho.
        if (Screen.width != lastScreenW || Screen.height != lastScreenH)
        {
            lastScreenW = Screen.width;
            lastScreenH = Screen.height;
            cam.orthographicSize = ClampOrthoToCellBounds(cam.orthographicSize);
        }

        if (RecenterPressedThisFrame()) BeginRecenter();

        if (recentering) TickRecenter();
        else
        {
            HandleZoom();
            if (enablePan) HandlePan();
        }

        UpdateVisibleCells();
    }

    // ---- Zoom / cell-count math ----

    float CellWorld => (float)cellSizePixels / pixelsPerUnit;

    /// <summary>OrthoSize that puts `cells` cells across the SHORTER screen dimension.</summary>
    float CellsToOrthoSizeShorter(int cells)
    {
        float aspect = Mathf.Max(cam.aspect, 0.0001f);
        // Shorter dim = vertical when aspect >= 1, horizontal when aspect < 1.
        // Height count = 2*ortho/cellWorld. Want shorter == cells.
        // If aspect >= 1: shorter is height, ortho = cells * cellWorld / 2.
        // If aspect <  1: shorter is width  = height*aspect, so ortho = cells * cellWorld / (2*aspect).
        float shorterDivisor = Mathf.Min(1f, aspect); // height-equivalent of shorter dimension
        return cells * CellWorld * 0.5f / shorterDivisor;
    }

    float ClampOrthoToCellBounds(float ortho)
    {
        float aspect = Mathf.Max(cam.aspect, 0.0001f);
        // Min ortho: shorter dim must show >= minCells.
        float orthoMin = minCellsVisible * CellWorld * 0.5f * Mathf.Max(1f, 1f / aspect);
        // Max ortho: longer dim must show <= maxCells.
        float orthoMax = maxCellsVisible * CellWorld * 0.5f / Mathf.Max(1f, aspect);
        if (orthoMin > orthoMax) orthoMax = orthoMin; // pathological: ranges collide → pin to min
        return Mathf.Clamp(ortho, orthoMin, orthoMax);
    }

    void HandleZoom()
    {
        float scroll = ReadScroll();
        if (scroll == 0f) return;

        float factor = scroll > 0f ? 1f / zoomStep : zoomStep;
        float target = ClampOrthoToCellBounds(cam.orthographicSize * factor);
        if (Mathf.Approximately(target, cam.orthographicSize)) return;

        if (zoomToCursor)
        {
            Vector3 screen = GetPointerScreenPos();
            Vector3 anchor = IsInsideViewport(screen) ? screen : ViewportCenterScreenPos();
            Vector3 before = cam.ScreenToWorldPoint(anchor);
            cam.orthographicSize = target;
            Vector3 after = cam.ScreenToWorldPoint(anchor);
            cam.transform.position += (before - after);
        }
        else
        {
            cam.orthographicSize = target;
        }
    }

    bool IsInsideViewport(Vector3 screen)
    {
        var r = cam.pixelRect;
        return screen.x >= r.x && screen.x <= r.xMax && screen.y >= r.y && screen.y <= r.yMax;
    }

    Vector3 ViewportCenterScreenPos()
    {
        var r = cam.pixelRect;
        return new Vector3(r.center.x, r.center.y, 0f);
    }

    // ---- Pan ----

    void HandlePan()
    {
        Vector3 screen = GetPointerScreenPos();

        if (GetPanButtonDown())
        {
            dragPanning = true;
            dragPanAnchorWorld = cam.ScreenToWorldPoint(screen);
        }
        if (GetPanButtonUp()) dragPanning = false;

        if (dragPanning)
        {
            Vector3 currentWorld = cam.ScreenToWorldPoint(screen);
            cam.transform.position += (dragPanAnchorWorld - currentWorld);
        }

        Vector2 kb = ReadKeyboardPan();
        if (kb.sqrMagnitude > 0f)
        {
            float speed = keyboardPanSpeed * (cam.orthographicSize / 8f);
            cam.transform.position += new Vector3(kb.x, kb.y, 0f) * (speed * Time.unscaledDeltaTime);
        }
    }

    // ---- Recenter ----

    void BeginRecenter()
    {
        recentering = true;
        recenterT = 0f;
        recenterFrom = cam.transform.position;
        recenterTo = new Vector3(recenterTarget.x, recenterTarget.y, cam.transform.position.z);
    }

    void TickRecenter()
    {
        // If the user starts panning manually, abandon the tween.
        if (GetPanButtonDown() || ReadKeyboardPan().sqrMagnitude > 0f || ReadScroll() != 0f)
        {
            recentering = false;
            return;
        }

        if (recenterDuration <= 0f)
        {
            cam.transform.position = recenterTo;
            recentering = false;
            return;
        }

        recenterT += Time.unscaledDeltaTime / recenterDuration;
        if (recenterT >= 1f)
        {
            cam.transform.position = recenterTo;
            recentering = false;
        }
        else
        {
            float t = 1f - Mathf.Pow(1f - recenterT, 3f); // ease-out cubic
            cam.transform.position = Vector3.LerpUnclamped(recenterFrom, recenterTo, t);
        }
    }

    // ---- Input shims (new Input System + legacy fallback) ----

    Vector3 GetPointerScreenPos()
    {
#if ENABLE_INPUT_SYSTEM
        var m = Mouse.current;
        if (m != null)
        {
            var p = m.position.ReadValue();
            return new Vector3(p.x, p.y, 0f);
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.mousePosition;
#else
        return Vector3.zero;
#endif
    }

    bool GetPanButtonDown()
    {
#if ENABLE_INPUT_SYSTEM
        var m = Mouse.current;
        if (m != null) return m.middleButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetMouseButtonDown(2) || Input.GetMouseButtonDown(1);
#else
        return false;
#endif
    }

    bool GetPanButtonUp()
    {
#if ENABLE_INPUT_SYSTEM
        var m = Mouse.current;
        if (m != null) return m.middleButton.wasReleasedThisFrame || m.rightButton.wasReleasedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetMouseButtonUp(2) || Input.GetMouseButtonUp(1);
#else
        return false;
#endif
    }

    Vector2 ReadKeyboardPan()
    {
        Vector2 v = Vector2.zero;
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v.x -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v.x += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v.y -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v.y += 1f;
            return v;
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) v.x -= 1f;
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) v.x += 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v.y -= 1f;
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v.y += 1f;
#endif
        return v;
    }

    float ReadScroll()
    {
#if ENABLE_INPUT_SYSTEM
        var m = Mouse.current;
        if (m != null) return m.scroll.ReadValue().y / 120f;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.mouseScrollDelta.y;
#else
        return 0f;
#endif
    }

    bool RecenterPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null) return kb.spaceKey.wasPressedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.Space);
#else
        return false;
#endif
    }

    // ---- Culling / spawning ----

    void UpdateVisibleCells()
    {
        float cw = CellWorld;
        Vector3 camPos = cam.transform.position;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        int xMin = Mathf.FloorToInt((camPos.x - halfW) / cw) - viewPadding;
        int xMax = Mathf.CeilToInt((camPos.x + halfW) / cw) + viewPadding;
        int yMin = Mathf.FloorToInt((camPos.y - halfH) / cw) - viewPadding;
        int yMax = Mathf.CeilToInt((camPos.y + halfH) / cw) + viewPadding;

        int needed = (xMax - xMin + 1) * (yMax - yMin + 1);
        if (needed > maxActiveCells) return;

        stale.Clear();
        foreach (var kv in active)
        {
            var c = kv.Key;
            if (c.x < xMin || c.x > xMax || c.y < yMin || c.y > yMax) stale.Add(c);
        }
        for (int i = 0; i < stale.Count; i++)
        {
            var go = active[stale[i]];
            go.SetActive(false);
            pool.Enqueue(go);
            active.Remove(stale[i]);
        }

        for (int x = xMin; x <= xMax; x++)
        {
            for (int y = yMin; y <= yMax; y++)
            {
                var coord = new Vector2Int(x, y);
                if (active.ContainsKey(coord)) continue;

                GameObject go;
                if (pool.Count > 0)
                {
                    go = pool.Dequeue();
                    go.SetActive(true);
                }
                else
                {
                    go = new GameObject("Cell");
                    go.transform.SetParent(cellsRoot, false);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = cellSprite;
                }
                go.transform.localPosition = new Vector3(x * cw, y * cw, 0f);
                active[coord] = go;
            }
        }
    }
}
