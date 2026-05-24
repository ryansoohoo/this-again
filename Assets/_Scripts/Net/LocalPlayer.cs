using Unity.Netcode;
using UnityEngine;

// The local-player facade (client): reads input and forwards intent to the ReplicationHub, and exposes the
// same surface the rest of the game used on PlayerMovement.LocalInstance (CurrentCell / InInstance / Halt /
// RequestEnterInstance / RequestLeaveInstance). The visible "self" is GhostManager's self-ghost.
public sealed class LocalPlayer : MonoBehaviour
{
    public static LocalPlayer Instance { get; private set; }

    readonly PlayerInput input = new();
    Vector2 lastSent = new(float.NaN, float.NaN);

    readonly PredictionSystem prediction = new();
    public PredictionSystem Prediction => prediction;
    bool wasInInstance;

    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

    bool Ready => ReplicationHub.Instance != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;

    public bool InInstance => GhostManager.Instance != null && GhostManager.Instance.SelfInInstance;

    public Vector2? SelfWorldPos
    {
        get
        {
            if (prediction.Active) return prediction.RenderedPos;
            var self = GhostManager.Instance != null ? GhostManager.Instance.SelfGhost : null;
            return self != null ? (Vector2?)(Vector2)self.position : null;
        }
    }

    public Vector2Int CurrentCell()
    {
        var gm = Game.Instance;
        if (gm == null) return Vector2Int.zero;
        if (prediction.Active) return gm.WorldToCell(prediction.RenderedPos);
        var self = GhostManager.Instance != null ? GhostManager.Instance.SelfGhost : null;
        return self != null ? gm.WorldToCell(self.position) : Vector2Int.zero;
    }

    public void Halt()
    {
        if (!Ready) return;
        lastSent = Vector2.zero;
        ReplicationHub.Instance.SubmitInputRpc(Vector2.zero);
        ReplicationHub.Instance.HaltRpc();
    }

    public void RequestEnterInstance(Vector2Int siteCell)
    {
        if (!Ready || InInstance) return;
        lastSent = Vector2.zero;
        ReplicationHub.Instance.SubmitInputRpc(Vector2.zero);
        ReplicationHub.Instance.EnterInstanceRpc(siteCell.x, siteCell.y);
    }

    public void RequestLeaveInstance()
    {
        if (!Ready || !InInstance) return;
        lastSent = Vector2.zero;
        ReplicationHub.Instance.SubmitInputRpc(Vector2.zero);
        ReplicationHub.Instance.LeaveInstanceRpc();
    }

    void Update()
    {
        if (!Ready) return;
        if (prediction.Active) prediction.Decay(Time.deltaTime);
        var cam = Game.Instance != null ? Game.Instance.Cam : Camera.main;
        var intent = input.Read(cam);
        if (intent.dir != lastSent) { lastSent = intent.dir; if (!prediction.Active) ReplicationHub.Instance.SubmitInputRpc(intent.dir); }
        if (intent.hasClickTarget) ReplicationHub.Instance.SetTargetRpc(intent.clickWorld);
        if (prediction.Active && GhostManager.Instance != null && GhostManager.Instance.SelfGhost != null)
        {
            // Render between fixed ticks so the sprite moves every frame (steady walk animation), not just on the tick.
            float alpha = Time.fixedDeltaTime > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 1f;
            GhostManager.Instance.SelfGhost.position = prediction.VisualPos(alpha);
        }
    }

    // Drives client-side prediction on the fixed tick while in the underworld: activates on entry (seeding from
    // the authoritative self position), deactivates on exit, steps prediction each FixedUpdate.
    void FixedUpdate()
    {
        if (!Ready) return;
        bool inst = InInstance;
        if (inst && !wasInInstance && SelfWorldPos.HasValue) prediction.Activate(SelfWorldPos.Value);
        else if (!inst && wasInInstance) prediction.Deactivate();
        wasInInstance = inst;
        if (prediction.Active) prediction.FixedTick(Time.fixedDeltaTime);
    }

    // Routes the server's authoritative self position + last-processed tick into reconciliation.
    public void OnSnapshot(SnapshotEntry[] entries, ulong localId, uint ackTick)
    {
        if (!prediction.Active) return;
        for (int i = 0; i < entries.Length; i++)
            if (entries[i].id == localId)
            {
                bool snap = (entries[i].flags & SnapshotEntry.SnapBit) != 0;
                prediction.Reconcile(new Vector2(entries[i].x, entries[i].y), ackTick, snap);
                return;
            }
    }
}
