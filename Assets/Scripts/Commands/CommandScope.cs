using System;

// What game state a command belongs to. The router keeps a bitmask of the currently-active scopes; a
// command is usable only while its scope is active. Global is always on. Add scopes here as systems grow
// (e.g. Dialogue, Shop, Trade) — this enum is the single place that defines the contexts commands live in.
[Flags]
public enum CommandScope
{
    None      = 0,
    Global    = 1 << 0,   // always available (help, settings)
    World     = 1 << 1,   // free-roam / exploration (connect, travel, look)
    Encounter = 1 << 2,   // active combat (attack, flee, abilities)
    Inventory = 1 << 3,   // item management (available both in the world and in combat)
}
