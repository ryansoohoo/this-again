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

    [Header("Weapon attack shine (AllIn1 glow — needs the weapon material's GLOW_ON; run Tools > Minifantasy > Setup Weapon Shine)")]
    [SerializeField] float shinePeak = 12f;     // peak _Glow during the swing
    [SerializeField] AnimationCurve shineCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);  // glow vs lunge progress (peak at strike, fade out)
    static readonly int GlowId = Shader.PropertyToID("_Glow");
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

    // Brighten/shine the weapon layers during the swing by driving the AllIn1 _Glow per-renderer (the weapon
    // material needs GLOW_ON — set up by Tools > Minifantasy > Setup Weapon Shine). Peak + curve are tunable here;
    // the glow color/look is tunable in the AllIn1 material inspector. MaterialPropertyBlock keeps it per-ghost.
    void DriveShine(AttackState state, AttackTimeline tl)
    {
        float glow = 0f;
        if (state.phase == AttackPhase.Hit || state.phase == AttackPhase.FollowThrough)
            glow = shinePeak * Mathf.Max(0f, shineCurve.Evaluate(AttackLogic.LungeProgress(state, tl)));
        SetGlow(weaponFront, glow);
        SetGlow(weaponBack, glow);
    }

    void SetGlow(SpriteRenderer sr, float glow)
    {
        if (sr == null) return;
        _shineMpb ??= new MaterialPropertyBlock();
        sr.GetPropertyBlock(_shineMpb);
        _shineMpb.SetFloat(GlowId, glow);
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
