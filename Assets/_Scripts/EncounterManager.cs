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
}
