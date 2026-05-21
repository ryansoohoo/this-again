using System;
using System.Collections.Generic;

// One typed entry in the chat/output log. Text now; a Sprite/icon and richer payload can be added here
// later (the ChatPopup switches on Type to choose how to render an entry).
public readonly struct OutputEntry
{
    public readonly OutputType Type;
    public readonly string Text;
    public OutputEntry(OutputType type, string text) { Type = type; Text = text; }
}

// Central output bus: any system posts typed entries; the ChatPopup renders them. Decoupled from the UI
// (event-based) and keeps a capped backlog so the popup can show recent history when it (re)appears.
public static class GameLog
{
    const int MaxHistory = 200;
    static readonly List<OutputEntry> history = new();

    public static IReadOnlyList<OutputEntry> History => history;
    public static event Action<OutputEntry> Posted;

    public static void Post(OutputType type, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var entry = new OutputEntry(type, text);
        history.Add(entry);
        if (history.Count > MaxHistory) history.RemoveAt(0);
        Posted?.Invoke(entry);
    }

    public static void Clear()
    {
        history.Clear();
    }
}
