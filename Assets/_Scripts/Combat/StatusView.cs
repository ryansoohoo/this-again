using UnityEngine;

// Visual: tints the player rig by active status effects. Driven each frame by GhostManager (remotes) or
// LocalPlayer (self) with the active-effect mask (StatusLogic.ActiveMask). No logic, no new GameObjects.
public sealed class StatusView : MonoBehaviour
{
    SpriteRenderer[] sprites;

    void Awake() => sprites = GetComponentsInChildren<SpriteRenderer>(true);

    // mask: one bit per StatusKind. Highest-priority active effect wins the tint; none = white.
    public void Render(byte mask)
    {
        Color c = Color.white;
        if ((mask & (1 << (int)StatusKind.Freeze)) != 0) c = new Color(0.6f, 0.8f, 1f);       // blue
        else if ((mask & (1 << (int)StatusKind.Poison)) != 0) c = new Color(0.6f, 1f, 0.6f);  // green
        else if ((mask & (1 << (int)StatusKind.HitStun)) != 0) c = new Color(1f, 0.7f, 0.7f); // red flash
        else if ((mask & (1 << (int)StatusKind.Slow)) != 0) c = new Color(0.8f, 0.8f, 0.95f); // dim
        if (sprites == null) return;
        for (int i = 0; i < sprites.Length; i++) if (sprites[i] != null) sprites[i].color = c;
    }
}
