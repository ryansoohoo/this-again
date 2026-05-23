using System.Text;
using UnityEngine;

// Registers the built-in commands into the CommandRouter. Lives in the game assembly (not the command
// framework) because handlers reach into game systems — RelayConnector, encounters, later inventory/abilities.
// Idempotent: safe to call from CommandConsole.Awake every play. New systems add their own commands the
// same way (router.Registry.Register(...)) and toggle their scope via the router when they activate.
public static class CommandBootstrap
{
    public static void EnsureInstalled()
    {
        var router = CommandRouter.Instance;
        if (router.Registry.All.Count > 0) return;   // already installed (statics survive domain reloads)
        var r = router.Registry;

        // ---- Global (always available) ----
        r.Register(new Command
        {
            Keyword = "help", Scope = CommandScope.Global, Arg = ArgMode.None,
            Description = "List the commands you can use right now.",
            Run = _ => CommandResult.Ok(HelpText(router), keepOpen: true),
        });

        // ---- World / connection (exploration) ----
        r.Register(new Command
        {
            Keyword = "create lobby", Aliases = new[] { "host" }, Scope = CommandScope.World, Arg = ArgMode.None,
            Description = "Host a Relay lobby and get a share code.", Usage = "create lobby",
            Run = _arg => { var n = Net(); if (n != null) _ = n.HostAsync(); return CommandResult.Ok(keepOpen: true); },   // stay open; RelayConnector posts progress + the join code
        });
        r.Register(new Command
        {
            Keyword = "join", Scope = CommandScope.World, Arg = ArgMode.Required,
            Description = "Join a Relay lobby by code.", Usage = "join <code>",
            Run = arg => { var n = Net(); if (n != null) _ = n.JoinAsync(arg.ToUpperInvariant()); return CommandResult.Ok(keepOpen: true); },   // stay open; RelayConnector posts join progress
        });
        // ---- Encounter (entering a structure site; only available while standing in one) ----
        r.Register(new Command
        {
            Keyword = "enter", Scope = CommandScope.Encounter, Arg = ArgMode.None,
            Description = "Enter the place you're standing at.",
            Run = _ =>
            {
                var e = EncounterManager.Instance;
                string name = e != null && e.Current != null ? e.Current.Name : "the place";
                return CommandResult.Ok($"A villager greets you to {name}. \"Welcome, traveler.\"", keepOpen: true, output: OutputType.Encounter);
            },
        });
        r.Register(new Command
        {
            Keyword = "leave", Scope = CommandScope.Encounter, Arg = ArgMode.None,
            Description = "Leave and return to the world.",
            Run = _ => { EncounterManager.Instance?.End(); return CommandResult.Ok("You leave.", keepOpen: false, output: OutputType.Encounter); },
        });

        // ---- Inventory (available in the world and in combat) ----
        r.Register(new Command
        {
            Keyword = "inventory", Aliases = new[] { "inv" }, Scope = CommandScope.Inventory, Arg = ArgMode.None,
            Description = "Show your inventory.",
            Run = _ => CommandResult.Ok("Your inventory is empty.", keepOpen: true, output: OutputType.Inventory),
        });

        // ---- Debug ----
        r.Register(new Command
        {
            Keyword = "sites", Scope = CommandScope.World, Arg = ArgMode.None,
            Description = "(debug) Report the nearest structure site to you.",
            Run = _ => CommandResult.Ok(NearestSite(), keepOpen: true, output: OutputType.System),
        });
    }

    static RelayConnector Net()
    {
        var n = Object.FindFirstObjectByType<RelayConnector>();
        if (n == null) GameLog.Post(OutputType.System, "No network manager in the scene.");
        return n;
    }

    static string NearestSite()
    {
        var gm = Game.Instance; var lp = PlayerMovement.LocalInstance;
        if (gm == null || gm.World == null || lp == null) return "No world/player.";
        var c = lp.CurrentCell();
        const int R = 80;
        StructureSite best = null; int bestD = int.MaxValue;
        for (int dx = -R; dx <= R; dx++)
        for (int dy = -R; dy <= R; dy++)
        {
            var s = gm.World.SiteAt(c.x + dx, c.y + dy);
            if (s == null) continue;
            int d = Mathf.Abs(dx) + Mathf.Abs(dy);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best == null ? $"No sites within {R} cells."
                            : $"Nearest: {best.Name} ({best.Def.label}) at ({best.Cell.x},{best.Cell.y}), {bestD} cells away.";
    }

    static string HelpText(CommandRouter router)
    {
        var sb = new StringBuilder("Available commands:");
        foreach (var c in router.Registry.All)
            if ((c.Scope & router.Active) != 0)
                sb.Append("\n  ").Append(c.Usage ?? c.Keyword).Append(" — ").Append(c.Description);
        return sb.ToString();
    }
}
