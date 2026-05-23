using UnityEngine;

// The encounter driver (local client only): watches the owning player's cell and, when they step onto a
// structure site, halts movement, swaps the command set to the Encounter scope, and opens the command
// console LOCKED with an intro line. The `enter`/`leave` commands (CommandBootstrap) read Current and call
// End() to finish. v1 is fully local + deterministic; only PlayerMovement.Halt touches the network. Added
// to the Game object at boot (like TunerPanels).
public sealed class EncounterManager : MonoBehaviour
{
    public static EncounterManager Instance { get; private set; }

    public StructureSite Current { get; private set; }
    public bool InEncounter => Current != null;

    const int TeleportJump = 1000;   // a cell delta this large is a dungeon teleport, not a walk step

    CommandConsole console;
    ChatPopup chatPopup;
    Vector2Int lastCell;
    bool haveLast;

    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

    void Update()
    {
        var lp = PlayerMovement.LocalInstance;
        var world = Game.Instance != null ? Game.Instance.World : null;
        if (lp == null || world == null) return;

        var cell = lp.CurrentCell();
        if (!haveLast) { lastCell = cell; haveLast = true; return; }   // don't trigger on the spawn cell

        // A dungeon teleport jumps the player thousands of cells in a frame: resync without triggering, so
        // landing back on the entrance site doesn't immediately re-open the encounter prompt.
        if (Mathf.Max(Mathf.Abs(cell.x - lastCell.x), Mathf.Abs(cell.y - lastCell.y)) > TeleportJump)
        {
            lastCell = cell;
            return;
        }

        if (!InEncounter && cell != lastCell)
        {
            var site = world.SiteAt(cell.x, cell.y);
            if (site != null) Begin(lp, site);
        }
        lastCell = cell;
    }

    void Begin(PlayerMovement lp, StructureSite site)
    {
        Current = site;
        lp.Halt();
        CommandRouter.Instance.EnterEncounter();
        if (console == null) console = FindFirstObjectByType<CommandConsole>();
        if (console != null) console.OpenLocked();
        if (chatPopup == null) chatPopup = FindFirstObjectByType<ChatPopup>();
        if (chatPopup != null) chatPopup.SnapVisible();
        GameLog.Post(OutputType.Encounter, $"You approach {site.Name}, {site.Def.label}. (enter / leave)");
    }

    // Called by the `leave` command. Clears the lock BEFORE the command result closes the console.
    public void End()
    {
        if (!InEncounter) return;
        Current = null;
        CommandRouter.Instance.ExitEncounter();
        if (console != null) console.Unlock();
        // lastCell is still the site cell, so we won't re-trigger until the player steps off and back on.
    }

    // Called by the `enter` command: descend into the site's dungeon instance. Clears the site prompt and swaps
    // the Encounter command set for the Instance one; PlayerMovement does the actual teleport.
    public void EnterDungeon()
    {
        if (!InEncounter) return;
        Current = null;
        CommandRouter.Instance.EnterInstance();
        if (console != null) console.Unlock();
    }
}
