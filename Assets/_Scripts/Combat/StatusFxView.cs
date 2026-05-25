using UnityEngine;

// Visual: the victim's status FX, driven each frame by GhostManager (remotes) / LocalPlayer (self) with the
// active-effect mask. Two own child renderers so it never touches the body/weapon SpriteRenderer.color:
//   - HitFxBody: plays an effect's hitFx ONCE on the mask bit's rising edge (0->1).
//   - TickFxBody: while a bit is set, loops the highest-priority effect's tickFx at its authored period.
// Resolves defId -> StatusEffectAsset via Game.Instance.StatusCatalog.Visual.
public sealed class StatusFxView : MonoBehaviour
{
    [SerializeField] SpriteRenderer hitRenderer;    // own child "HitFxBody"
    [SerializeField] SpriteRenderer tickRenderer;   // own child "TickFxBody"

    ushort prevMask;
    int hitDefId = -1; float hitElapsed;
    int tickDefId = -1; float tickElapsed;

    void Awake()
    {
        if (hitRenderer != null) hitRenderer.enabled = false;
        if (tickRenderer != null) tickRenderer.enabled = false;
    }

    // Priority for which single effect's tick FX shows when several are active (higher first).
    static readonly StatusKind[] Priority = { StatusKind.Fire, StatusKind.Bleed, StatusKind.Poison, StatusKind.Freeze, StatusKind.Slow, StatusKind.Fear };

    public void Render(ushort mask)
    {
        var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        if (cat == null) return;

        // --- Layer B: one-shot on any bit that just turned on ---
        ushort rising = (ushort)(mask & ~prevMask);
        if (rising != 0)
        {
            for (int k = 0; k < Priority.Length; k++)
            {
                int id = (int)Priority[k];
                if ((rising & (1 << id)) == 0) continue;
                var fx = cat.Visual(id);
                if (fx != null && fx.hitFx != null && fx.hitFx.Length > 0) { hitDefId = id; hitElapsed = 0f; }
                break;
            }
        }
        prevMask = mask;
        DriveOneShot(cat);

        // --- Layer C: looping tick FX for the highest-priority active effect ---
        int activeTick = -1;
        for (int k = 0; k < Priority.Length; k++)
        {
            int id = (int)Priority[k];
            if ((mask & (1 << id)) == 0) continue;
            var fx = cat.Visual(id);
            if (fx != null && fx.tickFx != null && fx.tickFx.Length > 0) { activeTick = id; break; }
        }
        DriveTick(cat, activeTick);
    }

    void DriveOneShot(StatusCatalog cat)
    {
        if (hitRenderer == null) return;
        if (hitDefId < 0) { if (hitRenderer.enabled) hitRenderer.enabled = false; return; }
        var fx = cat.Visual(hitDefId);
        if (fx == null || fx.hitFx == null || fx.hitFx.Length == 0) { hitDefId = -1; hitRenderer.enabled = false; return; }
        hitElapsed += Time.deltaTime;
        int frame = (int)(hitElapsed * fx.hitFps);
        if (frame >= fx.hitFx.Length) { hitDefId = -1; hitRenderer.enabled = false; return; }   // one-shot done
        hitRenderer.enabled = true;
        hitRenderer.sprite = fx.hitFx[frame];
    }

    void DriveTick(StatusCatalog cat, int defId)
    {
        if (tickRenderer == null) return;
        if (defId != tickDefId) { tickDefId = defId; tickElapsed = 0f; }
        if (defId < 0) { if (tickRenderer.enabled) tickRenderer.enabled = false; return; }
        var fx = cat.Visual(defId);
        if (fx == null || fx.tickFx == null || fx.tickFx.Length == 0) { tickRenderer.enabled = false; return; }
        tickElapsed += Time.deltaTime;
        tickRenderer.enabled = true;
        int frame = Mathf.Clamp((int)(tickElapsed * fx.tickFps), 0, fx.tickFx.Length - 1);
        tickRenderer.sprite = fx.tickFx[frame];
        if (frame >= fx.tickFx.Length - 1) tickElapsed = 0f;   // loop
    }
}
