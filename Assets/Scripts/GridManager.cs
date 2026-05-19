using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Cell source: looks up whether a cell exists at (i, j). The renderer asks this for every
/// candidate index inside the camera viewport. Today the only implementation is
/// <see cref="LocalBoundedCellSource"/>, which says "every cell inside [0, gridSize) × [0, gridSize)
/// exists, and they all look the same." Tomorrow this is the seam where a streaming /
/// networked source plugs in — the renderer never has to change.
/// </summary>
public interface ICellSource
{
    bool TryGetCell(int i, int j, out CellData cell);
}

public readonly struct CellData
{
    // Placeholder for future per-cell payload (terrain type, sprite variant, owner, etc.).
    // Today every cell is identical so this is empty.
    public static readonly CellData Default = default;
}

/// <summary>
/// A bounded grid where every cell inside [0, gridSize) × [0, gridSize) exists.
/// Replace with a streaming source later to fetch cells from disk or network.
/// </summary>
public sealed class LocalBoundedCellSource : ICellSource
{
    public int GridSize { get; }
    public LocalBoundedCellSource(int gridSize) { GridSize = gridSize; }

    public bool TryGetCell(int i, int j, out CellData cell)
    {
        if (i < 0 || i >= GridSize || j < 0 || j >= GridSize) { cell = default; return false; }
        cell = CellData.Default;
        return true;
    }
}

/// <summary>
/// LoL-style 2D camera + bounded-grid renderer for 16×16 pixel cells.
///
/// Grid model:
///   - <see cref="gridSize"/> × <see cref="gridSize"/> cells (square).
///   - Cell (i, j) ∈ [0, gridSize) is centered at world ((i + 0.5 - gridSize/2) * cellWorld, …).
///     Effect: the whole grid is centered on world origin regardless of size.
///   - Only cells inside the camera viewport are materialised; the rest live in a pool.
///   - Cell existence is gated by <see cref="ICellSource"/> so a streaming source can later
///     swap in without touching the renderer/pool.
///
/// Camera:
///   - Mouse-wheel zoom anchored to the cursor.
///   - Zoom is clamped so the SHORTER screen dimension always shows between
///     <see cref="minCellsVisible"/> and <see cref="gridSize"/> cells. At max zoom-out the
///     whole grid fits in the shorter dim (longer dim shows empty space beyond the grid).
///   - Camera position is clamped so the viewport rect can never leave the grid bounds. As
///     you zoom in there is more pan room; at max zoom-out the camera is pinned to origin.
///   - Middle / right mouse drag pans. WASD / arrow keys pan (speed scales with zoom).
///   - Spacebar tweens the camera back to origin (ease-out cubic). Manual input cancels.
///
/// Auto-bootstraps on Play.
/// </summary>
[DefaultExecutionOrder(-100)]
public class GridManager : MonoBehaviour
{
    [Header("Grid")]
    [Tooltip("World units per cell = cellSizePixels / pixelsPerUnit. Default 16/16 = 1 unit per cell.")]
    [SerializeField] int pixelsPerUnit = 16;
    [SerializeField] int cellSizePixels = 16;
    [Tooltip("Grid is gridSize × gridSize cells, centered on world origin.")]
    [SerializeField] int gridSize = 30;
    [Tooltip("Extra rings of cells to keep loaded just outside the viewport (smooths pan).")]
    [SerializeField] int viewPadding = 1;
    [Tooltip("Hard cap on simultaneously active cells. If a frame would exceed this, the spawn step skips.")]
    [SerializeField] int maxActiveCells = 10000;

    [Header("Zoom")]
    [Tooltip("Most zoomed in: at least this many cells visible in the SHORTER screen dimension.")]
    [SerializeField] int minCellsVisible = 10;
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
    [Tooltip("Seconds for the recenter tween. Set <= 0 for instant snap.")]
    [SerializeField] float recenterDuration = 0.25f;

    Camera cam;
    Sprite cellSprite;
    Transform cellsRoot;
    ICellSource source;

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
            cam.transform.position = new Vector3(0f, 0f, -10f);
        else
            cam.transform.position = new Vector3(0f, 0f, cam.transform.position.z);

        source = new LocalBoundedCellSource(gridSize);
        cellSprite = BuildCellSprite();
        cellsRoot = new GameObject("Cells").transform;
        cellsRoot.SetParent(transform, false);

        lastScreenW = Screen.width;
        lastScreenH = Screen.height;
        cam.orthographicSize = ClampOrthoToCellBounds(CellsToOrthoSize(startCellsVisible));
        ClampCameraToGrid();
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
        // Window resize → aspect changed → re-clamp zoom + pan.
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

