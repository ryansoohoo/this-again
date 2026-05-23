using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// The single replication NetworkObject (scene singleton). Carries every RPC both ways: owner->server intent
// (RequireOwnership=false; caller identified by SenderClientId) and server->client targeted AOI snapshots.
// Owns the server PlayerRegistry and drives PlayerSimSystem. No NetworkVariable, no NetworkTransform; players
// are not NetworkObjects.
[RequireComponent(typeof(NetworkObject))]
public sealed class ReplicationHub : NetworkBehaviour
{
    public static ReplicationHub Instance { get; private set; }

    [SerializeField] float moveSpeed = 4f;

    readonly PlayerRegistry registry = new();
    float sendAccum;

    // server scratch (reused to avoid per-tick allocation where possible)
    readonly List<AoiPlayer> aoiScratch = new();
    readonly Dictionary<ulong, HashSet<ulong>> visiblePrev = new();   // per viewer: last visible set (hysteresis)
    readonly HashSet<ulong> visibleNow = new();
    readonly List<SnapshotEntry> entryScratch = new();
    readonly ulong[] oneTarget = new ulong[1];

    public override void OnNetworkSpawn()
    {
        Instance = this;
        if (IsServer)
        {
            var nm = NetworkManager.Singleton;
            nm.OnClientConnectedCallback += OnClientConnected;
            nm.OnClientDisconnectCallback += OnClientDisconnected;
            foreach (var id in nm.ConnectedClientsIds) EnsurePlayer(id);   // seeds the host too
        }
    }

    public override void OnNetworkDespawn()
    {
        var nm = NetworkManager.Singleton;
        if (IsServer && nm != null)
        {
            nm.OnClientConnectedCallback -= OnClientConnected;
            nm.OnClientDisconnectCallback -= OnClientDisconnected;
        }
        if (Instance == this) Instance = null;
    }

    void OnClientConnected(ulong clientId) => EnsurePlayer(clientId);
    void OnClientDisconnected(ulong clientId) { registry.Remove(clientId); visiblePrev.Remove(clientId); }

    void EnsurePlayer(ulong clientId)
    {
        if (registry.Players.ContainsKey(clientId)) return;
        var gm = Game.Instance;
        int n = (int)clientId;
        var cell = new Vector2Int((n % 5) - 2, (n / 5) - 2);   // small spread around origin (old OnNetworkSpawn)
        var pos = gm != null ? gm.CellCenter(cell.x, cell.y) : (Vector2)cell;
        registry.Add(clientId, cell, pos);
    }

    void Update()
    {
        if (!IsServer) return;
        PlayerSimSystem.StepAll(registry, moveSpeed, Time.deltaTime);

        var cfg = Game.Instance != null ? Game.Instance.ReplicationCfg : null;
        int hz = cfg != null ? Mathf.Max(1, cfg.snapshotHz) : 15;
        sendAccum += Time.deltaTime;
        if (sendAccum < 1f / hz) return;
        sendAccum = 0f;
        if (cfg != null) SendSnapshots(cfg);
    }

    void SendSnapshots(ReplicationSettings cfg)
    {
        aoiScratch.Clear();
        foreach (var kv in registry.Players)
            aoiScratch.Add(new AoiPlayer(kv.Key, kv.Value.worldPos, kv.Value.regionKey));

        foreach (var kv in registry.Players)
        {
            ulong viewer = kv.Key;
            if (!visiblePrev.TryGetValue(viewer, out var prev)) { prev = new HashSet<ulong>(); visiblePrev[viewer] = prev; }
            AreaOfInterestSystem.VisibleFor(viewer, aoiScratch, cfg, prev, visibleNow);

            entryScratch.Clear();
            foreach (var id in visibleNow)
            {
                var sp = registry.Players[id];
                byte flags = 0;
                if (sp.snap) flags |= SnapshotEntry.SnapBit;
                if (id == viewer && sp.inInstance) flags |= SnapshotEntry.InInstanceBit;
                entryScratch.Add(new SnapshotEntry { id = id, x = sp.worldPos.x, y = sp.worldPos.y, flags = flags });
            }

            prev.Clear();
            foreach (var id in visibleNow) prev.Add(id);

            oneTarget[0] = viewer;
            var p = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = oneTarget } };
            SnapshotClientRpc(entryScratch.ToArray(), p);
        }

        foreach (var sp in registry.Players.Values) sp.snap = false;   // snap consumed by this tick's snapshots
    }

    [ClientRpc]
    void SnapshotClientRpc(SnapshotEntry[] entries, ClientRpcParams _ = default)
    {
        if (GhostManager.Instance != null)
            GhostManager.Instance.Apply(entries, NetworkManager.Singleton.LocalClientId);
    }

    // ---- owner -> server intent (RequireOwnership=false; caller = SenderClientId) ----
    [ServerRpc(RequireOwnership = false)]
    public void SubmitInputServerRpc(Vector2 dir, ServerRpcParams p = default)
    {
        if (registry.TryGet(p.Receive.SenderClientId, out var sp)) sp.submittedInput = dir;
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetTargetServerRpc(Vector2 worldPoint, ServerRpcParams p = default)
    {
        if (registry.TryGet(p.Receive.SenderClientId, out var sp))
        { sp.submittedInput = Vector2.zero; PlayerSimSystem.SetTarget(sp, worldPoint, moveSpeed); }
    }

    [ServerRpc(RequireOwnership = false)]
    public void HaltServerRpc(ServerRpcParams p = default)
    {
        if (registry.TryGet(p.Receive.SenderClientId, out var sp)) PlayerSimSystem.Halt(sp);
    }

    [ServerRpc(RequireOwnership = false)]
    public void EnterInstanceServerRpc(int siteX, int siteY, ServerRpcParams p = default)
    {
        var id = p.Receive.SenderClientId;
        if (!registry.TryGet(id, out var sp) || sp.inInstance) return;
        sp.overworldReturnCell = sp.motion.cell;
        var origin = Underworld.RegionOriginForSite(siteX, siteY);
        sp.regionKey = origin;
        PlayerSimSystem.Teleport(sp, Underworld.SpawnCell(origin, (int)id));
        sp.inInstance = true;
    }

    [ServerRpc(RequireOwnership = false)]
    public void LeaveInstanceServerRpc(ServerRpcParams p = default)
    {
        if (!registry.TryGet(p.Receive.SenderClientId, out var sp) || !sp.inInstance) return;
        sp.regionKey = Vector2Int.zero;
        PlayerSimSystem.Teleport(sp, sp.overworldReturnCell);
        sp.inInstance = false;
    }
}
