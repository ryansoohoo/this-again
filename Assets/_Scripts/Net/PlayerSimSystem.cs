using UnityEngine;

// Server movement sim (LOGIC): the old PlayerMovement.ServerStep, now run over every ServerPlayer in the
// registry each frame. Consumes submittedInput / click path, steps PlayerMotion, writes ServerPlayer.worldPos.
// Pure server-side; nothing here replicates (the snapshot stream does). Uses Game.Instance world queries.
public static class PlayerSimSystem
{
    const float SecondsPerCost = 0.1f;   // same currency as the old PlayerMovement

    public static void StepAll(PlayerRegistry reg, float moveSpeed, float dt)
    {
        var gm = Game.Instance;
        if (gm == null) return;
        foreach (var sp in reg.Players.Values) Step(gm, sp, moveSpeed, dt);
    }

    // In-instance free movement now runs through AttackSimSystem.StepInstanceFixed (attack + lunge + movement via
    // the shared InstanceStep), so it is no longer here. This system keeps overworld grid/click movement.

    static void Step(Game gm, ServerPlayer sp, float moveSpeed, float dt)
    {
        if (sp.inInstance) return;   // in-instance players move via the fixed free-step, not the grid step
        var m = sp.motion;
        if (m.moving)
        {
            m.moveT += dt / m.stepDuration;
            if (m.moveT < 1f) { sp.worldPos = Vector2.Lerp(m.fromPos, m.toPos, m.moveT); return; }
            m.cell = m.toCell;
            sp.worldPos = m.toPos;
            m.moving = false;
        }

        Vector2 mi = sp.submittedInput;
        if (mi.sqrMagnitude > 0.01f)
        {
            m.hasTarget = false; m.path.Clear();
            TryWalkStep(gm, sp, new Vector2Int(StepSign(mi.x), StepSign(mi.y)), moveSpeed);
            return;
        }

        if (m.hasTarget && m.pathIndex < m.path.Count)
        {
            Vector2Int next = m.path[m.pathIndex++];
            if (!gm.IsWalkable(next.x, next.y)) { m.hasTarget = false; m.path.Clear(); return; }
            BeginStep(gm, sp, m.cell, next, moveSpeed);
            if (m.pathIndex >= m.path.Count) m.hasTarget = false;
        }
    }

    // Click-to-move: route from where we'll be standing to the target cell.
    public static void SetTarget(ServerPlayer sp, Vector2 worldPoint, float moveSpeed)
    {
        var gm = Game.Instance; if (gm == null) return;
        var m = sp.motion;
        m.targetCell = gm.WorldToCell(worldPoint);
        Vector2Int from = m.moving ? m.toCell : m.cell;
        m.path.Clear();
        m.path.AddRange(Pathfinder.FindPath(from, m.targetCell, gm.IsWalkable, EnterCost(gm, moveSpeed)));
        m.pathIndex = 0;
        m.hasTarget = m.path.Count > 0;
    }

    public static void Halt(ServerPlayer sp)
    {
        sp.motion.hasTarget = false; sp.motion.path.Clear(); sp.motion.pathIndex = 0;
    }

    // Snap to a cell centre with no slide; raise the snap flag so ghosts don't interpolate across the jump.
    public static void Teleport(ServerPlayer sp, Vector2Int cell)
    {
        var gm = Game.Instance; if (gm == null) return;
        var m = sp.motion;
        m.moving = false; m.hasTarget = false; m.path.Clear(); m.pathIndex = 0;
        m.cell = cell;
        sp.worldPos = gm.CellCenter(cell.x, cell.y);
        sp.snap = true;
    }

    static void TryWalkStep(Game gm, ServerPlayer sp, Vector2Int step, float moveSpeed)
    {
        if (step == Vector2Int.zero) return;
        var m = sp.motion;
        bool diag = step.x != 0 && step.y != 0;
        bool sideX = gm.IsWalkable(m.cell.x + step.x, m.cell.y);
        bool sideY = gm.IsWalkable(m.cell.x, m.cell.y + step.y);
        if (gm.IsWalkable(m.cell.x + step.x, m.cell.y + step.y) && (!diag || (sideX && sideY)))
        { BeginStep(gm, sp, m.cell, m.cell + step, moveSpeed); return; }
        if (!diag) return;
        if (sideX) BeginStep(gm, sp, m.cell, new Vector2Int(m.cell.x + step.x, m.cell.y), moveSpeed);
        else if (sideY) BeginStep(gm, sp, m.cell, new Vector2Int(m.cell.x, m.cell.y + step.y), moveSpeed);
    }

    static void BeginStep(Game gm, ServerPlayer sp, Vector2Int from, Vector2Int to, float moveSpeed)
    {
        var m = sp.motion;
        m.fromPos = gm.CellCenter(from.x, from.y);
        m.toPos = gm.CellCenter(to.x, to.y);
        m.toCell = to;
        float baseTime = Vector2.Distance(m.fromPos, m.toPos) / Mathf.Max(moveSpeed, 0.01f);
        float extra = gm.MoveCost(to.x, to.y) * SecondsPerCost;
        m.stepDuration = Mathf.Max(baseTime + extra, 1e-4f);
        m.moveT = 0f;
        m.moving = true;
    }

    // Cached enter-cost delegate: created once and reused, reading the per-call inputs from static fields, so a
    // click-to-move query doesn't allocate a fresh closure + delegate each time. Main-thread, one query at a time.
    static Game _enterGm;
    static float _enterUnitsPerSecond;
    static readonly System.Func<int, int, int> _enterCost =
        (x, y) => Mathf.RoundToInt(_enterGm.MoveCost(x, y) * SecondsPerCost * _enterUnitsPerSecond);

    static System.Func<int, int, int> EnterCost(Game gm, float moveSpeed)
    {
        _enterGm = gm;
        _enterUnitsPerSecond = Pathfinder.Ortho * Mathf.Max(moveSpeed, 0.01f) / Mathf.Max(gm.CellWorld, 1e-4f);
        return _enterCost;
    }

    static int StepSign(float v) => v > 0.001f ? 1 : (v < -0.001f ? -1 : 0);
}
