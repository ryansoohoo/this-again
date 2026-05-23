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
        r.Register(new Command
        {
            Keyword = "fight", Scope = CommandScope.World, Arg = ArgMode.None,
            Description = "(debug) Start a mock encounter to reveal combat commands.",
            Run = _ => { CommandRouter.Instance.EnterEncounter(); return CommandResult.Ok("An encounter begins! Try 'attack' or 'flee'.", keepOpen: true, output: OutputType.Encounter); },
        });

        // ---- Encounter (combat; only available mid-encounter) ----
        r.Register(new Command
        {
            Keyword = "attack", Scope = CommandScope.Encounter, Arg = ArgMode.Optional,
            Description = "Attack the enemy (or a named target).", Usage = "attack [target]",
            Run = arg => CommandResult.Ok(string.IsNullOrEmpty(arg) ? "You attack!" : "You attack the " + arg + "!", keepOpen: true, output: OutputType.Encounter),
        });
        r.Register(new Command
        {
            Keyword = "flee", Scope = CommandScope.Encounter, Arg = ArgMode.None,
            Description = "Flee the encounter back to the world.",
            Run = _ => { CommandRouter.Instance.ExitEncounter(); return CommandResult.Ok("You flee back to the world.", output: OutputType.Encounter); },
        });

        // ---- Inventory (available in the world and in combat) ----
        r.Register(new Command
        {
            Keyword = "inventory", Aliases = new[] { "inv" }, Scope = CommandScope.Inventory, Arg = ArgMode.None,
            Description = "Show your inventory.",
            Run = _ => CommandResult.Ok("Your inventory is empty.", keepOpen: true, output: OutputType.Inventory),
        });
    }

    static RelayConnector Net()
    {
        var n = Object.FindFirstObjectByType<RelayConnector>();
        if (n == null) GameLog.Post(OutputType.System, "No network manager in the scene.");
        return n;
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
