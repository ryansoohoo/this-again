using NUnit.Framework;

// EditMode tests for the pure command-resolution logic: scope filtering, longest-keyword matching,
// argument enforcement, and prefix suggestion. No Unity scene/play-mode needed.
public class CommandRegistryTests
{
    const CommandScope World = CommandScope.Global | CommandScope.World;
    const CommandScope Combat = CommandScope.Global | CommandScope.Encounter;

    static CommandRegistry MakeRegistry()
    {
        var r = new CommandRegistry();
        r.Register(new Command { Keyword = "create lobby", Scope = CommandScope.World, Arg = ArgMode.None, Run = _ => CommandResult.Ok() });
        r.Register(new Command { Keyword = "join", Scope = CommandScope.World, Arg = ArgMode.Required, Run = a => CommandResult.Ok(a) });
        r.Register(new Command { Keyword = "attack", Scope = CommandScope.Encounter, Arg = ArgMode.Optional, Run = _ => CommandResult.Ok() });
        return r;
    }

    [Test]
    public void NoArgCommand_InActiveScope_IsOk()
    {
        Assert.AreEqual(CommandStatus.Ok, MakeRegistry().Execute("create lobby", World).Status);
    }

    [Test]
    public void UnknownKeyword_IsUnknown()
    {
        Assert.AreEqual(CommandStatus.Unknown, MakeRegistry().Execute("teleport", World).Status);
    }

    [Test]
    public void KnownCommand_InInactiveScope_IsUnavailable()
    {
        Assert.AreEqual(CommandStatus.Unavailable, MakeRegistry().Execute("attack", World).Status);
    }

    [Test]
    public void KnownCommand_InActiveScope_IsOk()
    {
        Assert.AreEqual(CommandStatus.Ok, MakeRegistry().Execute("attack", Combat).Status);
    }

    [Test]
    public void RequiredArgMissing_IsBadUsage()
    {
        Assert.AreEqual(CommandStatus.BadUsage, MakeRegistry().Execute("join", World).Status);
    }

    [Test]
    public void RequiredArgGiven_PassesArgThrough()
    {
        var res = MakeRegistry().Execute("join ABCDE", World);
        Assert.AreEqual(CommandStatus.Ok, res.Status);
        Assert.AreEqual("ABCDE", res.Message);   // arg preserved in original case
    }

    [Test]
    public void Matching_IsCaseInsensitive()
    {
        Assert.AreEqual(CommandStatus.Ok, MakeRegistry().Execute("CREATE LOBBY", World).Status);
    }

    [Test]
    public void Suggest_CompletesUnambiguousPrefix_InScope()
    {
        Assert.AreEqual("ttack", MakeRegistry().Suggest("a", Combat));
    }

    [Test]
    public void Suggest_IgnoresInactiveScopes()
    {
        Assert.AreEqual("", MakeRegistry().Suggest("a", World));
    }

    [Test]
    public void Suggest_AmbiguousPrefix_ReturnsEmpty()
    {
        var r = MakeRegistry();
        r.Register(new Command { Keyword = "attune", Scope = CommandScope.Encounter, Arg = ArgMode.None, Run = _ => CommandResult.Ok() });
        Assert.AreEqual("", r.Suggest("a", Combat));   // "attack" and "attune" both match -> ambiguous
    }

    [Test]
    public void Suggest_ExactFullWord_ReturnsEmpty()
    {
        Assert.AreEqual("", MakeRegistry().Suggest("attack", Combat));   // already complete
    }

    [Test]
    public void Suggest_EmptyInput_ReturnsEmpty()
    {
        Assert.AreEqual("", MakeRegistry().Suggest("", Combat));
    }
}
