using UnityEngine;

// Visual: derives Idle/Walk + 4-way facing from the replicated transform and drives the Animator. Reads the
// transform (data); writes only the animator. Runs on every client. Never touches movement state.
public sealed class PlayerView : MonoBehaviour
{
    [SerializeField] Animator animator;

    SpriteRenderer[] bodyRenderers;                 // all child sprites EXCEPT the "Shadow" child; day/night tints these
    Vector3 lastPos;
    Vector2 facing = new(1f, -1f);                  // default SE (down-right)
    static readonly int SpeedHash = Animator.StringToHash("Speed");
    static readonly int DirXHash = Animator.StringToHash("DirX");
    static readonly int DirYHash = Animator.StringToHash("DirY");

    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        lastPos = transform.position;

        var all = GetComponentsInChildren<SpriteRenderer>(true);
        var body = new System.Collections.Generic.List<SpriteRenderer>(all.Length);
        foreach (var sr in all)
            if (sr.gameObject.name != "Shadow" && sr.gameObject.name != "DmgBody"
                && sr.gameObject.name != "HitFxBody" && sr.gameObject.name != "TickFxBody"
                && !IsUnderAttackRig(sr.transform)) body.Add(sr);   // leave shadow + attack rig + hurt + status-FX sprites untouched
        bodyRenderers = body.ToArray();
    }

    void OnEnable()
    {
        if (animator != null) { animator.SetFloat(DirXHash, facing.x); animator.SetFloat(DirYHash, facing.y); }
    }

    void LateUpdate()
    {
        Vector3 pos = transform.position;
        Vector2 delta = (Vector2)(pos - lastPos);
        lastPos = pos;

        float speed = delta.magnitude / Mathf.Max(Time.deltaTime, 1e-5f);
        if (animator != null) animator.SetFloat(SpeedHash, speed);

        if (delta.sqrMagnitude > 1e-6f)
        {
            Vector2 d = delta.normalized;
            if (Mathf.Abs(d.y) < 0.2f) d.y -= 0.2f;     // bias near-horizontal toward facing the camera (down)
            facing = d.normalized;
            if (animator != null)
            {
                animator.SetFloat(DirXHash, facing.x);
                animator.SetFloat(DirYHash, facing.y);
            }
        }

        var dn = Game.Instance != null ? Game.Instance.DayNight : null;
        if (dn != null && bodyRenderers != null)
        {
            var c = dn.tint;
            for (int i = 0; i < bodyRenderers.Length; i++)
                if (bodyRenderers[i] != null) bodyRenderers[i].color = c;
        }
    }

    static bool IsUnderAttackRig(Transform t)
    {
        for (var p = t; p != null; p = p.parent) if (p.name == "AttackRig") return true;
        return false;
    }

    // Lets AttackView hide the idle/walk body during an attack (the Animator keeps running underneath).
    public void SetBodyVisible(bool visible)
    {
        if (bodyRenderers == null) return;
        for (int i = 0; i < bodyRenderers.Length; i++)
            if (bodyRenderers[i] != null) bodyRenderers[i].enabled = visible;
    }
}
