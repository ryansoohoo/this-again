using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// Client-side ghost lifecycle + interpolation. Consumes targeted snapshots from ReplicationHub and renders
// every player it is told about (including the local player) as a plain, non-networked GameObject. PlayerView
// on the ghost derives walk/idle + facing from the interpolated motion, exactly as it did from NetworkTransform.
public sealed class GhostManager : MonoBehaviour
{
    public static GhostManager Instance { get; private set; }

    [SerializeField] GameObject ghostPrefab;   // visual-only player rig (Ghost.prefab); Inspector-wired

    sealed class Ghost
    {
        public Transform tf;
        public Vector2 fromPos, toPos;
        public float t, dur;
        public bool seen;
        public AttackView attackView;   // remote attack rig (the self ghost's is driven by LocalPlayer instead)
        public bool attacking;
        public byte weaponId, poseByte;
        public ushort residual;
    }

    readonly Dictionary<ulong, Ghost> ghosts = new();
    readonly List<ulong> tmpRemove = new();

    public Transform SelfGhost { get; private set; }
    public bool SelfInInstance { get; private set; }

    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

    // Reliable attack transition events for visible attackers. Remote rigs render from the snapshot pose; these
    // guarantee no transition is missed and are the SFX/VFX + future-effects seam (Struck especially).
    public void ApplyAttackEvents(AttackEvent[] events, ulong localId) { }

    public void Apply(SnapshotEntry[] entries, ulong localId)
    {
        foreach (var g in ghosts.Values) g.seen = false;

        var cfg = Game.Instance != null ? Game.Instance.ReplicationCfg : null;
        float dur = 1f / (cfg != null ? Mathf.Max(1, cfg.snapshotHz) : 15);

        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            var pos = new Vector2(e.x, e.y);
            bool snap = (e.flags & SnapshotEntry.SnapBit) != 0;

            if (!ghosts.TryGetValue(e.id, out var g))
            {
                if (ghostPrefab == null) continue;
                var go = Instantiate(ghostPrefab, pos, Quaternion.identity);
                g = new Ghost { tf = go.transform, fromPos = pos, toPos = pos, t = 1f, dur = dur, attackView = go.GetComponent<AttackView>() };
                ghosts[e.id] = g;
                if (e.id == localId) SelfGhost = g.tf;
            }

            bool predictedSelf = e.id == localId && LocalPlayer.Instance != null && LocalPlayer.Instance.Prediction.Active;
            if (predictedSelf) { /* PredictionSystem drives the self transform; ignore snapshot interp for self */ }
            else if (snap) { g.fromPos = pos; g.toPos = pos; g.t = 1f; g.tf.position = pos; }
            else { g.fromPos = CurrentPos(g); g.toPos = pos; g.t = 0f; g.dur = dur; }
            g.seen = true;
            if (e.id != localId)   // remotes render from the replicated pose; the self rig is LocalPlayer's job
            {
                g.attacking = (e.flags & SnapshotEntry.AttackingBit) != 0;
                g.weaponId = e.weaponId; g.poseByte = e.pose; g.residual = e.residual;
            }

            if (e.id == localId) SelfInInstance = (e.flags & SnapshotEntry.SelfBit) != 0;
        }

        tmpRemove.Clear();
        foreach (var kv in ghosts) if (!kv.Value.seen) tmpRemove.Add(kv.Key);
        for (int i = 0; i < tmpRemove.Count; i++)
        {
            var g = ghosts[tmpRemove[i]];
            if (g.tf == SelfGhost) SelfGhost = null;
            Destroy(g.tf.gameObject);
            ghosts.Remove(tmpRemove[i]);
        }
    }

    static Vector2 CurrentPos(Ghost g) => Vector2.Lerp(g.fromPos, g.toPos, Mathf.Clamp01(g.t));

    const float MoveEps = 1e-6f;   // sq-distance below which a ghost is treated as not-moving between snapshots

    // Count of same-room remotes (every ghost except self). In-instance AOI is region-only, so all ghosts are
    // roommates. The predictor calls this to size its buffer before FillRoommateBodies.
    public int RoommateCount(ulong selfId)
    {
        int n = 0;
        foreach (var kv in ghosts) if (kv.Key != selfId) n++;
        return n;
    }

    // Fill buf[0..RoommateCount) with one CollisionBody per same-room remote, for the owner's collision prediction:
    //   pos     = CurrentPos(g) — the RENDERED (interpolated, on-screen) position, so you stop when sprites touch
    //   radius  = the caller's collision radius (uniform, same value the server uses)
    //   invMass = 1 if the ghost moved between its last two snapshots (a "mover"), else 0 (pinned) — mirrors the
    //             server's mover-yields weighting symmetrically. Order is arbitrary; ResolveOne id-sorts internally.
    public void FillRoommateBodies(ulong selfId, float radius, CollisionBody[] buf)
    {
        int k = 0;
        foreach (var kv in ghosts)
        {
            if (kv.Key == selfId) continue;
            if (k >= buf.Length) break;
            var g = kv.Value;
            float invMass = (g.toPos - g.fromPos).sqrMagnitude > MoveEps ? 1f : 0f;
            buf[k++] = new CollisionBody { id = kv.Key, pos = CurrentPos(g), radius = radius, invMass = invMass };
        }
    }

    // Interpolate in Update (not LateUpdate): Game.LateUpdate's follow-camera reads these positions, so the
    // ghosts must already be moved for this frame before any LateUpdate runs.
    void Update()
    {
        float dt = Time.deltaTime;
        bool predicting = LocalPlayer.Instance != null && LocalPlayer.Instance.Prediction.Active;
        foreach (var g in ghosts.Values)
        {
            bool isSelf = g.tf == SelfGhost;
            if (!(predicting && isSelf))   // PredictionSystem positions the self-ghost while predicting
            {
                if (g.dur > 1e-5f && g.t < 1f) g.t = Mathf.Min(1f, g.t + dt / g.dur);
                g.tf.position = CurrentPos(g);
            }
            if (!isSelf) RenderAttack(g);   // remotes render the replicated attack pose; LocalPlayer drives self
        }
    }

    // Drive a remote ghost's AttackView from its last replicated pose. Idle -> Render(default, null) hides the rig.
    void RenderAttack(Ghost g)
    {
        if (g.attackView == null) return;
        if (!g.attacking) { g.attackView.Render(default, null); return; }
        var cat = Game.Instance != null ? Game.Instance.WeaponCatalog : null;
        var def = cat != null ? cat.Get(g.weaponId) : null;
        AttackPose.Unpack(g.poseByte, out var phase, out var frame, out var dir);
        Vector2 canonical = (def != null && def.directions != null && dir < def.directions.Length)
            ? def.directions[dir].canonicalDir : Vector2.right;
        var st = new AttackState
        {
            phase = phase, frameIndex = frame, dirIndex = dir,
            residualDeg = Vector2.SignedAngle(canonical, AimQuant.Decode(g.residual)),
        };
        g.attackView.Render(st, def);
    }
}
