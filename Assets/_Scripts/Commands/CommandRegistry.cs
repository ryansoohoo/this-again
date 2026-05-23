using System.Collections.Generic;
using System.Text.RegularExpressions;

// Holds every command and resolves a typed line against the currently-active scopes. Pure logic with no
// Unity/scene dependencies, so it is unit-testable. Keywords may be multi-word and are matched
// longest-first ("create lobby" beats a hypothetical "create"); a keyword that matches only in an
// inactive scope yields Unavailable (so the UI can say "not right now") rather than Unknown.
public sealed class CommandRegistry
{
    readonly List<Command> commands = new();

    public IReadOnlyList<Command> All => commands;

    public void Register(Command c)
    {
        c.Keyword = c.Keyword.ToLowerInvariant();
        if (c.Aliases != null)
            for (int i = 0; i < c.Aliases.Length; i++) c.Aliases[i] = c.Aliases[i].ToLowerInvariant();
        commands.Add(c);
    }

    public CommandResult Execute(string line, CommandScope active)
    {
        string norm = Collapse(line);
        if (norm.Length == 0) return new CommandResult(CommandStatus.Unknown);
        string lower = norm.ToLowerInvariant();

        Command best = null;
        int bestLen = -1;
        bool bestActive = false;
        foreach (var c in commands)
        {
            foreach (var kw in Keywords(c))
            {
                if (!Matches(lower, kw, c.Arg)) continue;
                bool act = (c.Scope & active) != 0;
                // Prefer a longer (more specific) keyword; on a tie prefer the active one.
                if (kw.Length > bestLen || (kw.Length == bestLen && act && !bestActive))
                {
                    best = c; bestLen = kw.Length; bestActive = act;
                }
            }
        }

        if (best == null) return new CommandResult(CommandStatus.Unknown);
        if (!bestActive) return new CommandResult(CommandStatus.Unavailable, "You can't do that right now.");

        string arg = norm.Length > bestLen ? norm.Substring(bestLen).Trim() : "";
        if (best.Arg == ArgMode.Required && arg.Length == 0)
            return CommandResult.Bad("Usage: " + (best.Usage ?? best.Keyword));
        return best.Run(arg);
    }

    // The suffix that completes an unambiguous prefix among active commands, else "". The autocomplete
    // UI (not wired yet) renders this as a ghost and accepts it on Tab/Enter.
    public string Suggest(string partial, CommandScope active)
    {
        string s = Collapse(partial).ToLowerInvariant();
        if (s.Length == 0) return "";
        string match = null;
        foreach (var c in commands)
        {
            if ((c.Scope & active) == 0) continue;
            foreach (var kw in Keywords(c))
                if (kw != s && kw.StartsWith(s))
                {
                    if (match != null && match != kw) return "";   // ambiguous
                    match = kw;
                }
        }
        return match == null ? "" : match.Substring(s.Length);
    }

    static bool Matches(string lowerLine, string keyword, ArgMode arg)
    {
        if (arg == ArgMode.None) return lowerLine == keyword;
        return lowerLine == keyword || lowerLine.StartsWith(keyword + " ");
    }

    static IEnumerable<string> Keywords(Command c)
    {
        yield return c.Keyword;
        if (c.Aliases != null)
            foreach (var a in c.Aliases) yield return a;
    }

    static string Collapse(string s) => Regex.Replace(s.Trim(), @"\s+", " ");
}
