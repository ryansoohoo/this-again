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

    // Weapon shine drives the AllIn1 _Glow + _GlowColor per attack phase (weapon material needs GLOW_ON; run
    // Tools > Minifantasy > Setup Weapon Shine). NOTE: high glow + the scene Bloom = the halo spills onto the
    // character (bloom is screen-space). Keep these modest (or raise the Bloom threshold) to keep it weapon-only.
    [Header("Weapon shine — CHARGE (wind-up)")]
    [SerializeField] float chargeGlow = 2f;                                                  // peak _Glow while charging
    [SerializeField] Color chargeColor = new Color(0.7f, 0.85f, 1f);                         // cool build-up
    [SerializeField] AnimationCurve chargeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);     // vs wind-up progress (builds as you hold)
    [Header("Weapon shine — ATTACK (strike)")]
    [SerializeField] float attackGlow = 6f;                                                  // peak _Glow during the swing
    [SerializeField] Color attackColor = new Color(1f, 0.95f, 0.7f);                         // warm flash
    [SerializeField] AnimationCurve attackCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);  // vs lunge progress (flash at strike, fade)
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
        float glow = 0f; Color color = attackColor;
        switch (state.phase)
        {
            case AttackPhase.Anticipation:
            case AttackPhase.TapWindup:
                glow = chargeGlow * Mathf.Max(0f, chargeCurve.Evaluate(AttackLogic.AnticipationProgress(state, tl)));
                color = chargeColor;
                break;
            case AttackPhase.Hit:
            case AttackPhase.FollowThrough:
                glow = attackGlow * Mathf.Max(0f, attackCurve.Evaluate(AttackLogic.LungeProgress(state, tl)));
                color = attackColor;
                break;
        }
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
