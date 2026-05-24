using UnityEngine;

// Per-ghost collision-circle debug ring, pre-placed on Ghost.prefab (a child carrying a LineRenderer). Unlike
// OnDrawGizmos (editor Scene view only), a LineRenderer renders in the Game view AND in player builds, under URP's
// 2D renderer — so 'colgizmos' works for any peer, host or client. Each ghost owns its ring; it reads positions
// for free via parenting. Shown only while the local player is in an instance: collision is underworld-only, and
// region-only AOI means every visible ghost is a roommate, so gating all rings on SelfInInstance is correct.
[RequireComponent(typeof(LineRenderer))]
public sealed class CollisionCircle : MonoBehaviour
{
    public static bool Show;            // toggled by the 'colgizmos' command (static: survives ghost respawns)

    const int Segments = 32;
    static Material _shared;            // one Sprites/Default material for every ring (always present in a 2D build)

    LineRenderer _lr;
    float _builtRadius = -1f;

    void Awake()
    {
        _lr = GetComponent<LineRenderer>();
        if (_shared == null)
        {
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) Debug.LogWarning("[CollisionCircle] Sprites/Default not found; ring may not render.");
            _shared = new Material(sh);
        }
        _lr.sharedMaterial = _shared;
        _lr.useWorldSpace = false;      // ring follows the ghost via parenting
        _lr.loop = true;
        _lr.positionCount = Segments;
        _lr.widthMultiplier = 0.04f;
        _lr.numCapVertices = 0;
        _lr.alignment = LineAlignment.View;
        _lr.sortingOrder = 1000;        // draw over the player sprites
        _lr.startColor = _lr.endColor = new Color(0.2f, 1f, 1f, 0.9f);
        _lr.enabled = false;
    }

    void LateUpdate()
    {
        bool show = Show && GhostManager.Instance != null && GhostManager.Instance.SelfInInstance;
        if (_lr.enabled != show) _lr.enabled = show;
        if (!show) return;
        float r = (Game.Instance != null && Game.Instance.MovementCfg != null) ? Game.Instance.MovementCfg.collisionRadius : 0.3f;
        if (!Mathf.Approximately(r, _builtRadius)) Rebuild(r);
    }

    void Rebuild(float r)
    {
        for (int i = 0; i < Segments; i++)
        {
            float a = i / (float)Segments * Mathf.PI * 2f;
            _lr.SetPosition(i, new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
        }
        _builtRadius = r;
    }
}
