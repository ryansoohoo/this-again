using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class GridManager : MonoBehaviour
{
    [Header("Grid")]
    [SerializeField] int pixelsPerUnit = 16;
    [SerializeField] int cellSizePixels = 16;
    [SerializeField] int viewPadding = 1;

    [Header("Zoom")]
    [SerializeField] float minOrthoSize = 2f;
    [SerializeField] float maxOrthoSize = 64f;
    [SerializeField] float zoomStep = 1.15f;
    [SerializeField] float startOrthoSize = 8f;

    Camera cam;
    Sprite cellSprite;
    Transform cellsRoot;

    readonly Dictionary<Vector2Int, GameObject> active = new();
    readonly Queue<GameObject> pool = new();
    readonly List<Vector2Int> stale = new();

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
            Debug.LogError("[GridManager] No Main Camera in scene.");
            enabled = false;
            return;
        }
        cam.orthographic = true;
        cam.orthographicSize = startOrthoSize;
        cam.backgroundColor = Color.black;
        cam.clearFlags = CameraClearFlags.SolidColor;

        cellSprite = BuildCellSprite();

        cellsRoot = new GameObject("Cells").transform;
        cellsRoot.SetParent(transform, false);
    }

    Sprite BuildCellSprite()
    {
        var tex = new Texture2D(cellSizePixels, cellSizePixels, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "CellTex"
        };
        var pixels = new Color32[cellSizePixels * cellSizePixels];
        var black = new Color32(0, 0, 0, 255);
        for (int i = 0; i < pixels.Length; i++) pixels[i] = black;
        int cx = cellSizePixels / 2;
        int cy = cellSizePixels / 2;
        pixels[cy * cellSizePixels + cx] = new Color32(255, 255, 255, 255);
        tex.SetPixels32(pixels);
        tex.Apply(false, false);

        var rect = new Rect(0, 0, cellSizePixels, cellSizePixels);
        return Sprite.Create(tex, rect, new Vector2(0f, 0f), pixelsPerUnit, 0, SpriteMeshType.FullRect);
    }

    void Update()
    {
        float scroll = ReadScroll();
        if (scroll != 0f)
        {
            float factor = scroll > 0f ? 1f / zoomStep : zoomStep;
            cam.orthographicSize = Mathf.Clamp(cam.orthographicSize * factor, minOrthoSize, maxOrthoSize);
        }
        UpdateVisibleCells();
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

    void UpdateVisibleCells()
    {
        float cellWorld = (float)cellSizePixels / pixelsPerUnit;
        Vector3 camPos = cam.transform.position;
        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        int xMin = Mathf.FloorToInt((camPos.x - halfW) / cellWorld) - viewPadding;
        int xMax = Mathf.CeilToInt((camPos.x + halfW) / cellWorld) + viewPadding;
        int yMin = Mathf.FloorToInt((camPos.y - halfH) / cellWorld) - viewPadding;
        int yMax = Mathf.CeilToInt((camPos.y + halfH) / cellWorld) + viewPadding;

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
                go.transform.localPosition = new Vector3(x * cellWorld, y * cellWorld, 0f);
                active[coord] = go;
            }
        }
    }
}
