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

    public void Render(ushort mask)
    {
        var cat = Game.Instance != null ? Game.Instance.StatusCatalog : null;
        if (cat == null) return;

        // --- Layer B: one-shot on the highest-priority effect whose bit just turned on ---
        ushort rising = (ushort)(mask & ~prevMask);
        if (rising != 0)
        {
            int best = -1, bestPri = -1;
            for (int id = 0; id < 16; id++)
            {
                if ((rising & (1 << id)) == 0) continue;
                var fx = cat.Visual(id);
                if (fx == null || fx.hitFx == null || fx.hitFx.Length == 0) continue;
                if (fx.visualPriority > bestPri) { bestPri = fx.visualPriority; best = id; }
            }
            if (best >= 0) { hitDefId = best; hitElapsed = 0f; }
        }
        prevMask = mask;
        DriveOneShot(cat);

        // --- Layer C: looping tick FX for the highest-priority active effect that has one ---
        int tBest = -1, tPri = -1;
        for (int id = 0; id < 16; id++)
        {
            if ((mask & (1 << id)) == 0) continue;
            var fx = cat.Visual(id);
            if (fx == null || fx.tickFx == null || fx.tickFx.Length == 0) continue;
            if (fx.visualPriority > tPri) { tPri = fx.visualPriority; tBest = id; }
        }
        DriveTick(cat, tBest);
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
        tickRenderer.enabled = true;
        // One whole tick-FX animation per damage period, so 1 DoT tick == ~1 sprite-sheet cycle. The gameplay
        // period (periodSeconds) is the source of truth; non-DoTs (period 0) fall back to the authored tickFps.
        float loopDur = fx.periodSeconds > 0f ? fx.periodSeconds : fx.tickFx.Length / Mathf.Max(1f, fx.tickFps);
        tickElapsed += Time.deltaTime;
        if (tickElapsed >= loopDur) tickElapsed -= loopDur;   // loop once per period
        int frame = Mathf.Clamp((int)(tickElapsed / loopDur * fx.tickFx.Length), 0, fx.tickFx.Length - 1);
        tickRenderer.sprite = fx.tickFx[frame];
    }
}
