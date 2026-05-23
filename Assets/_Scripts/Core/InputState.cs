// Cross-cutting input gate: true while the text console is capturing keystrokes, so gameplay/camera input
// stays suppressed. Lives in Core (not UI) so Camera/Player can read it without depending on the console.
public static class InputState
{
    public static bool Typing;
}
