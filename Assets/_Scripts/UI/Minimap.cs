using Unity.Netcode;
using UnityEngine;

// Local-only minimap HUD (IMGUI). Square = whole grid, rectangle = current camera viewport.
// Not networked/replicated. Hidden only on a dedicated server (server with no local client).
// Lives on the "Minimap" prefab (Resources/Minimap), instantiated by Game at boot.
public class Minimap : MonoBehaviour
{
    [SerializeField] float size = 330f;
    [SerializeField] float margin = 10f;
    [SerializeField] float border = 1f;
    [SerializeField] Color background = new Color(0f, 0f, 0f, 0.6f);
    [SerializeField] Color frame = new Color(1f, 1f, 1f, 0.5f);
    [SerializeField] Color viewportFill = new Color(1f, 1f, 1f, 0.05f);
    [SerializeField] Color viewportBorder = new Color(1f, 1f, 0.2f, 0.4f);

    static Texture2D _tex;

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;            // draw-only: skip layout/input passes (GC)
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsServer && !nm.IsClient) return;          // dedicated server: no local view

        var gm = Game.Instance;
        if (gm == null) return;
        var cam = gm.Cam != null ? gm.Cam : Camera.main;
        if (cam == null) return;

        Vector2 ext = gm.MinimapWorldExtent;
        float worldX = ext.x * 2f, worldY = ext.y * 2f;
        if (worldX <= 0f || worldY <= 0f) return;
        Vector2 ctr = gm.MinimapWorldCenter;
        float left = ctr.x - ext.x, bottom = ctr.y - ext.y;

        // Fit the world rect into the `size` box, preserving its X:Y aspect (square when ext.x == ext.y).
        float uiW = size, uiH = size;
        if (ext.x >= ext.y) uiH = size * (ext.y / ext.x); else uiW = size * (ext.x / ext.y);

        float ox = Screen.width - margin - uiW, oy = margin;           // top-right
        var box = new Rect(ox, oy, uiW, uiH);
        Fill(box, background);                                          // map background
        var mapTex = gm.MinimapTexture;
        if (mapTex != null) GUI.DrawTexture(box, mapTex);              // wide overview around the player
        Outline(box, frame);

        float h = cam.orthographicSize, w = h * cam.aspect;
        Vector3 c = cam.transform.position;
        float nL = Mathf.Clamp01((c.x - w - left) / worldX);
        float nR = Mathf.Clamp01((c.x + w - left) / worldX);
        float nB = Mathf.Clamp01((c.y - h - bottom) / worldY);
        float nT = Mathf.Clamp01((c.y + h - bottom) / worldY);
        var view = new Rect(ox + nL * uiW, oy + (1f - nT) * uiH,        // Y flipped: GUI origin top-left
                            (nR - nL) * uiW, (nT - nB) * uiH);
        Fill(view, viewportFill);                                      // subtle fill
        Outline(view, viewportBorder);                                 // viewport box border

        // The overview is centered on the player, so they sit at the middle of the box.
        float dot = Mathf.Max(2f, Mathf.Min(uiW, uiH) * 0.025f);
        Fill(new Rect(ox + uiW * 0.5f - dot * 0.5f, oy + uiH * 0.5f - dot * 0.5f, dot, dot), viewportBorder);
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
