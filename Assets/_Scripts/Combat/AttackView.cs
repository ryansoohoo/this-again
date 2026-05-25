using UnityEngine;

// Visual for one player's attack. Driven externally (LocalPlayer calls Render for the self-ghost only in v1).
// Swaps the idle/walk body for the 3-layer attack rig, drives the frames, and rotates the pivot by the residual.
public sealed class AttackView : MonoBehaviour
{
    [SerializeField] PlayerView playerView;     // toggles the normal body
    [SerializeField] Transform pivot;           // AttackRig root (rotated by residual)
    [SerializeField] SpriteRenderer weaponBack;
    [SerializeField] SpriteRenderer body;
    [SerializeField] SpriteRenderer weaponFront;

    // Weapon shine: per-KEYFRAME intensity (TimedFrame.glow, authored on each attack's frames) × the attack's
    // glowColor, driven onto the weapon layers' AllIn1 _Glow/_GlowColor. Weapon material needs GLOW_ON (run
    // Tools > Minifantasy > Setup Weapon Shine). NOTE: high glow + the scene Bloom spills the halo onto the
    // character (bloom is screen-space) — keep glow modest or raise the Bloom threshold to keep it weapon-only.
    static readonly int GlowId = Shader.PropertyToID("_Glow");
    static readonly int GlowColorId = Shader.PropertyToID("_GlowColor");
    MaterialPropertyBlock _shineMpb;

    void Awake()
    {
        if (playerView == null) playerView = GetComponent<PlayerView>();
        SetRigActive(false);
    }

    public void Render(AttackState state, AttackDefinition def)
    {
        if (def == null || !AttackLogic.IsAttacking(state.phase))
        {
            SetRigActive(false);
            if (playerView != null) playerView.SetBodyVisible(true);
            return;
        }

        var tl = def.Timeline;
        int row = tl.directions[state.dirIndex].row;
        int i = row * def.columnsPerRow + AttackLogic.CurrentColumn(state, tl);

        if (playerView != null) playerView.SetBodyVisible(false);
        SetRigActive(true);
        SetFrame(body, def.bodyFrames, i);
        SetFrame(weaponFront, def.weaponFrontFrames, i);
        SetFrame(weaponBack, def.weaponBackFrames, i);
        // Rotate only the weapon layers toward the aim; the character body stays upright.
        float rot = def.rotateToAim ? state.residualDeg : 0f;
        if (pivot != null) pivot.localEulerAngles = Vector3.zero;
        if (weaponBack != null) weaponBack.transform.localEulerAngles = new Vector3(0f, 0f, rot);
        if (weaponFront != null) weaponFront.transform.localEulerAngles = new Vector3(0f, 0f, rot);
        Tint();
        DriveShine(state, tl);
    }

    // Drive the AllIn1 glow on the weapon layers per attack phase — a "charging" look during the wind-up and a
    // separate "attack" look during the strike — each with its own intensity/color/curve (authored above). The
    // weapon material needs GLOW_ON. Per-renderer MaterialPropertyBlock so it stays per-ghost and still batches.
    void DriveShine(AttackState state, AttackTimeline tl)
    {
        float glow = AttackLogic.CurrentGlow(state, tl);   // per-keyframe intensity authored on the attack's frames
        Color color = tl.glowColor.maxColorComponent > 0.01f ? tl.glowColor : Color.white;  // fallback for un-migrated assets
        SetShine(weaponFront, glow, color);
        SetShine(weaponBack, glow, color);
    }

    void SetShine(SpriteRenderer sr, float glow, Color color)
    {
        if (sr == null) return;
        _shineMpb ??= new MaterialPropertyBlock();
        sr.GetPropertyBlock(_shineMpb);
        _shineMpb.SetFloat(GlowId, glow);
        _shineMpb.SetColor(GlowColorId, color);
        sr.SetPropertyBlock(_shineMpb);
    }

    void SetFrame(SpriteRenderer sr, Sprite[] frames, int i)
    {
        if (sr == null) return;
        if (frames != null && i >= 0 && i < frames.Length && frames[i] != null) { sr.sprite = frames[i]; sr.enabled = true; }
        else sr.enabled = false;
    }

    void SetRigActive(bool on)
    {
        if (pivot != null && pivot.gameObject.activeSelf != on) pivot.gameObject.SetActive(on);
    }

    void Tint()
    {
        var dn = Game.Instance != null ? Game.Instance.DayNight : null;
        if (dn == null) return;
        if (weaponBack != null) weaponBack.color = dn.tint;
        if (body != null) body.color = dn.tint;
        if (weaponFront != null) weaponFront.color = dn.tint;
    }
}
