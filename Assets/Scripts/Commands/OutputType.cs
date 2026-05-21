// The kind of content a chat/output entry carries, so the popup can render each differently — color and
// icon now, richer widgets later (item rows with sprites, encounter cards, ability tooltips). Extend as
// systems grow. CommandResult tags its message with one of these; gameplay posts typed entries to GameLog.
public enum OutputType
{
    Text,        // plain narration / default
    System,      // notices & errors ("you can't do that right now", "unknown command")
    Command,     // echo of what the player typed
    Encounter,   // combat events
    Inventory,   // item listings
}
