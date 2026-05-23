using Unity.Netcode;
using UnityEngine;

// Server-authoritative tile movement (LOGIC). The OWNER submits intent (a NetworkVariable dir + a click
// ServerRpc) via PlayerInput; the SERVER consumes intent, runs Pathfinder, and writes PlayerMotion + the
// authoritative transform (replicated by NetworkTransform). Walk/Idle + facing are derived separately by
// PlayerView from the replicated transform.
[RequireComponent(typeof(NetworkObject))]
public class PlayerMovement : NetworkBehaviour
{
    public static PlayerMovement LocalInstance { get; private set; }   // the owning client's own player

    [Header("Movement (server)")]
    [SerializeField] float moveSpeed = 4f;          // world units/sec while crossing a cell

    // Owner -> server intent: 8-way step direction, each axis in {-1,0,1}. Owner-writable.
    readonly NetworkVariable<Vector2> moveInput =
        new(Vector2.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    readonly PlayerMotion motion = new();   // server-side movement state (data)
    readonly PlayerInput input = new();      // owner input reader (logic helper)

    public override void OnNetworkSpawn()
    {
        if (IsOwner) LocalInstance = this;
        if (IsServer)
        {
            var gm = Game.Instance;
            if (gm != null)
            {
                int n = (int)OwnerClientId;
                motion.cell = new Vector2Int((n % 5) - 2, (n / 5) - 2);   // small spread around the origin
                transform.position = (Vector3)gm.CellCenter(motion.cell.x, motion.cell.y);
            }
        }
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
    }

    // ---- Owner input -> networked intent ----
    void ReadOwnerInput()
    {
        var cam = Game.Instance != null ? Game.Instance.Cam : Camera.main;
        var intent = input.Read(cam);
        if (intent.dir != moveInput.Value) moveInput.Value = intent.dir;   // raw 8-way intent (not normalized)
        if (intent.hasClickTarget) SetTargetServerRpc(intent.clickWorld);
    }

    [ServerRpc]
    void SetTargetServerRpc(Vector2 worldPoint)
    {
        var gm = Game.Instance;
        if (gm == null) return;
        motion.targetCell = gm.WorldToCell(worldPoint);
        // Route from where we'll be standing (the in-progress step's destination, else the current cell).
        Vector2Int from = motion.moving ? motion.toCell : motion.cell;
        motion.path.Clear();
        motion.path.AddRange(Pathfinder.FindPath(from, motion.targetCell, gm.IsWalkable));
        motion.pathIndex = 0;
        motion.hasTarget = motion.path.Count > 0;
    }

    // ---- Server tile movement (authoritative) ----
    void ServerStep(float dt)
    {
        var gm = Game.Instance;
        if (gm == null) return;

        if (motion.moving)
        {
            motion.moveT += dt / motion.stepDuration;
            if (motion.moveT < 1f) { transform.position = Vector2.Lerp(motion.fromPos, motion.toPos, motion.moveT); return; }
            motion.cell = motion.toCell;                            // arrived: snap to the exact cell center
            transform.position = (Vector3)motion.toPos;
            motion.moving = false;
        }

        Vector2 mi = moveInput.Value;
        if (mi.sqrMagnitude > 0.01f)
        {
            motion.hasTarget = false; motion.path.Clear();          // WASD cancels click-to-move
            TryWalkStep(gm, new Vector2Int(StepSign(mi.x), StepSign(mi.y)));
            return;
        }

        if (motion.hasTarget && motion.pathIndex < motion.path.Count)
        {
            Vector2Int next = motion.path[motion.pathIndex++];
            if (!gm.IsWalkable(next.x, next.y)) { motion.hasTarget = false; motion.path.Clear(); return; }
            BeginStep(gm, motion.cell, next);
            if (motion.pathIndex >= motion.path.Count) motion.hasTarget = false;
        }
    }

    // WASD: step if walkable; a diagonal needs both orthogonal cells open, else slide along the clear axis.
    void TryWalkStep(Game gm, Vector2Int step)
    {
        if (step == Vector2Int.zero) return;
        bool diag = step.x != 0 && step.y != 0;
        bool sideX = gm.IsWalkable(motion.cell.x + step.x, motion.cell.y);
        bool sideY = gm.IsWalkable(motion.cell.x, motion.cell.y + step.y);

        if (gm.IsWalkable(motion.cell.x + step.x, motion.cell.y + step.y) && (!diag || (sideX && sideY)))
        {
            BeginStep(gm, motion.cell, motion.cell + step);
            return;
        }
        if (!diag) return;
        if (sideX) BeginStep(gm, motion.cell, new Vector2Int(motion.cell.x + step.x, motion.cell.y));
        else if (sideY) BeginStep(gm, motion.cell, new Vector2Int(motion.cell.x, motion.cell.y + step.y));
    }

    void BeginStep(Game gm, Vector2Int from, Vector2Int to)
    {
        motion.fromPos = gm.CellCenter(from.x, from.y);
        motion.toPos = gm.CellCenter(to.x, to.y);
        motion.toCell = to;
        motion.stepDuration = Mathf.Max(Vector2.Distance(motion.fromPos, motion.toPos) / Mathf.Max(moveSpeed, 0.01f), 1e-4f);
        motion.moveT = 0f;
        motion.moving = true;
    }

    static int StepSign(float v) => v > 0.001f ? 1 : (v < -0.001f ? -1 : 0);
}
