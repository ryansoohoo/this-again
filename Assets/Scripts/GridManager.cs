using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Builds an infinite grid of 16x16 pixel cells. Each cell is a black square with a single
/// white pixel at the center. Only cells inside the camera viewport are materialised — the
/// rest live in an inactive pool. Mouse-wheel zooms (anchored to cursor), middle-or-right-drag
/// pans, WASD/arrows also pan.
///
/// Auto-bootstraps on Play: no scene setup required. If a GridManager already exists in the
/// loaded scene, the auto-boot is skipped so designer-placed settings win.
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
    [Tooltip("Hard cap to avoid runaway spawning when the user zooms way out.")]
    [SerializeField] int maxActiveCells = 10000;

    [Header("Zoom")]
    [SerializeField] float minOrthoSize = 1f;
    [SerializeField] float maxOrthoSize = 128f;
    [Tooltip("Multiplicative step per wheel notch (1.2 = 20% per notch).")]
    [SerializeField] float zoomStep = 1.2f;
    [SerializeField] float startOrthoSize = 8f;
    [Tooltip("Keep the world-space point under the cursor fixed while zooming.")]
    [SerializeField] bool zoomToCursor = true;

    [Header("Pan")]
    [SerializeField] bool enablePan = true;
    [Tooltip("Units/sec at orthoSize=8; scales with zoom so it feels consistent.")]
    [SerializeField] float keyboardPanSpeed = 20f;

    Camera cam;
    Sprite cellSprite;
    Transform cellsRoot;

    readonly Dictionary<Vector2Int, GameObject> active = new();
    readonly Queue<GameObject> pool = new();
    readonly List<Vector2Int> stale = new();

    bool dragPanning;
    Vector3 dragPanAnchorWorld;

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
        cam.orthographicSize = startOrthoSize;
        cam.backgroundColor = Color.black;
        cam.clearFlags = CameraClearFlags.SolidColor;
        if (cam.transform.position.z >= 0f)
            cam.transform.position = new Vector3(cam.transform.position.x, cam.transform.position.y, -10f);

        cellSprite = BuildCellSprite();

        cellsRoot = new GameObject("Cells").transform;
        cellsRoot.SetParent(transform, false);
    }

    /// <summary>
    /// Procedurally builds a cellSizePixels × cellSizePixels sprite: black background with a single
    /// white pixel at the geometric center. Pivot is centered, so a cell placed at (x,y) is centered
    /// on (x,y) in world space.
    /// </summary>
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
        tex.Apply(false, true); // makeNoLongerReadable=true; we don't need to read it back

        var rect = new Rect(0, 0, cellSizePixels, cellSizePixels);
        return Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), pixelsPerUnit, 0, SpriteMeshType.FullRect);
    }

    void Update()
    {
        HandleZoom();
        if (enablePan) HandlePan();
        UpdateVisibleCells();
    }

    // ---- Input ----

    void HandleZoom()
    {
        float scroll = ReadScroll();
        if (scroll == 0f) return;

        float factor = scroll > 0f ? 1f / zoomStep : zoomStep;
        float newSize = Mathf.Clamp(cam.orthographicSize * factor, minOrthoSize, maxOrthoSize);
        if (Mathf.Approximately(newSize, cam.orthographicSize)) return;

        if (zoomToCursor)
        {
            Vector3 screen = GetPointerScreenPos();
            Vector3 before = cam.ScreenToWorldPoint(screen);
            cam.orthographicSize = newSize;
            Vector3 after = cam.ScreenToWorldPoint(screen);
            cam.transform.position += (before - after);
        }
        else
        {
            cam.orthographicSize = newSize;
        }
    }

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
            Vector3 delta = dragPanAnchorWorld - currentWorld;
            cam.transform.position += delta;
            // anchor stays the same in world space — the camera moves under it
        }

        Vector2 kb = ReadKeyboardPan();
        if (kb.sqrMagnitude > 0f)
        {
            float speed = keyboardPanSpeed * (cam.orthographicSize / 8f);
            cam.transform.position += new Vector3(kb.x, kb.y, 0f) * (speed * Time.unscaledDeltaTime);
        }
    }

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

    // ---- Culling / spawning ----

    void UpdateVisibleCells()
    {
        float cellWorld = (float)cellSizePixels / pixelsPerUnit;
        Vector3 camPos = cam.transform.position;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        // Cell (x,y) is centered at (x*cellWorld, y*cellWorld). A cell is visible if its
        // bounds intersect the camera rect. Inflate with viewPadding for safety / smooth pan.
        int xMin = Mathf.FloorToInt((camPos.x - halfW) / cellWorld) - viewPadding;
        int xMax = Mathf.CeilToInt((camPos.x + halfW) / cellWorld) + viewPadding;
        int yMin = Mathf.FloorToInt((camPos.y - halfH) / cellWorld) - viewPadding;
        int yMax = Mathf.CeilToInt((camPos.y + halfH) / cellWorld) + viewPadding;

        int needed = (xMax - xMin + 1) * (yMax - yMin + 1);
        if (needed > maxActiveCells) return; // refuse to spawn beyond budget — last frame stays

        // Recycle now-offscreen cells
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

        // Spawn missing cells
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
                go.transform.localPosition = new Vector3(x * cellWorld, y * cellWorld, 0f);
                active[coord] = go;
            }
        }
    }
}
