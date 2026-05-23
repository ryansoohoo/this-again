using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

// Server-authoritative TILE-BASED player. The character always rests at a cell center; pressing a
// direction (8-way WASD) or right-clicking a cell makes the SERVER lerp it one cell at a time. The
// OWNER only submits intent (a NetworkVariable direction + a click-target RPC); the server owns the
// authoritative transform, which NetworkTransform replicates. Walk/Idle + 4-way facing are derived
// from the replicated position delta, so the lerp itself drives the animation (no extra net state).
[RequireComponent(typeof(NetworkObject))]
public class PlayerController : NetworkBehaviour
{
    public static PlayerController LocalInstance { get; private set; }   // the owning client's own player (drives the local view window)

    [Header("Movement (server)")]
    [SerializeField] float moveSpeed = 4f;          // world units/sec while crossing a cell

    [Header("Visuals (all clients)")]
    [SerializeField] Animator animator;

    // Owner -> server intent: 8-way step direction, each axis in {-1,0,1}. Owner-writable.
    readonly NetworkVariable<Vector2> moveInput =
        new(Vector2.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    // Server grid state.
    Vector2Int cell;
    bool moving;
    Vector2Int toCell;
    Vector2 fromPos, toPos;
    float moveT, stepDuration;
    bool hasTarget;
    Vector2Int targetCell;
    readonly List<Vector2Int> path = new();   // A* route to targetCell (cells to step onto, in order)
    int pathIndex;

    // Client-only animation derivation.
    Vector3 lastPos;
    Vector2 facing = new(1f, -1f);                  // default SE (down-right)
    static readonly int SpeedHash = Animator.StringToHash("Speed");
    static readonly int DirXHash = Animator.StringToHash("DirX");
    static readonly int DirYHash = Animator.StringToHash("DirY");

    // Owner-only double-click detection for auto-move (double-left-click).
    float lastClickTime = -1f;
    Vector2 lastClickPos;
    const float DoubleClickTime = 0.3f;
    const float DoubleClickPixels = 24f;

    public override void OnNetworkSpawn()
    {
        if (IsOwner) LocalInstance = this;
        lastPos = transform.position;
        if (IsServer)
        {
            var gm = Game.Instance;
            if (gm != null)
            {
                int n = (int)OwnerClientId;
                cell = new Vector2Int((n % 5) - 2, (n / 5) - 2);   // small spread around the origin so players don't stack
                transform.position = (Vector3)gm.CellCenter(cell.x, cell.y);
            }
            lastPos = transform.position;
        }
        if (animator != null) { animator.SetFloat(DirXHash, facing.x); animator.SetFloat(DirYHash, facing.y); }
    }

    public override void OnNetworkDespawn()
    {
        if (LocalInstance == this) LocalInstance = null;
    }

    // The cell the player visually occupies right now (from the replicated transform).
    public Vector2Int CurrentCell()
    {
        var gm = Game.Instance;
        return gm != null ? gm.WorldToCell(transform.position) : Vector2Int.zero;
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsOwner) ReadOwnerInput();
        if (IsServer) ServerStep(Time.deltaTime);
        UpdateVisuals();
    }

