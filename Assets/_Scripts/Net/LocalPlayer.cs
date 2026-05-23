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

    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

    bool Ready => ReplicationHub.Instance != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;

    public bool InInstance => GhostManager.Instance != null && GhostManager.Instance.SelfInInstance;

    public Vector2? SelfWorldPos
    {
        get
        {
            var self = GhostManager.Instance != null ? GhostManager.Instance.SelfGhost : null;
            return self != null ? (Vector2?)(Vector2)self.position : null;
        }
    }

    public Vector2Int CurrentCell()
    {
        var gm = Game.Instance;
        var self = GhostManager.Instance != null ? GhostManager.Instance.SelfGhost : null;
        if (gm == null || self == null) return Vector2Int.zero;
        return gm.WorldToCell(self.position);
    }

    public void Halt()
    {
        if (!Ready) return;
        lastSent = Vector2.zero;
        ReplicationHub.Instance.SubmitInputServerRpc(Vector2.zero);
        ReplicationHub.Instance.HaltServerRpc();
    }

    public void RequestEnterInstance(Vector2Int siteCell)
    {
        if (!Ready || InInstance) return;
        lastSent = Vector2.zero;
        ReplicationHub.Instance.SubmitInputServerRpc(Vector2.zero);
        ReplicationHub.Instance.EnterInstanceServerRpc(siteCell.x, siteCell.y);
    }

    public void RequestLeaveInstance()
    {
        if (!Ready || !InInstance) return;
        lastSent = Vector2.zero;
        ReplicationHub.Instance.SubmitInputServerRpc(Vector2.zero);
        ReplicationHub.Instance.LeaveInstanceServerRpc();
    }

    void Update()
    {
        if (!Ready) return;
        var cam = Game.Instance != null ? Game.Instance.Cam : Camera.main;
        var intent = input.Read(cam);
        if (intent.dir != lastSent) { lastSent = intent.dir; ReplicationHub.Instance.SubmitInputServerRpc(intent.dir); }
        if (intent.hasClickTarget) ReplicationHub.Instance.SetTargetServerRpc(intent.clickWorld);
    }
}
