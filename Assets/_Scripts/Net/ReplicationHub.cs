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
    readonly Dictionary<int, SnapshotEntry[]> snapshotBufByLen = new();   // RPC send buffers reused by entry count (args serialize synchronously, so reuse is safe)

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
        float period = 1f / hz;
        sendAccum += Time.deltaTime;
        if (sendAccum < period) return;
        sendAccum -= period;                       // keep the remainder so the cadence stays at exactly hz (matches the client's 1/hz interp window)
        if (sendAccum > period) sendAccum = 0f;    // fell >1 period behind (low fps): resync instead of spiraling
        if (cfg != null) SendSnapshots(cfg);
    }

    void FixedUpdate()
    {
        if (!IsServer) return;
        var cfg = Game.Instance != null ? Game.Instance.MovementCfg : null;
        if (cfg != null) PlayerSimSystem.StepInstanceFixed(registry, cfg, Time.fixedDeltaTime);
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
            int len = entryScratch.Count;
            if (!snapshotBufByLen.TryGetValue(len, out var buf)) { buf = new SnapshotEntry[len]; snapshotBufByLen[len] = buf; }
            entryScratch.CopyTo(buf);
            SnapshotClientRpc(buf, registry.Players[viewer].lastProcessedTick, p);
        }

        foreach (var sp in registry.Players.Values) sp.snap = false;   // snap consumed by this tick's snapshots
    }

    [ClientRpc]
    void SnapshotClientRpc(SnapshotEntry[] entries, uint ackTick, ClientRpcParams _ = default)
    {
        if (GhostManager.Instance != null)
            GhostManager.Instance.Apply(entries, NetworkManager.Singleton.LocalClientId);
        if (LocalPlayer.Instance != null)
            LocalPlayer.Instance.OnSnapshot(entries, NetworkManager.Singleton.LocalClientId, ackTick);
    }

    // ---- owner -> server intent (RequireOwnership=false; caller = SenderClientId) ----
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void SubmitInputRpc(Vector2 dir, RpcParams p = default)
    {
        if (registry.TryGet(p.Receive.SenderClientId, out var sp)) sp.submittedInput = dir;
    }

    // Tick-stamped free-move input from an in-instance owner (for prediction + reconciliation). Buffered and
    // consumed in tick order by PlayerSimSystem.StepInstanceFixed. Late duplicates (already simulated) drop.
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void SubmitInputTickRpc(uint tick, Vector2 input, RpcParams p = default)
    {
        if (!registry.TryGet(p.Receive.SenderClientId, out var sp)) return;
        if (tick <= sp.lastProcessedTick) return;                       // already simulated; ignore late dup
        sp.serverInputs ??= new InputRingBuffer(Mathf.Max(8, Mathf.NextPowerOfTwo(
            Game.Instance != null ? Game.Instance.MovementCfg.inputBufferCapacity : 128)));
        sp.serverInputs.Store(new InputFrame { tick = tick, input = input });
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void SetTargetRpc(Vector2 worldPoint, RpcParams p = default)
    {
        if (registry.TryGet(p.Receive.SenderClientId, out var sp))
        { sp.submittedInput = Vector2.zero; PlayerSimSystem.SetTarget(sp, worldPoint, moveSpeed); }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void HaltRpc(RpcParams p = default)
    {
        if (registry.TryGet(p.Receive.SenderClientId, out var sp)) PlayerSimSystem.Halt(sp);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void EnterInstanceRpc(int siteX, int siteY, RpcParams p = default)
    {
        var id = p.Receive.SenderClientId;
        if (!registry.TryGet(id, out var sp) || sp.inInstance) return;
        sp.overworldReturnCell = sp.motion.cell;
        var origin = Underworld.RegionOriginForSite(siteX, siteY);
        sp.regionKey = origin;
        PlayerSimSystem.Teleport(sp, Underworld.SpawnCell(origin, (int)id));
        sp.inInstance = true;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void LeaveInstanceRpc(RpcParams p = default)
    {
        if (!registry.TryGet(p.Receive.SenderClientId, out var sp) || !sp.inInstance) return;
        sp.regionKey = Vector2Int.zero;
        PlayerSimSystem.Teleport(sp, sp.overworldReturnCell);
        sp.inInstance = false;
    }
}
