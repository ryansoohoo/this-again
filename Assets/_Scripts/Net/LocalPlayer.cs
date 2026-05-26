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
    public ushort SelfHp { get; private set; }   // server-authoritative HP from the latest snapshot (display only)
    bool wasInInstance;

    [SerializeField] AttackDefinition currentAttack;
    public AttackDefinition EquippedWeapon => currentAttack;
    [SerializeField] AttackDefinition[] weapons;   // number keys 1-9,0 select these
    readonly AttackSystem attack = new();
    Transform attackViewGhost;
    AttackView attackView;

    // Attack input latched each render frame (button edges only fire on the render frame), consumed on the fixed tick.
    bool attackPressed, attackReleased, attackFeint, attackHeld;
    Vector2 attackAim;

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

        if (intent.weaponSlot >= 0 && weapons != null && intent.weaponSlot < weapons.Length
            && weapons[intent.weaponSlot] != null && !AttackLogic.IsAttacking(attack.State.phase))
            currentAttack = weapons[intent.weaponSlot];   // swap equipped weapon (not mid-attack)

        // Latch attack input for the next fixed tick (button edges fire on the render frame; the sim eats them there).
        bool canAttack = InInstance && SelfWorldPos.HasValue;   // combat is underworld-only
        attackPressed |= canAttack && intent.lmbDown;
        attackReleased |= canAttack && intent.lmbUp;
        attackFeint |= canAttack && intent.rmbDown;
        attackHeld = canAttack && intent.lmbHeld;
        if (SelfWorldPos.HasValue) attackAim = intent.cursorWorld - SelfWorldPos.Value;   // aim at the mouse cursor

        ResolveAttackView();
        if (attackView != null) attackView.Render(attack.State, currentAttack);   // render the rig every frame from current state

        var selfGhost = GhostManager.Instance != null ? GhostManager.Instance.SelfGhost : null;
        if (prediction.Active && selfGhost != null)
        {
            // Render between fixed ticks so the sprite moves every frame (steady walk animation), not just on the tick.
            float alpha = Time.fixedDeltaTime > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 1f;
            selfGhost.position = prediction.VisualPos(alpha);
        }
        if (selfGhost != null)   // self effect tint + hurt anim from the predicted mask (0 when not predicting, so they reset on leave)
        {
            ushort mask = prediction.Active ? StatusLogic.ActiveMask(prediction.Status) : (ushort)0;
            var sv = selfGhost.GetComponent<StatusView>(); if (sv != null) sv.Render(mask);
            var fx = selfGhost.GetComponent<StatusFxView>(); if (fx != null) fx.Render(mask);
            var dv = selfGhost.GetComponent<DmgView>(); if (dv != null) dv.Render(mask);   // after StatusView (tint) — DmgView wins the hurt sprite
        }
    }

    // Owner fixed tick: prediction activates on instance entry / deactivates on exit. While active, step attack +
    // lunge + movement together through the shared InstanceStep (with a weapon equipped) or movement-only (without).
    void FixedUpdate()
    {
        if (!Ready) return;
        bool inst = InInstance;
        if (inst && !wasInInstance && SelfWorldPos.HasValue) prediction.Activate(SelfWorldPos.Value);
        else if (!inst && wasInInstance) prediction.Deactivate();
        wasInInstance = inst;

        if (!prediction.Active) { attackPressed = attackReleased = attackFeint = false; return; }

        if (currentAttack != null)
        {
            var ai = new AttackIntent
            {
                pressed = attackPressed, held = attackHeld, released = attackReleased,
                feint = attackFeint, aimDir = attackAim,
            };
            var atk = attack.State;
            prediction.FixedTickInstance(ref atk, ai, ResolveWeaponId(), currentAttack.Timeline, attack.Scales, Time.fixedDeltaTime);
            attack.SetState(atk);
        }
        else
        {
            prediction.FixedTick(Time.fixedDeltaTime);
        }
        attackPressed = attackReleased = attackFeint = false;
    }

    // The equipped weapon's catalog id for the wire (server + remotes resolve it identically). 0 if unset.
    byte ResolveWeaponId()
    {
        var cat = Game.Instance != null ? Game.Instance.WeaponCatalog : null;
        if (cat == null || currentAttack == null) return 0;
        int i = cat.IndexOf(currentAttack);
        return (byte)(i < 0 ? 0 : i);
    }

    void ResolveAttackView()
    {
        var ghost = GhostManager.Instance != null ? GhostManager.Instance.SelfGhost : null;
        if (ghost == attackViewGhost) return;          // re-resolve only when the self-ghost changes
        attackViewGhost = ghost;
        attackView = ghost != null ? ghost.GetComponent<AttackView>() : null;
    }

    // Stub — full implementation lands in Task 9 (mirror an InventorySlot[20] from server).
    public void OnInventoryChanged(InventorySlot[] slots) { }

    // Routes the server's authoritative self position + last-processed tick into reconciliation.
    public void OnSnapshot(SnapshotEntry[] entries, ulong localId, uint ackTick)
    {
        if (!prediction.Active) return;
        for (int i = 0; i < entries.Length; i++)
            if (entries[i].id == localId)
            {
                var e = entries[i];
                if ((e.flags & SnapshotEntry.SelfBit) != 0)
                {
                    prediction.AdoptExternal(e.effDefId, e.effRemaining, e.effStacks, e.effectCount, e.selfFleeAngle);
                    SelfHp = e.hp;
                }
                bool snap = (e.flags & SnapshotEntry.SnapBit) != 0;
                prediction.Reconcile(new Vector2(e.x, e.y), ackTick, snap);
                return;
            }
    }
}
