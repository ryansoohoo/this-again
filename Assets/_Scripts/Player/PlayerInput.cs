using UnityEngine;
using UnityEngine.InputSystem;

// Logic helper: reads the owning client's devices into a per-frame Intent (movement + attack edges + cursor).
// No state. LocalPlayer turns this into movement input and an AttackIntent.
public sealed class PlayerInput
{
    public struct Intent
    {
        public Vector2 dir;        // raw 8-way WASD (each axis -1/0/1); zero while typing
        public bool lmbDown;       // attack press edge
        public bool lmbHeld;       // attack held
        public bool lmbUp;         // attack release edge
        public bool rmbDown;       // feint edge
        public Vector2 cursorWorld;// mouse position in world space
        public int weaponSlot;     // 0-9 if a number key was pressed this frame, else -1
    }

    public Intent Read(Camera cam)
    {
        var result = new Intent();
        result.weaponSlot = -1;
        if (InputState.Typing) return result;          // command line open: no input

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed) result.dir.y += 1f;
            if (kb.sKey.isPressed) result.dir.y -= 1f;
            if (kb.aKey.isPressed) result.dir.x -= 1f;
            if (kb.dKey.isPressed) result.dir.x += 1f;

            for (int i = 0; i < 9; i++)
                if (kb[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame) { result.weaponSlot = i; break; }
            if (result.weaponSlot < 0 && kb.digit0Key.wasPressedThisFrame) result.weaponSlot = 9;
        }

        var mouse = Mouse.current;
        if (mouse != null)
        {
            result.lmbDown = mouse.leftButton.wasPressedThisFrame;
            result.lmbHeld = mouse.leftButton.isPressed;
            result.lmbUp = mouse.leftButton.wasReleasedThisFrame;
            result.rmbDown = mouse.rightButton.wasPressedThisFrame;
            if (cam != null)
            {
                Vector2 sp = mouse.position.ReadValue();
                Vector3 wp = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, Mathf.Abs(cam.transform.position.z)));
                result.cursorWorld = new Vector2(wp.x, wp.y);
            }
        }
        return result;
    }
}
