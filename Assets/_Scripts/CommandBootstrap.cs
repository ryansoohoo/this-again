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
            Run = _ =>
            {
                var lp = LocalPlayer.Instance;
                if (lp == null) return CommandResult.Bad("No player yet.");
                var mirror = lp.inventoryMirror;
                var weapons = Game.Instance != null ? Game.Instance.WeaponCatalog : null;
                var consumables = Game.Instance != null ? Game.Instance.ConsumableCatalog : null;
                var sb = new StringBuilder();
                bool any = false;
                for (int i = 0; i < mirror.Length; i++)
                {
                    var s = mirror[i];
                    if (s.IsEmpty) continue;
                    any = true;
                    string name = s.kind == ItemKind.Weapon
                        ? (weapons != null && weapons.Get(s.id) != null ? weapons.Get(s.id).name : $"weapon#{s.id}")
                        : (consumables != null && consumables.Get(s.id) != null ? consumables.Get(s.id).displayName : $"consumable#{s.id}");
                    char tag = s.kind == ItemKind.Weapon ? 'W' : 'C';
                    if (s.count > 1) sb.AppendLine($"[{i + 1}] {name} x{s.count} ({tag})");
                    else sb.AppendLine($"[{i + 1}] {name} ({tag})");
                }
                if (!any) return CommandResult.Ok("Your inventory is empty.", keepOpen: true, output: OutputType.Inventory);
                // v1 doesn't surface the equipped marker — the server's last `equip` confirmation log is the signal.
                return CommandResult.Ok(sb.ToString().TrimEnd(), keepOpen: true, output: OutputType.Inventory);
            },
        });

        r.Register(new Command
        {
            Keyword = "give", Scope = CommandScope.Inventory, Arg = ArgMode.Required,
            Description = "(host) Give yourself an item. Usage: give weapon|consumable <id|name> [count]",
            Usage = "give weapon|consumable <id|name> [count]",
            Run = arg =>
            {
                var hub = ReplicationHub.Instance;
                if (hub == null || !hub.IsHost) return CommandResult.Bad("give is host-only.");
                var parts = arg.Split(new[] { ' ' }, 3, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return CommandResult.Bad("Usage: give weapon|consumable <id|name> [count]");

                ItemKind kind;
                switch (parts[0].ToLowerInvariant())
                {
                    case "w": case "weapon": kind = ItemKind.Weapon; break;
                    case "c": case "consumable": kind = ItemKind.Consumable; break;
                    default: return CommandResult.Bad("First arg must be 'weapon' or 'consumable'.");
                }

                if (!TryResolveItemId(kind, parts[1], out byte id, out string resolveReason))
                    return CommandResult.Bad(resolveReason);

                byte count = 1;
                if (parts.Length == 3 && (!byte.TryParse(parts[2], out count) || count == 0))
                    return CommandResult.Bad("Count must be 1..255.");

                hub.GiveSelfServerRpc((byte)kind, id, count);
                return CommandResult.Ok(keepOpen: true);
            },
        });

        r.Register(new Command
        {
            Keyword = "equip", Scope = CommandScope.Inventory, Arg = ArgMode.Required,
            Description = "Equip a weapon by slot number or name. Overworld only.",
            Usage = "equip <slot|name>",
            Run = arg =>
            {
                var lp = LocalPlayer.Instance;
                var hub = ReplicationHub.Instance;
                if (lp == null || hub == null) return CommandResult.Bad("Not connected.");
                if (!TryResolveSlotArg(lp.inventoryMirror, arg, out int slotIndex, out string reason))
                    return CommandResult.Bad(reason);
                hub.EquipRequestServerRpc(slotIndex);
                return CommandResult.Ok(keepOpen: true);
            },
        });

        r.Register(new Command
        {
            Keyword = "use", Scope = CommandScope.Inventory, Arg = ArgMode.Required,
            Description = "Use a consumable by slot number or name.",
            Usage = "use <slot|name>",
            Run = arg =>
            {
                var lp = LocalPlayer.Instance;
                var hub = ReplicationHub.Instance;
                if (lp == null || hub == null) return CommandResult.Bad("Not connected.");
                if (!TryResolveSlotArg(lp.inventoryMirror, arg, out int slotIndex, out string reason))
                    return CommandResult.Bad(reason);
                hub.UseRequestServerRpc(slotIndex);
                return CommandResult.Ok(keepOpen: true);
            },
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
                return CommandResult.Bad("'enchant' is being rewired for the new inventory system — see spec §8 follow-up.");
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

    // Resolves "3" → slot 2 (0-based), or "iron sword" → first matching slot by item name. Returns -1 on miss.
    static bool TryResolveSlotArg(InventorySlot[] mirror, string arg, out int slotIndex, out string reason)
    {
        slotIndex = -1; reason = null;
        arg = arg.Trim();
        if (int.TryParse(arg, out int n)) {
            if (n < 1 || n > mirror.Length) { reason = $"Slot must be 1..{mirror.Length}."; return false; }
            slotIndex = n - 1;
            if (mirror[slotIndex].IsEmpty) { reason = "That slot is empty."; return false; }
            return true;
        }
        // Name match — walk the mirror, look up display names via catalogs
        var gm = Game.Instance;
        int found = -1, hits = 0;
        for (int i = 0; i < mirror.Length; i++)
        {
            var s = mirror[i];
            if (s.IsEmpty) continue;
            string name = null;
            if (s.kind == ItemKind.Weapon && gm != null && gm.WeaponCatalog != null && gm.WeaponCatalog.Get(s.id) != null)
                name = gm.WeaponCatalog.Get(s.id).name;
            else if (s.kind == ItemKind.Consumable && gm != null && gm.ConsumableCatalog != null && gm.ConsumableCatalog.Get(s.id) != null)
            {
                var d = gm.ConsumableCatalog.Get(s.id);
                name = d.displayName ?? d.name;
            }
            if (name == null) continue;
            if (string.Equals(name, arg, System.StringComparison.OrdinalIgnoreCase)
             || name.IndexOf(arg, System.StringComparison.OrdinalIgnoreCase) >= 0)
            { found = i; hits++; }
        }
        if (hits == 0) { reason = $"No item matches '{arg}'."; return false; }
        if (hits > 1) { reason = $"Multiple items match '{arg}'. Be more specific."; return false; }
        slotIndex = found; return true;
    }

    static bool TryResolveItemId(ItemKind kind, string idOrName, out byte id, out string reason)
    {
        id = 0; reason = null;
        if (byte.TryParse(idOrName, out id)) return true;   // numeric id always wins

        var gm = Game.Instance;
        if (gm == null) { reason = "No catalog available."; return false; }

        if (kind == ItemKind.Weapon)
        {
            var c = gm.WeaponCatalog;
            if (c == null || c.weapons == null) { reason = "WeaponCatalog not wired."; return false; }
            int found = -1, hits = 0;
            for (int i = 0; i < c.weapons.Length; i++)
            {
                var w = c.weapons[i];
                if (w == null) continue;
                if (string.Equals(w.name, idOrName, System.StringComparison.OrdinalIgnoreCase)
                 || w.name.IndexOf(idOrName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                { found = i; hits++; }
            }
            if (hits == 0) { reason = $"No weapon matches '{idOrName}'."; return false; }
            if (hits > 1) { reason = $"Multiple weapons match '{idOrName}'. Be more specific."; return false; }
            id = (byte)found; return true;
        }
        else // Consumable
        {
            var c = gm.ConsumableCatalog;
            if (c == null || c.entries == null) { reason = "ConsumableCatalog not wired."; return false; }
            int found = -1, hits = 0;
            for (int i = 0; i < c.entries.Length; i++)
            {
                var d = c.entries[i];
                if (d == null) continue;
                string n = d.displayName ?? d.name;
                if (string.Equals(n, idOrName, System.StringComparison.OrdinalIgnoreCase)
                 || n.IndexOf(idOrName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                { found = i; hits++; }
            }
            if (hits == 0) { reason = $"No consumable matches '{idOrName}'."; return false; }
            if (hits > 1) { reason = $"Multiple consumables match '{idOrName}'. Be more specific."; return false; }
            id = (byte)found; return true;
        }
    }
}
