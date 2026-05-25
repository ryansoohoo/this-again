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
    readonly DummySpawner dummies = new();   // server-only test-dummy goblins (one per underworld room)
    float sendAccum;

    // server scratch (reused to avoid per-tick allocation where possible)
    readonly List<AoiPlayer> aoiScratch = new();
    readonly Dictionary<ulong, HashSet<ulong>> visiblePrev = new();   // per viewer: last visible set (hysteresis)
    readonly HashSet<ulong> visibleNow = new();
    readonly List<SnapshotEntry> entryScratch = new();
    readonly List<AttackEvent> eventScratch = new();
    readonly ulong[] oneTarget = new ulong[1];
    readonly Dictionary<int, SnapshotEntry[]> snapshotBufByLen = new();   // RPC send buffers reused by entry count (args serialize synchronously, so reuse is safe)
    readonly Dictionary<int, AttackEvent[]> eventBufByLen = new();        // same reuse-by-count for the piggybacked attack-event RPC (was a per-send ToArray)

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
    void OnClientDisconnected(ulong clientId)
    {
        bool inInst = registry.TryGet(clientId, out var sp) && sp.inInstance;
        Vector2Int region = inInst ? sp.regionKey : default;
        registry.Remove(clientId);
        visiblePrev.Remove(clientId);
        if (inInst) dummies.RemoveDummyIfRoomEmpty(registry, region);
    }

    void EnsurePlayer(ulong clientId)
    {
        if (registry.Players.ContainsKey(clientId)) return;
        var gm = Game.Instance;
        int n = (int)clientId;
        var cell = new Vector2Int((n % 5) - 2, (n / 5) - 2);   // small spread around origin (old OnNetworkSpawn)
        var pos = gm != null ? gm.CellCenter(cell.x, cell.y) : (Vector2)cell;
        registry.Add(clientId, cell, pos, gm != null ? gm.PlayerCharacter : null);
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
        var catalog = Game.Instance != null ? Game.Instance.WeaponCatalog : null;
        if (cfg != null) AttackSimSystem.StepInstanceFixed(registry, catalog, cfg, Time.fixedDeltaTime);
        dummies.TickRespawn();
    }

    void SendSnapshots(ReplicationSettings cfg)
    {
        aoiScratch.Clear();
        foreach (var kv in registry.Combatants)
            aoiScratch.Add(new AoiPlayer(kv.Key, kv.Value.worldPos, kv.Value.regionKey));

        foreach (var kv in registry.Players)
        {
            ulong viewer = kv.Key;
            if (!visiblePrev.TryGetValue(viewer, out var prev)) { prev = new HashSet<ulong>(); visiblePrev[viewer] = prev; }
            AreaOfInterestSystem.VisibleFor(viewer, aoiScratch, cfg, prev, visibleNow);

            entryScratch.Clear();
            foreach (var id in visibleNow)
            {
                var c = registry.Combatants[id];
                byte flags = 0;
                if (c is ServerPlayer csp && csp.snap) flags |= SnapshotEntry.SnapBit;
                bool self = id == viewer && c.inInstance;
                if (self) flags |= SnapshotEntry.SelfBit;
                if (c.inInstance) flags |= SnapshotEntry.InstanceBit;
                var entry = new SnapshotEntry { id = id, x = c.worldPos.x, y = c.worldPos.y, flags = flags };
                if (c.inInstance) entry.hp = (ushort)Mathf.Max(0, c.hp);
                if (self)
                {
                    int n = c.status.count;
                    entry.effectCount = (byte)n;
                    entry.effDefId = new byte[n]; entry.effRemaining = new ushort[n]; entry.effStacks = new byte[n];
                    for (int k = 0; k < n; k++)
                    {
                        entry.effDefId[k] = c.status.effects[k].defId;
                        entry.effRemaining[k] = (ushort)Mathf.Max(0, c.status.effects[k].remainingTicks);
                        entry.effStacks[k] = c.status.effects[k].stacks;
                    }
                    entry.selfFleeAngle = 0xFFFF;
                    var statusDefs = Game.Instance != null ? Game.Instance.StatusCatalog?.Defs : null;
                    if (statusDefs != null && StatusLogic.ActiveForcedMove(c.status, statusDefs, out var fdir, out _))
                        entry.selfFleeAngle = AimQuant.Encode(fdir);
                }
                else if (c.inInstance)
                {
                    entry.effectMask = StatusLogic.ActiveMask(c.status);
                }
                if (c.inInstance && AttackLogic.IsAttacking(c.attackState.phase))
                {
                    var st = c.attackState;
                    entry.flags |= SnapshotEntry.AttackingBit;
                    entry.weaponId = c.weaponId;
                    entry.pose = AttackPose.Pack(st.phase, st.frameIndex, st.dirIndex);
                    entry.residual = AimQuant.Encode(st.lockedAim);
                }
                entryScratch.Add(entry);
            }

            prev.Clear();
            foreach (var id in visibleNow) prev.Add(id);

            oneTarget[0] = viewer;
            var p = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = oneTarget } };
            int len = entryScratch.Count;
            if (!snapshotBufByLen.TryGetValue(len, out var buf)) { buf = new SnapshotEntry[len]; snapshotBufByLen[len] = buf; }
            entryScratch.CopyTo(buf);
            SnapshotClientRpc(buf, registry.Players[viewer].lastProcessedTick, p);

            // Piggyback reliable attack events for visible attackers on the same per-viewer AOI pass.
            eventScratch.Clear();
            foreach (var id in visibleNow)
            {
                var c2 = registry.Combatants[id];
                if (c2.pendingEvents == null || c2.pendingEvents.Count == 0) continue;
                foreach (var ev in c2.pendingEvents) eventScratch.Add(ev);
            }
            if (eventScratch.Count > 0)
            {
                int elen = eventScratch.Count;
                if (!eventBufByLen.TryGetValue(elen, out var ebuf)) { ebuf = new AttackEvent[elen]; eventBufByLen[elen] = ebuf; }
                eventScratch.CopyTo(ebuf);
                AttackEventClientRpc(ebuf, p);
            }
        }

        foreach (var sp in registry.Players.Values) { sp.snap = false; sp.pendingEvents?.Clear(); }   // consumed this tick
    }

    [ClientRpc]
    void SnapshotClientRpc(SnapshotEntry[] entries, uint ackTick, ClientRpcParams _ = default)
    {
        if (GhostManager.Instance != null)
            GhostManager.Instance.Apply(entries, NetworkManager.Singleton.LocalClientId);
        if (LocalPlayer.Instance != null)
            LocalPlayer.Instance.OnSnapshot(entries, NetworkManager.Singleton.LocalClientId, ackTick);
    }

    [ClientRpc]
    void AttackEventClientRpc(AttackEvent[] events, ClientRpcParams _ = default)
    {
        if (GhostManager.Instance != null)
            GhostManager.Instance.ApplyAttackEvents(events, NetworkManager.Singleton.LocalClientId);
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
    public void SubmitInputTickRpc(InputCommand cmd, RpcParams p = default)
    {
        if (!registry.TryGet(p.Receive.SenderClientId, out var sp)) return;
        if (cmd.tick <= sp.lastProcessedTick) return;                   // already simulated; ignore late dup
        sp.weaponId = cmd.weaponId;
        sp.serverInputs ??= new RingBuffer<InputCommand>(Mathf.Max(8, Mathf.NextPowerOfTwo(
            Game.Instance != null ? Game.Instance.MovementCfg.inputBufferCapacity : 128)));
        sp.serverInputs.Store(cmd);
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
        sp.hp = sp.stats != null ? sp.stats.GetInt(StatKind.MaxHp) : 100;   // full HP each run (from CharacterDef stats)
        sp.status.Clear();                        // no effects carried in from a previous run
        dummies.EnsureRoomDummy(registry, sp.regionKey);   // one goblin test dummy per room
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void LeaveInstanceRpc(RpcParams p = default)
    {
        if (!registry.TryGet(p.Receive.SenderClientId, out var sp) || !sp.inInstance) return;
        var leftRegion = sp.regionKey;
        sp.regionKey = Vector2Int.zero;
        PlayerSimSystem.Teleport(sp, sp.overworldReturnCell);
        sp.inInstance = false;
        sp.status.Clear();   // effects don't persist to the overworld
        dummies.RemoveDummyIfRoomEmpty(registry, leftRegion);
    }

    // Debug seam (host/server only): the local player's authoritative StatusState, for console testing. Null on a
    // pure client — effects are server-authoritative.
    public StatusState DebugLocalStatus()
    {
        var nm = NetworkManager.Singleton;
        if (!IsServer || nm == null) return null;
        return registry.TryGet(nm.LocalClientId, out var sp) ? sp.status : null;
    }
}
