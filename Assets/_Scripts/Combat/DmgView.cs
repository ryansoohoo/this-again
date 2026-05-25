using UnityEngine;

// Visual: plays the directional hurt animation while HitStun is active. Manual sprite-swap (like AttackView) so it
// needs no Animator-controller changes: it hides the normal idle/walk body and shows the Dmg frames for the
// victim's current 4-way facing (read from the Animator's DirX/DirY), advancing then HOLDING the last frame for the
// whole stun. Driven every frame by GhostManager (remotes) and LocalPlayer (self) with the active-effect mask.
public sealed class DmgView : MonoBehaviour
{
    [SerializeField] PlayerView playerView;       // toggles the normal body
    [SerializeField] SpriteRenderer dmgRenderer;  // the hurt sprite (own child "DmgBody"; the Animator never drives it)
    [SerializeField] Animator animator;           // read DirX/DirY for facing
    [SerializeField] Sprite[] frames;             // 16 = 4 dirs (SE,SW,NE,NW) x 4 frames, row-major (Dmg_0..15)
    [SerializeField] int columns = 4;
    [SerializeField] float fps = 12f;

    static readonly int DirXHash = Animator.StringToHash("DirX");
    static readonly int DirYHash = Animator.StringToHash("DirY");

    bool active;
    float elapsed;

    void Awake()
    {
        if (playerView == null) playerView = GetComponent<PlayerView>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (dmgRenderer != null) dmgRenderer.enabled = false;
    }

    // mask: active-effect bitmask (StatusLogic.ActiveMask). Call every frame; pass 0 to clear.
    public void Render(ushort mask)
    {
        bool hurt = (mask & (1 << (int)StatusKind.HitStun)) != 0;
        if (!hurt) { if (active) Stop(); return; }
        if (frames == null || frames.Length < columns || dmgRenderer == null) return;

        if (!active) { active = true; elapsed = 0f; }
        else elapsed += Time.deltaTime;

        // Hide the body EVERY frame so AttackView's idle "body visible" restore can't fight us.
        if (playerView != null) playerView.SetBodyVisible(false);
        dmgRenderer.enabled = true;

        int frame = Mathf.Min(columns - 1, (int)(elapsed * fps));   // advance then hold the last frame for the rest of the stun
        int i = Row() * columns + frame;
        if (i >= 0 && i < frames.Length && frames[i] != null) dmgRenderer.sprite = frames[i];

        var dn = Game.Instance != null ? Game.Instance.DayNight : null;
        if (dn != null) dmgRenderer.color = dn.tint;   // day/night tint, like the body (wins over StatusView on this renderer)
    }

    void Stop()
    {
        active = false;
        if (dmgRenderer != null) dmgRenderer.enabled = false;
        if (playerView != null) playerView.SetBodyVisible(true);
    }

    // Facing → sheet row: SE=0, SW=1, NE=2, NW=3 (matches DirPos in CharacterAnimBuildTool and the Dmg sheet rows).
    int Row()
    {
        if (animator == null) return 0;
        float dx = animator.GetFloat(DirXHash), dy = animator.GetFloat(DirYHash);
        return (dx > 0f ? 0 : 1) + (dy > 0f ? 2 : 0);
    }
}
