using System;

// The command data model: one verb the player can type, tagged with the scope it's usable in. The single
// argument (when the verb takes one) is the whole trimmed remainder of the line after the keyword.
public enum ArgMode { None, Optional, Required }

public enum CommandStatus
{
    Ok,           // ran successfully
    Unknown,      // no command with that keyword exists in any scope
    Unavailable,  // the command exists but its scope isn't active right now
    BadUsage,     // matched, but a required argument was missing (or the handler rejected it)
}

public readonly struct CommandResult
{
    public readonly CommandStatus Status;
    public readonly string Message;   // user-facing text, shown in the chat popup (GameLog)
    public readonly bool KeepOpen;    // keep the console open for another command (combat, help, inventory)
    public readonly OutputType Output;// how the chat popup renders Message

    public CommandResult(CommandStatus status, string message = null, bool keepOpen = false, OutputType output = OutputType.Text)
    {
        Status = status; Message = message; KeepOpen = keepOpen; Output = output;
    }

    public static CommandResult Ok(string message = null, bool keepOpen = false, OutputType output = OutputType.Text) => new(CommandStatus.Ok, message, keepOpen, output);
    public static CommandResult Bad(string message) => new(CommandStatus.BadUsage, message);
}

public sealed class Command
{
    public string Keyword;                    // lowercase; may contain spaces ("create lobby")
    public string[] Aliases;                  // alternative keywords (lowercased on register)
    public CommandScope Scope = CommandScope.Global;
    public ArgMode Arg = ArgMode.None;
    public string Description;                // shown by `help`
    public string Usage;                      // e.g. "join <code>"; defaults to the keyword
    public Func<string, CommandResult> Run;   // arg is the trimmed remainder ("" when none)
}
