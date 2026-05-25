using UnityEngine;
using UnityEngine.InputSystem;

// The one place W/S/A/D maps to a movement vector. Shared by the per-frame intent (PlayerInput) and the
// fixed-tick prediction sample (PredictionSystem) so the two can't drift apart.
public static class WasdInput
{
    // Raw 8-way intent (each axis -1/0/1) from a known-non-null keyboard. No guards — the caller owns those.
    public static Vector2 Read(Keyboard kb)
    {
        Vector2 d = Vector2.zero;
        if (kb.wKey.isPressed) d.y += 1f;
        if (kb.sKey.isPressed) d.y -= 1f;
        if (kb.aKey.isPressed) d.x -= 1f;
        if (kb.dKey.isPressed) d.x += 1f;
        return d;
    }

    // Guarded: zero while the command line is open or there is no keyboard.
    public static Vector2 Read()
    {
        if (InputState.Typing) return Vector2.zero;
        var kb = Keyboard.current;
        return kb != null ? Read(kb) : Vector2.zero;
    }
}
