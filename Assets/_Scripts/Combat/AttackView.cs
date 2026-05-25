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

    // Weapon shine: per-keyframe intensity (TimedFrame.glow) × the attack's glowColor, driven onto the weapon
    // layers' AllIn1 _Glow/_GlowColor. Uses each renderer's per-INSTANCE material (sr.material), NOT a
    // MaterialPropertyBlock — an MPB on a SpriteRenderer clobbers its color (the day/night tint); a material
    // instance leaves vertex color alone. Weapon material needs GLOW_ON (Tools > Minifantasy > Setup Weapon Shine).
    static readonly int GlowId = Shader.PropertyToID("_Glow");
    static readonly int GlowColorId = Shader.PropertyToID("_GlowColor");

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

    // Reads the current keyframe's glow (authored on the attack's TimedFrames) and drives the weapon layers' glow.
    // Per-instance material so it's per-ghost and never touches SpriteRenderer.color (day/night tint stays intact).
    void DriveShine(AttackState state, AttackTimeline tl)
    {
        if (!Application.isPlaying) return;   // sr.material instantiates — don't leak materials in edit mode
        float glow = AttackLogic.CurrentGlow(state, tl);
        Color color = tl.glowColor.maxColorComponent > 0.01f ? tl.glowColor : Color.white;
        SetGlow(weaponFront, glow, color);
        SetGlow(weaponBack, glow, color);
    }

    void SetGlow(SpriteRenderer sr, float glow, Color color)
    {
        if (sr == null) return;
        var m = sr.material;            // per-instance (created once); does NOT clobber the sprite's vertex color
        m.SetFloat(GlowId, glow);
        m.SetColor(GlowColorId, color);
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
