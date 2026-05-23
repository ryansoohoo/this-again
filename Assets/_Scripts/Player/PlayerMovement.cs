using Unity.Netcode;
using Unity.Netcode.Components;
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

    // Converts a biome's integer extraMoveCost into seconds added per tile. 0.1 => the int counts tenths of
    // a second (cost 1 = +0.1s, 10 = +1s). A normal tile crosses in CellWorld/moveSpeed (~0.25s).
    const float SecondsPerCost = 0.1f;

    // Owner -> server intent: 8-way step direction, each axis in {-1,0,1}. Owner-writable.
    readonly NetworkVariable<Vector2> moveInput =
        new(Vector2.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    readonly PlayerMotion motion = new();   // server-side movement state (data)
    readonly PlayerInput input = new();      // owner input reader (logic helper)

    // Server -> everyone: is this player currently inside a dungeon instance? Owner reads it to gate commands.
    readonly NetworkVariable<bool> inInstance =
        new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public bool InInstance => inInstance.Value;

    NetworkTransform netTransform;     // snap-teleports without an interpolated slide across the map
    Vector2Int overworldReturnCell;    // server-only: where to drop the player when they leave

    public override void OnNetworkSpawn()
    {
        netTransform = GetComponent<NetworkTransform>();
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

    // Stop all movement now (used when an encounter begins): cancel WASD intent and ask the server to drop
    // any click-to-move path, so the player can't drift off the encounter tile. An in-flight step finishes.
    public void Halt()
    {
        if (!IsSpawned) return;
        if (IsOwner) moveInput.Value = Vector2.zero;
        HaltServerRpc();
    }

    [ServerRpc]
    void HaltServerRpc()
    {
        motion.hasTarget = false;
        motion.path.Clear();
        motion.pathIndex = 0;
    }

    // ---- Dungeon instance teleport (server-authoritative) ----
    // Owner asks to enter the off-map room a structure site maps to; server teleports them and flips inInstance.
    public void RequestEnterInstance(Vector2Int siteCell)
    {
        if (!IsSpawned || InInstance) return;
        if (IsOwner) moveInput.Value = Vector2.zero;   // drop any walk intent so we don't drift on arrival
        EnterInstanceServerRpc(siteCell.x, siteCell.y);
    }

    public void RequestLeaveInstance()
    {
        if (!IsSpawned || !InInstance) return;
        if (IsOwner) moveInput.Value = Vector2.zero;
        LeaveInstanceServerRpc();
    }

    [ServerRpc]
    void EnterInstanceServerRpc(int siteX, int siteY)
    {
        if (inInstance.Value) return;
        overworldReturnCell = motion.cell;
        var origin = Underworld.RegionOriginForSite(siteX, siteY);
        ServerTeleport(Underworld.SpawnCell(origin, (int)OwnerClientId));
        inInstance.Value = true;
    }

    [ServerRpc]
    void LeaveInstanceServerRpc()
    {
        if (!inInstance.Value) return;
        ServerTeleport(overworldReturnCell);
        inInstance.Value = false;
    }

    // Snap the authoritative transform to a cell's centre with no interpolated slide, resetting movement so the
    // next step starts cleanly from the new cell.
    void ServerTeleport(Vector2Int cell)
    {
        var gm = Game.Instance;
        if (gm == null) return;
        motion.moving = false;
        motion.hasTarget = false;
        motion.path.Clear();
        motion.pathIndex = 0;
        motion.cell = cell;
        Vector3 pos = (Vector3)gm.CellCenter(cell.x, cell.y);
        if (netTransform != null) netTransform.Teleport(pos, transform.rotation, transform.localScale);
        else transform.position = pos;
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
        motion.path.AddRange(Pathfinder.FindPath(from, motion.targetCell, gm.IsWalkable, EnterCost(gm)));
        motion.pathIndex = 0;
        motion.hasTarget = motion.path.Count > 0;
    }

    // A* penalty for ENTERING a cell, in Pathfinder units, from the cell's biome extraMoveCost. One ortho
    // tile = Pathfinder.Ortho units = (CellWorld/moveSpeed) seconds, so unitsPerSecond = Ortho*speed/CellWorld;
    // converting the tile's extra seconds into that currency makes A* minimize total travel TIME — it detours
    // around slow terrain whenever a longer-but-faster route exists.
    System.Func<int, int, int> EnterCost(Game gm)
    {
        float unitsPerSecond = Pathfinder.Ortho * Mathf.Max(moveSpeed, 0.01f) / Mathf.Max(gm.CellWorld, 1e-4f);
        return (x, y) => Mathf.RoundToInt(gm.MoveCost(x, y) * SecondsPerCost * unitsPerSecond);
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
        float baseTime = Vector2.Distance(motion.fromPos, motion.toPos) / Mathf.Max(moveSpeed, 0.01f);
        float extra = gm.MoveCost(to.x, to.y) * SecondsPerCost;     // biome's flat per-tile slowdown (the tile entered)
        motion.stepDuration = Mathf.Max(baseTime + extra, 1e-4f);
        motion.moveT = 0f;
        motion.moving = true;
    }

    static int StepSign(float v) => v > 0.001f ? 1 : (v < -0.001f ? -1 : 0);
}
