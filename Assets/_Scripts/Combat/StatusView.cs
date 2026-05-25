using UnityEngine;

// Visual: tints the player rig by active status effects. Driven each frame by GhostManager (remotes) or
// LocalPlayer (self) with the active-effect mask (StatusLogic.ActiveMask). No logic, no new GameObjects.
public sealed class StatusView : MonoBehaviour
{
    SpriteRenderer[] sprites;

    void Awake() => sprites = GetComponentsInChildren<SpriteRenderer>(true);

    // mask: one bit per StatusKind. Highest-priority tinting effect wins. When NO tinting effect is active we must
    // NOT touch the color — PlayerView/AttackView own it (day/night tint), and writing white here would wipe their
    // darkening (it previously left the whole attack rig full-bright, since nothing re-darkens the rig after us).
    public void Render(byte mask)
    {
        if (sprites == null) return;
        Color c;
        if ((mask & (1 << (int)StatusKind.Freeze)) != 0) c = new Color(0.6f, 0.8f, 1f);       // blue
        else if ((mask & (1 << (int)StatusKind.Poison)) != 0) c = new Color(0.6f, 1f, 0.6f);  // green
        else if ((mask & (1 << (int)StatusKind.Slow)) != 0) c = new Color(0.8f, 0.8f, 0.95f); // dim
        else return;   // no tint (idle, or HitStun-only which DmgView handles) — leave day/night alone
        for (int i = 0; i < sprites.Length; i++) if (sprites[i] != null) sprites[i].color = c;
    }
}
