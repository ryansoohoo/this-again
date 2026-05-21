// Single front door for the command system: owns the registry and the active-scope bitmask, and exposes
// the scope API that gameplay calls when state changes (an encounter starts, a shop opens, …). The console
// talks only to this. Plain C# singleton — usable from both UI and gameplay without scene wiring.
public sealed class CommandRouter
{
    static CommandRouter instance;
    public static CommandRouter Instance => instance ??= new CommandRouter();

    const CommandScope DefaultScopes = CommandScope.Global | CommandScope.World | CommandScope.Inventory;

    public CommandRegistry Registry { get; } = new();
    public CommandScope Active { get; private set; } = DefaultScopes;

    public CommandResult Execute(string line) => Registry.Execute(line, Active);
    public string Suggest(string partial) => Registry.Suggest(partial, Active);

    public void Activate(CommandScope s) => Active |= s;
    public void Deactivate(CommandScope s) => Active &= ~(s & ~CommandScope.Global);   // Global can never be turned off

    // Gameplay seams. A real EncounterManager calls these on combat start/end; for now the `fight`/`flee`
    // demo commands do. Entering combat swaps the World command set for the Encounter one.
    public void EnterEncounter() { Deactivate(CommandScope.World); Activate(CommandScope.Encounter); }
    public void ExitEncounter() { Deactivate(CommandScope.Encounter); Activate(CommandScope.World); }

    // Restore the default scope set for a fresh play session (the static instance survives domain reloads).
    public void ResetScopes() => Active = DefaultScopes;
}
