using UnityEngine;

// Visual: tints the player rig by the highest-priority active tinting effect, read from the catalog (data-driven).
// Driven each frame with the active-effect mask. When no tinting effect is active it must NOT touch the color
// (PlayerView/AttackView own the day/night tint). HitStun has no tint (DmgView shows the hurt sprite).
public sealed class StatusView : MonoBehaviour
{
    SpriteRenderer[] sprites;

    void Awake() => sprites = GetComponentsInChildren<SpriteRenderer>(true);

    public void Render(ushort mask)
    {
        if (sprites == null) return;
        var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        if (cat == null) return;
        int best = -1, bestPri = -1;
        for (int id = 0; id < 16; id++)
        {
            if ((mask & (1 << id)) == 0) continue;
            var fx = cat.Visual(id);
            if (fx == null || fx.tintColor == Color.white) continue;   // white = no tint
            if (fx.visualPriority > bestPri) { bestPri = fx.visualPriority; best = id; }
        }
        if (best < 0) return;   // no tinting effect active — leave color alone (do NOT write white)
        var c = cat.Visual(best).tintColor;
        for (int i = 0; i < sprites.Length; i++) if (sprites[i] != null) sprites[i].color = c;
    }
}
