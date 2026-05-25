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

    // Fixed weapon attack FX — the SAME for every weapon (not per-keyframe data): a small glow on the Hit frame,
    // a darken-to-gray through Follow-through, nothing on the wind-up. Driven onto the weapon layers' AllIn1
    // _Glow + HSV via each renderer's per-INSTANCE material (sr.material), NOT a MaterialPropertyBlock — an MPB on
    // a SpriteRenderer clobbers its color (day/night tint). Material needs GLOW_ON + HSV_ON (Setup Weapon Shine).
    [Header("Weapon attack FX (same for all weapons)")]
    [SerializeField] float hitGlow = 1.5f;                  // small glow on the Hit frame
    [SerializeField] float followThroughBright = 0.5f;      // HSV brightness on follow-through (<1 = darker)
    [SerializeField] float followThroughSaturation = 0f;    // HSV saturation on follow-through (0 = gray)
    static readonly int GlowId = Shader.PropertyToID("_Glow");
    static readonly int HsvBrightId = Shader.PropertyToID("_HsvBright");
    static readonly int HsvSatId = Shader.PropertyToID("_HsvSaturation");

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
        DriveFx(state);
    }

    // Fixed by phase, same for all weapons: Hit → small glow; Follow-through → darken to gray; else neutral.
    // Per-instance material so it's per-ghost and never touches SpriteRenderer.color (day/night tint stays intact).
    void DriveFx(AttackState state)
    {
        if (!Application.isPlaying) return;   // sr.material instantiates — don't leak materials in edit mode
        float glow = 0f, bright = 1f, sat = 1f;
        if (state.phase == AttackPhase.Hit) glow = hitGlow;
        else if (state.phase == AttackPhase.FollowThrough) { bright = followThroughBright; sat = followThroughSaturation; }
        SetFx(weaponFront, glow, bright, sat);
        SetFx(weaponBack, glow, bright, sat);
    }

    void SetFx(SpriteRenderer sr, float glow, float bright, float sat)
    {
        if (sr == null) return;
        var m = sr.material;            // per-instance (created once); does NOT clobber the sprite's vertex color
        m.SetFloat(GlowId, glow);
        m.SetFloat(HsvBrightId, bright);
        m.SetFloat(HsvSatId, sat);
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
