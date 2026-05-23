using UnityEngine;
using UnityEngine.InputSystem;

// Logic helper: reads the owning client's devices and returns movement intent. Holds only double-click
// timing state. Writes nothing itself — PlayerMovement applies the intent to the networked state.
public sealed class PlayerInput
{
    public struct Intent
    {
        public Vector2 dir;           // raw 8-way WASD direction (each axis -1/0/1); zero while typing
        public bool hasClickTarget;   // true on a double-left-click
        public Vector2 clickWorld;    // world point of the double-click
    }

    float lastClickTime = -1f;
    Vector2 lastClickPos;
    const float DoubleClickTime = 0.3f, DoubleClickPixels = 24f;

    public Intent Read(Camera cam)
    {
        var result = new Intent();
        if (InputState.Typing) return result;          // command line open: no movement, no clicks

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed) result.dir.y += 1f;
            if (kb.sKey.isPressed) result.dir.y -= 1f;
            if (kb.aKey.isPressed) result.dir.x -= 1f;
            if (kb.dKey.isPressed) result.dir.x += 1f;
        }

        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            float now = Time.unscaledTime;
            Vector2 sp = mouse.position.ReadValue();
            bool isDouble = now - lastClickTime <= DoubleClickTime && Vector2.Distance(sp, lastClickPos) <= DoubleClickPixels;
            lastClickTime = isDouble ? -1f : now;       // reset after a double so a 3rd click starts fresh
            lastClickPos = sp;
            if (isDouble && cam != null)
            {
                Vector3 wp = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, Mathf.Abs(cam.transform.position.z)));
                result.hasClickTarget = true;
                result.clickWorld = new Vector2(wp.x, wp.y);
            }
        }
        return result;
    }
}