        ClampCameraToGrid();
        UpdateVisibleCells();
    }

    // ---- Grid geometry ----

    float CellWorld => (float)cellSizePixels / pixelsPerUnit;
    float GridHalfWorld => gridSize * CellWorld * 0.5f; // grid spans [-half, +half] in both axes

    /// <summary>World-space center of cell (i, j) where (i, j) ∈ [0, gridSize).</summary>
    Vector3 CellCenterWorld(int i, int j)
    {
        float w = CellWorld;
        return new Vector3((i + 0.5f) * w - GridHalfWorld, (j + 0.5f) * w - GridHalfWorld, 0f);
    }

    // ---- Zoom math ----

    /// <summary>OrthoSize that fits `cells` cells across the SHORTER screen dimension.</summary>
    float CellsToOrthoSize(int cells)
    {
        float aspect = Mathf.Max(cam.aspect, 0.0001f);
        // Shorter side in world units = 2 * orthoSize * min(1, aspect)
        return cells * CellWorld * 0.5f / Mathf.Min(1f, aspect);
    }

    float ClampOrthoToCellBounds(float ortho)
    {
        float aspect = Mathf.Max(cam.aspect, 0.0001f);
        float aspectAdjust = 1f / Mathf.Min(1f, aspect); // max(1, 1/aspect)
        float orthoMin = minCellsVisible * CellWorld * 0.5f * aspectAdjust;
        float orthoMax = gridSize * CellWorld * 0.5f * aspectAdjust;
        if (orthoMin > orthoMax) orthoMax = orthoMin;
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

    void ClampCameraToGrid()
    {
        float gh = GridHalfWorld;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        float minX = -gh + halfW;
        float maxX = gh - halfW;
        if (minX > maxX) { minX = maxX = 0f; } // viewport wider than grid → pin to center

        float minY = -gh + halfH;
        float maxY = gh - halfH;
        if (minY > maxY) { minY = maxY = 0f; }

        var p = cam.transform.position;
        cam.transform.position = new Vector3(Mathf.Clamp(p.x, minX, maxX), Mathf.Clamp(p.y, minY, maxY), p.z);
    }

    // ---- Recenter ----

    void BeginRecenter()
    {
        recentering = true;
        recenterT = 0f;
        recenterFrom = cam.transform.position;
        recenterTo = new Vector3(0f, 0f, cam.transform.position.z);
    }

    void TickRecenter()
    {
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
        float gh = GridHalfWorld;
        Vector3 camPos = cam.transform.position;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        // Convert camera-edge world coords -> cell indices.
        // Cell i has its left edge at (i * cw - gh). So world x = (i * cw - gh) => i = (x + gh) / cw.
        int iMin = Mathf.FloorToInt((camPos.x - halfW + gh) / cw) - viewPadding;
        int iMax = Mathf.CeilToInt((camPos.x + halfW + gh) / cw) + viewPadding;
        int jMin = Mathf.FloorToInt((camPos.y - halfH + gh) / cw) - viewPadding;
        int jMax = Mathf.CeilToInt((camPos.y + halfH + gh) / cw) + viewPadding;

        // Clip to grid bounds — outside-grid coords are not spawned even if the viewport
        // extends past the grid (e.g., at max zoom out with a wide aspect ratio).
        iMin = Mathf.Max(iMin, 0);
        iMax = Mathf.Min(iMax, gridSize - 1);
        jMin = Mathf.Max(jMin, 0);
        jMax = Mathf.Min(jMax, gridSize - 1);

        int needed = Mathf.Max(0, (iMax - iMin + 1) * (jMax - jMin + 1));
        if (needed > maxActiveCells) return;

        // Recycle cells now outside the visible window.
        stale.Clear();
        foreach (var kv in active)
        {
            var c = kv.Key;
            if (c.x < iMin || c.x > iMax || c.y < jMin || c.y > jMax) stale.Add(c);
        }
        for (int i = 0; i < stale.Count; i++)
        {
            var go = active[stale[i]];
            go.SetActive(false);
            pool.Enqueue(go);
            active.Remove(stale[i]);
        }

        // Spawn missing cells, gated by ICellSource so streaming sources can later say "no".
        for (int i = iMin; i <= iMax; i++)
        {
            for (int j = jMin; j <= jMax; j++)
            {
                var coord = new Vector2Int(i, j);
                if (active.ContainsKey(coord)) continue;
                if (!source.TryGetCell(i, j, out _)) continue;

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
                go.transform.localPosition = CellCenterWorld(i, j);
                active[coord] = go;
            }
        }
    }
}