    // ---- Owner input ----
    void ReadOwnerInput()
    {
        if (InputState.Typing)              // command line open: don't let typed letters drive movement
        {
            if (moveInput.Value != Vector2.zero) moveInput.Value = Vector2.zero;
            return;
        }

        Vector2 dir = Vector2.zero;
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed) dir.y += 1f;
            if (kb.sKey.isPressed) dir.y -= 1f;
            if (kb.aKey.isPressed) dir.x -= 1f;
            if (kb.dKey.isPressed) dir.x += 1f;
        }
        if (dir != moveInput.Value) moveInput.Value = dir;          // raw 8-way intent (not normalized)

        // Double-left-click sets an auto-move target (the server walks the player there tile by tile).
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            float now = Time.unscaledTime;
            Vector2 sp = mouse.position.ReadValue();
            bool isDouble = now - lastClickTime <= DoubleClickTime && Vector2.Distance(sp, lastClickPos) <= DoubleClickPixels;
            lastClickTime = isDouble ? -1f : now;                    // reset after a double so a 3rd click starts fresh
            lastClickPos = sp;

            if (isDouble)
            {
                var cam = Game.Instance != null ? Game.Instance.Cam : Camera.main;
                if (cam != null)
                {
                    Vector3 wp = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, Mathf.Abs(cam.transform.position.z)));
                    SetTargetServerRpc(new Vector2(wp.x, wp.y));
                }
            }
        }
    }

    [ServerRpc]
    void SetTargetServerRpc(Vector2 worldPoint)
    {
        var gm = Game.Instance;
        if (gm == null) return;
        targetCell = gm.WorldToCell(worldPoint);
        // Route from where we'll be standing (the in-progress step's destination, else the current
        // cell) so a fresh click never rewinds the current tile. A* steers around water; clicking on
        // water or an unreachable spot yields a best-effort route to the nearest reachable cell.
        Vector2Int from = moving ? toCell : cell;
        path.Clear();
        path.AddRange(Pathfinder.FindPath(from, targetCell, gm.IsWalkable));
        pathIndex = 0;
        hasTarget = path.Count > 0;
    }

    // ---- Server tile movement (authoritative) ----
    void ServerStep(float dt)
    {
        var gm = Game.Instance;
        if (gm == null) return;

        if (moving)
        {
            moveT += dt / stepDuration;
            if (moveT < 1f) { transform.position = Vector2.Lerp(fromPos, toPos, moveT); return; }
            cell = toCell;                                          // arrived: snap to the exact cell center
            transform.position = (Vector3)toPos;
            moving = false;
        }

        // WASD intent takes priority and chains while held; otherwise follow the click-to-move route.
        Vector2 mi = moveInput.Value;
        if (mi.sqrMagnitude > 0.01f)
        {
            hasTarget = false; path.Clear();                       // WASD cancels click-to-move
            TryWalkStep(gm, new Vector2Int(StepSign(mi.x), StepSign(mi.y)));
            return;
        }

        if (hasTarget && pathIndex < path.Count)
        {
            Vector2Int next = path[pathIndex++];
            if (!gm.IsWalkable(next.x, next.y)) { hasTarget = false; path.Clear(); return; }  // route stale (live regen)
            BeginStep(gm, cell, next);
            if (pathIndex >= path.Count) hasTarget = false;        // last cell consumed
        }
    }

    // WASD: step if the destination is walkable. A diagonal needs both orthogonal cells open (so you
    // can't cut a water corner); if the diagonal is blocked, slide along whichever axis is clear.
    void TryWalkStep(Game gm, Vector2Int step)
    {
        if (step == Vector2Int.zero) return;
        bool diag = step.x != 0 && step.y != 0;
        bool sideX = gm.IsWalkable(cell.x + step.x, cell.y);
        bool sideY = gm.IsWalkable(cell.x, cell.y + step.y);

        if (gm.IsWalkable(cell.x + step.x, cell.y + step.y) && (!diag || (sideX && sideY)))
        {
            BeginStep(gm, cell, cell + step);
            return;
        }
        if (!diag) return;                                         // straight step into water: blocked
        if (sideX) BeginStep(gm, cell, new Vector2Int(cell.x + step.x, cell.y));
        else if (sideY) BeginStep(gm, cell, new Vector2Int(cell.x, cell.y + step.y));
    }

    void BeginStep(Game gm, Vector2Int from, Vector2Int to)
    {
        fromPos = gm.CellCenter(from.x, from.y);
        toPos = gm.CellCenter(to.x, to.y);
        toCell = to;
        stepDuration = Mathf.Max(Vector2.Distance(fromPos, toPos) / Mathf.Max(moveSpeed, 0.01f), 1e-4f);
        moveT = 0f;
        moving = true;
    }

    static int StepSign(float v) => v > 0.001f ? 1 : (v < -0.001f ? -1 : 0);

    // ---- Visuals: Idle/Walk + 4-way facing from the replicated position ----
    void UpdateVisuals()
    {
        Vector3 pos = transform.position;
        Vector2 delta = (Vector2)(pos - lastPos);
        lastPos = pos;

        float speed = delta.magnitude / Mathf.Max(Time.deltaTime, 1e-5f);
        if (animator != null) animator.SetFloat(SpeedHash, speed);

        if (delta.sqrMagnitude > 1e-6f)
        {
            Vector2 d = delta.normalized;
            if (Mathf.Abs(d.y) < 0.2f) d.y -= 0.2f;                 // bias near-horizontal toward facing the camera (down)
            facing = d.normalized;
            if (animator != null)
            {
                animator.SetFloat(DirXHash, facing.x);
                animator.SetFloat(DirYHash, facing.y);
            }
        }
    }
}
