using UnityEngine;

// Visual: tints the player rig by the highest-priority active tinting effect, read from the catalog (data-driven).
// Driven each frame with the active-effect mask. When no tinting effect is active it must NOT touch the color
// (PlayerView/AttackView own the day/night tint). HitStun has no tint (DmgView shows the hurt sprite).
public sealed class StatusView : MonoBehaviour
{
    SpriteRenderer[] sprites;
    static readonly StatusKind[] Priority = { StatusKind.Freeze, StatusKind.Fire, StatusKind.Poison, StatusKind.Bleed, StatusKind.Slow, StatusKind.Fear };

    void Awake() => sprites = GetComponentsInChildren<SpriteRenderer>(true);

    public void Render(byte mask)
    {
        if (sprites == null) return;
        var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        if (cat == null) return;
        for (int k = 0; k < Priority.Length; k++)
        {
            int id = (int)Priority[k];
            if ((mask & (1 << id)) == 0) continue;
            var fx = cat.Visual(id);
            if (fx == null || fx.tintColor == Color.white) continue;   // white = no tint
            for (int i = 0; i < sprites.Length; i++) if (sprites[i] != null) sprites[i].color = fx.tintColor;
            return;
        }
        // no tinting effect active — leave color alone (do NOT write white)
    }
}
