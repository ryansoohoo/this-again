using Unity.Netcode;
using UnityEngine;

// Local-only minimap HUD (IMGUI). Square = whole grid, rectangle = current camera viewport.
// Not networked/replicated. Hidden only on a dedicated server (server with no local client).
// Lives on the "Minimap" prefab (Resources/Minimap), instantiated by GridManager at boot.
public class Minimap : MonoBehaviour
{
    [SerializeField] float size = 160f;
    [SerializeField] float margin = 10f;
    [SerializeField] float border = 1f;
    [SerializeField] Color background = new Color(0f, 0f, 0f, 0.6f);
    [SerializeField] Color frame = new Color(1f, 1f, 1f, 0.5f);
    [SerializeField] Color viewportFill = new Color(1f, 1f, 1f, 0.15f);
    [SerializeField] Color viewportBorder = new Color(1f, 1f, 0.2f, 0.95f);

    static Texture2D _tex;

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;            // draw-only: skip layout/input passes (GC)
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer && !nm.IsClient) return;          // dedicated server: no local view

        var gm = GridManager.Instance;
        if (gm == null) return;
        var cam = gm.Cam != null ? gm.Cam : Camera.main;
        if (cam == null) return;

        float half = gm.HalfExtent, world = half * 2f;
        if (world <= 0f) return;

        float ox = Screen.width - margin - size, oy = margin;          // top-right
        var square = new Rect(ox, oy, size, size);
        Fill(square, background);                                       // map background
        var mapTex = gm.MinimapTexture;
        if (mapTex != null) GUI.DrawTexture(square, mapTex);            // one colored dot per tile
        Outline(square, frame);

        float h = cam.orthographicSize, w = h * cam.aspect;
        Vector3 c = cam.transform.position;
        float nL = Mathf.Clamp01((c.x - w + half) / world);
        float nR = Mathf.Clamp01((c.x + w + half) / world);
        float nB = Mathf.Clamp01((c.y - h + half) / world);
        float nT = Mathf.Clamp01((c.y + h + half) / world);
        var view = new Rect(ox + nL * size, oy + (1f - nT) * size,      // Y flipped: GUI origin top-left
                            (nR - nL) * size, (nT - nB) * size);
        Fill(view, viewportFill);
        Outline(view, viewportBorder);                                 // bright viewport border
    }

    static Texture2D Tex { get { if (_tex == null) { _tex = new Texture2D(1, 1); _tex.SetPixel(0, 0, Color.white); _tex.Apply(); } return _tex; } }
    static void Fill(Rect r, Color col) { var o = GUI.color; GUI.color = col; GUI.DrawTexture(r, Tex); GUI.color = o; }
    void Outline(Rect r, Color col)
    {
        Fill(new Rect(r.x, r.y, r.width, border), col);
        Fill(new Rect(r.x, r.yMax - border, r.width, border), col);
        Fill(new Rect(r.x, r.y, border, r.height), col);
        Fill(new Rect(r.xMax - border, r.y, border, r.height), col);
    }
}
