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
    }

    readonly Dictionary<ulong, Ghost> ghosts = new();
    readonly List<ulong> tmpRemove = new();

    public Transform SelfGhost { get; private set; }
    public bool SelfInInstance { get; private set; }

    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

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
                g = new Ghost { tf = go.transform, fromPos = pos, toPos = pos, t = 1f, dur = dur };
                ghosts[e.id] = g;
                if (e.id == localId) SelfGhost = g.tf;
            }

            if (snap) { g.fromPos = pos; g.toPos = pos; g.t = 1f; g.tf.position = pos; }
            else { g.fromPos = CurrentPos(g); g.toPos = pos; g.t = 0f; g.dur = dur; }
            g.seen = true;

            if (e.id == localId) SelfInInstance = (e.flags & SnapshotEntry.InInstanceBit) != 0;
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

    // Interpolate in Update (not LateUpdate): Game.LateUpdate's follow-camera reads these positions, so the
    // ghosts must already be moved for this frame before any LateUpdate runs.
    void Update()
    {
        float dt = Time.deltaTime;
        foreach (var g in ghosts.Values)
        {
            if (g.dur > 1e-5f && g.t < 1f) g.t = Mathf.Min(1f, g.t + dt / g.dur);
            g.tf.position = CurrentPos(g);
        }
    }
}
