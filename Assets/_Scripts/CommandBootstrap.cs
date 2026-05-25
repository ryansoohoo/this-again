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
            Description = "Descend into the dungeon you're standing at.",
            Run = _ =>
            {
                var e = EncounterManager.Instance;
                var pm = LocalPlayer.Instance;
                var site = e != null ? e.Current : null;
                if (site == null || pm == null) return CommandResult.Bad("There's nothing to enter here.");
                pm.RequestEnterInstance(site.Cell);   // server teleports us to the site's off-map room
                e.EnterDungeon();                     // clear the prompt + swap Encounter scope for Instance
                return CommandResult.Ok($"You descend into {site.Name}.", keepOpen: false, output: OutputType.Encounter);
            },
        });
        r.Register(new Command
        {
            Keyword = "leave", Scope = CommandScope.Encounter | CommandScope.Instance, Arg = ArgMode.None,
            Description = "Leave: climb out of the dungeon, or step away from a place.",
            Run = _ =>
            {
                var pm = LocalPlayer.Instance;
                if (pm != null && pm.InInstance)
                {
                    pm.RequestLeaveInstance();
                    CommandRouter.Instance.ExitInstance();   // Instance -> World
                    return CommandResult.Ok("You climb back to the surface.", keepOpen: false, output: OutputType.Encounter);
                }
                EncounterManager.Instance?.End();
                return CommandResult.Ok("You leave.", keepOpen: false, output: OutputType.Encounter);
            },
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
        r.Register(new Command
        {
            Keyword = "dungeon", Scope = CommandScope.World, Arg = ArgMode.None,
            Description = "(debug) Teleport into a shared test dungeon room.",
            Run = _ =>
            {
                var pm = LocalPlayer.Instance;
                if (pm == null) return CommandResult.Bad("No player yet — host or join first.");
                if (pm.InInstance) return CommandResult.Bad("You're already in a dungeon.");
                pm.RequestEnterInstance(Vector2Int.zero);   // fixed test room: everyone who runs this shares it
                CommandRouter.Instance.EnterInstance();      // World -> Instance (so 'leave' becomes available)
                return CommandResult.Ok("(debug) Descending into the test dungeon. Type 'leave' to return.", keepOpen: false, output: OutputType.System);
            },
        });
        r.Register(new Command
        {
            Keyword = "effect", Scope = CommandScope.Instance, Arg = ArgMode.Optional,
            Description = "(debug) Apply/clear a status effect on yourself to test the framework.",
            Usage = "effect [hitstun|cooldown|poison|freeze|slow|list|clear]",
            Run = arg =>
            {
                var hub = ReplicationHub.Instance;
                var status = hub != null ? hub.DebugLocalStatus() : null;
                if (status == null) return CommandResult.Bad("Effects are host-only for now — run this on the host, in a dungeon.");
                var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
                if (cat == null) return CommandResult.Bad("No StatusCatalog wired on Game.");

                var parts = arg.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                string which = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
                if (which == "list")
                {
                    var sb = new StringBuilder("Active: ");
                    for (int i = 0; i < status.count; i++)
                        sb.Append((StatusKind)status.effects[i].defId).Append('(').Append(status.effects[i].remainingTicks).Append("t x").Append(status.effects[i].stacks).Append(") ");
                    if (status.count == 0) sb.Append("none");
                    return CommandResult.Ok(sb.ToString(), keepOpen: true, output: OutputType.System);
                }
                if (which == "clear") { status.Clear(); return CommandResult.Ok("Effects cleared.", keepOpen: true, output: OutputType.System); }
                if (!System.Enum.TryParse<StatusKind>(MapName(which), true, out var kind))
                    return CommandResult.Bad("Usage: effect [hitstun|cooldown|poison|freeze|slow|list|clear]");
                int id = (int)kind;
                if (cat.Defs == null || id >= cat.Defs.Length) return CommandResult.Bad("Catalog missing that effect — run Tools > Combat > Build Status Catalog.");
                int dur = kind == StatusKind.AttackCooldown ? 30 : -1;   // cooldown has no inherent duration
                StatusLogic.Apply(status, cat.Defs[id], 0u, self: kind == StatusKind.AttackCooldown, durationOverride: dur);
                return CommandResult.Ok($"Applied {kind}.", keepOpen: true, output: OutputType.System);
            },
        });
        r.Register(new Command
        {
            Keyword = "enchant", Scope = CommandScope.Instance, Arg = ArgMode.Optional,
            Description = "(debug) Set your equipped weapon's main on-hit status effect (mutates the weapon).",
            Usage = "enchant [poison|freeze|slow|bleed|fire|fear|none|list]",
            Run = arg =>
            {
                var lp = LocalPlayer.Instance;
                var weapon = lp != null ? lp.EquippedWeapon : null;
                if (weapon == null) return CommandResult.Bad("No weapon equipped.");
                var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
                if (cat == null) return CommandResult.Bad("No StatusCatalog wired on Game.");

                string which = arg.Trim().ToLowerInvariant();
                if (which.Length == 0 || which == "list")
                {
                    string cur = weapon.mainEffect != null ? weapon.mainEffect.kind.ToString() : "none";
                    return CommandResult.Ok($"{weapon.name} main status: {cur}. Usage: enchant [poison|freeze|slow|bleed|fire|fear|none]", keepOpen: true, output: OutputType.System);
                }
                if (which == "none" || which == "clear")
                {
                    weapon.SetMainEffect(null);
#if UNITY_EDITOR
                    UnityEditor.EditorUtility.SetDirty(weapon);
#endif
                    return CommandResult.Ok($"{weapon.name} enchant removed.", keepOpen: true, output: OutputType.System);
                }
                if (!System.Enum.TryParse<StatusKind>(which, true, out var kind) || kind == StatusKind.HitStun || kind == StatusKind.AttackCooldown)
                    return CommandResult.Bad("Usage: enchant [poison|freeze|slow|bleed|fire|fear|none|list]");
                var fx = cat.Visual((int)kind);
                if (fx == null) return CommandResult.Bad("Catalog missing that effect — run Tools > Combat > Build Status Catalog.");
                weapon.SetMainEffect(fx);
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(weapon);
#endif
                return CommandResult.Ok($"{weapon.name} enchanted with {kind} (its main status). Strikes now apply it.", keepOpen: true, output: OutputType.System);
            },
        });
        r.Register(new Command
        {
            Keyword = "colgizmos", Aliases = new[] { "cgz" }, Scope = CommandScope.Global, Arg = ArgMode.None,
            Description = "(debug) Toggle collision rings — player body circles drawn in-game (works in builds; underworld only).",
            Run = _ =>
            {
                CollisionCircle.Show = !CollisionCircle.Show;
                return CommandResult.Ok($"Collision rings {(CollisionCircle.Show ? "ON" : "OFF")}.", keepOpen: true, output: OutputType.System);
            },
        });
        r.Register(new Command
        {
            Keyword = "time", Scope = CommandScope.World, Arg = ArgMode.Optional,
            Description = "(debug) Show, set (0..1), or resume ('off') the day/night clock.", Usage = "time [0..1|off]",
            Run = arg =>
            {
                var gm = Game.Instance;
                if (gm == null) return CommandResult.Ok("No game.", keepOpen: true, output: OutputType.System);
                if (arg.Length == 0)
                {
                    float now = gm.DayNight != null ? gm.DayNight.timeOfDay : 0f;
                    return CommandResult.Ok($"Time of day: {now:0.00}" + (gm.TimeOverride.HasValue ? " (frozen)" : ""),
                                            keepOpen: true, output: OutputType.System);
                }
                if (arg.Equals("off", System.StringComparison.OrdinalIgnoreCase))
                {
                    gm.SetTimeOverride(null);
                    return CommandResult.Ok("Time resumed (live clock).", keepOpen: true, output: OutputType.System);
                }
                if (float.TryParse(arg, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float t))
                {
                    float f = Mathf.Repeat(t, 1f);
                    gm.SetTimeOverride(f);
                    return CommandResult.Ok($"Time frozen at {f:0.00}.", keepOpen: true, output: OutputType.System);
                }
                return CommandResult.Bad("Usage: time [0..1|off]");
            },
        });
    }

    static string MapName(string s) => s == "cooldown" ? "AttackCooldown" : s;

    static RelayConnector Net()
    {
        var n = Object.FindFirstObjectByType<RelayConnector>();
        if (n == null) GameLog.Post(OutputType.System, "No network manager in the scene.");
        return n;
    }

    static string NearestSite()
    {
        var gm = Game.Instance; var lp = LocalPlayer.Instance;
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
