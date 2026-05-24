using Unity.Netcode;
using UnityEngine;

// Client-side prediction for the local player while in the underworld. Runs on the fixed tick: sample input,
// step locally with the same MovementStep the server uses, store the frame in a ring buffer, and send the
// tick-stamped input. On each snapshot, Reconcile() corrects against the server by replaying un-acked inputs
// and easing the correction via smoothOffset. RenderedPos drives the visual (Task 9). Plain logic class,
// ticked by LocalPlayer; not a MonoBehaviour.
public sealed class PredictionSystem
{
    public bool Active { get; private set; }
    public Vector2 Pos { get; private set; }            // authoritative-predicted logical position
    public uint Tick { get; private set; }
    public StatusState Status { get; } = new();         // owner-predicted effects; reduces to the gate, adopted on snapshot (Task 11)

    RingBuffer<InputFrame> buffer;
    Vector2 smoothOffset;                                // decays to zero so corrections don't snap
    Vector2 prevPos;                                     // logical pos before the latest FixedTick step
    ulong localId;                                       // cached on Activate; excludes self + sorts deterministically
    const float MovedEps = 1e-6f;                        // MUST match AttackSimSystem.MovedEps so client invMass mirrors the server
    CollisionBody[] _remotes = new CollisionBody[8];     // roommate bodies, filled by GhostManager (grown as needed)
    CollisionBody[] _scratch = new CollisionBody[8];     // ResolveOne work buffer (>= remotes+1)

    public Vector2 RenderedPos => Pos + smoothOffset;

    // Visual position for rendering BETWEEN fixed ticks: interpolate prev->current by the fixed-step alpha, then
    // add the (separately decaying) correction offset. Pos only changes on the fixed tick, so without this the
    // sprite is piecewise-constant across render frames and PlayerView's delta-derived Speed collapses to zero
    // on every inter-tick frame, flickering Walk<->Idle. alpha is clamped, so it is safe to pass anything.
    public Vector2 VisualPos(float alpha) => Vector2.Lerp(prevPos, Pos, Mathf.Clamp01(alpha)) + smoothOffset;

    public void Activate(Vector2 startPos)
    {
        var cfg = Game.Instance.MovementCfg;
        buffer = new RingBuffer<InputFrame>(Mathf.Max(8, Mathf.NextPowerOfTwo(cfg.inputBufferCapacity)));
        localId = NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId : 0;
        Pos = startPos;
        prevPos = startPos;
        smoothOffset = Vector2.zero;
        Tick = 0;
        Status.Clear();
        Active = true;
    }

    public void Deactivate() { Status.Clear(); Active = false; }

    static StatusEffectDef[] Defs() { var c = Game.Instance != null ? Game.Instance.StatusCatalog : null; return c != null ? c.Defs : System.Array.Empty<StatusEffectDef>(); }

    // Self-predicted kinds are owner-authoritative-by-construction (derived from our own input); everything else
    // is server-driven. Keep AttackCooldown (we predict it from our own feint); adopt the rest from the server.
    static bool SelfPredicted(byte defId) => defId == (byte)StatusKind.AttackCooldown;

    // On each snapshot: drop our external effects, re-add the authoritative ones (re-syncs stun/poison/freeze/slow
    // timing), and leave self-predicted instances untouched so the cooldown doesn't flicker before the ack catches up.
    public void AdoptExternal(byte[] defIds, ushort[] remaining, byte[] stacks, int n)
    {
        for (int i = 0; i < Status.count; )
        {
            if (!SelfPredicted(Status.effects[i].defId)) Status.effects[i] = Status.effects[--Status.count];
            else i++;
        }
        for (int k = 0; k < n; k++)
        {
            if (SelfPredicted(defIds[k])) continue;   // server's copy of our cooldown — keep our prediction
            if (Status.count >= StatusState.Cap) break;
            Status.effects[Status.count++] = new ActiveEffect
            {
                defId = defIds[k], remainingTicks = remaining[k], stacks = stacks[k],
                sincePeriodTick = 0, appliedTick = 0, selfInflicted = false,
            };
        }
    }

    // Movement-only fixed tick (no weapon equipped): predict locally and send the tick-stamped input. The wire
    // carries RAW WASD; the server re-applies its own (authoritative) gate. Local prediction + the stored replay
    // frame use the gated vector so reconcile replay reproduces the same motion.
    public void FixedTick(float dt)
    {
        var cfg = Game.Instance.MovementCfg;
        GateMod gate = GateMod.Quantize(StatusLogic.Step(Status, Defs(), out _));   // age effects → predicted gate
        Vector2 raw = WasdInput.Read();
        Vector2 move = InstanceStep.FreeMove(raw, gate);
        Tick++;
        prevPos = Pos;                                  // remember where we were so the visual can interpolate across the step
        Vector2 stepped = MovementStep.Step(Pos, move, dt, cfg.moveSpeed, Walkable);
        Pos = ResolveSelfCollision(stepped, prevPos);   // predict our own push-apart vs same-room ghosts
        buffer.Store(new InputFrame { tick = Tick, input = move, predictedPos = Pos });
        ReplicationHub.Instance.SubmitInputTickRpc(new InputCommand { tick = Tick, rawMove = raw });
    }

    // In-instance fixed tick with an equipped weapon: step attack + lunge + movement together via the shared
    // InstanceStep (the same function the server and replay use), then send the EFFECTIVE move so the (Phase-1
    // unchanged) server reproduces the identical position. `atk` is stepped in place.
    public void FixedTickInstance(ref AttackState atk, AttackIntent attack, byte weaponId, AttackTimeline tl, PhaseScales scales, float dt)
    {
        var cfg = Game.Instance.MovementCfg;
        Vector2 rawMove = WasdInput.Read();
        ushort aimQ = AimQuant.Encode(attack.aimDir);
        attack.aimDir = AimQuant.Decode(aimQ);   // predict with the SAME value the server receives (determinism)
        Tick++;
        prevPos = Pos;
        Vector2 p = Pos;
        var ctx = new InstanceCtx { timeline = tl, scales = scales, dt = dt, speed = cfg.moveSpeed, walkable = Walkable, defs = Defs() };
        InstanceStep.Step(ref atk, Status, ref p, new InstanceInput { rawMove = rawMove, attack = attack }, ctx, out _);   // steps Status + derives the gate
        Pos = ResolveSelfCollision(p, prevPos);          // predict push-apart; p includes lunge so invMass mirrors the server
        GateMod gate = GateMod.Quantize(StatusLogic.Reduce(Status, Defs()));   // read-only: the post-step gate for the stored fallback vector
        buffer.Store(new InputFrame { tick = Tick, input = AttackLogic.LungeVelocity(atk, tl) ?? InstanceStep.FreeMove(rawMove, gate), predictedPos = Pos });
        byte bits = (byte)((attack.pressed ? InputCommand.Pressed : 0) | (attack.held ? InputCommand.Held : 0)
                         | (attack.released ? InputCommand.Released : 0) | (attack.feint ? InputCommand.Feint : 0));
        ReplicationHub.Instance.SubmitInputTickRpc(new InputCommand { tick = Tick, rawMove = rawMove, attackBits = bits, aimAngle = aimQ, weaponId = weaponId });
    }

    // Called when a snapshot arrives (Active only). authPos = server authoritative self pos; ackTick = last
    // client tick the server simulated. If our prediction at that tick diverged, snap the logical position to
    // authoritative and replay still-unacked inputs, then push the pre-correction visual delta into
    // smoothOffset so the on-screen correction eases instead of snapping.
    public void Reconcile(Vector2 authPos, uint ackTick, bool snap)
    {
        if (!Active || ackTick == 0) return;

        // Teleport: jump hard to the authoritative position (continuing from there through any post-ack inputs),
        // with NO smoothing — the snap flag exists precisely to avoid easing across the jump.
        if (snap)
        {
            if (buffer.TryGet(ackTick, out _)) { ReplayFrom(authPos, ackTick); smoothOffset = Vector2.zero; }
            else HardSnap(authPos);
            return;
        }

        if (!buffer.TryGet(ackTick, out var acked)) { HardSnap(authPos); return; }

        float eps = Game.Instance.MovementCfg.reconcileEpsilon;
        if ((acked.predictedPos - authPos).sqrMagnitude <= eps * eps) return;   // within tolerance: no correction

        Vector2 before = RenderedPos;
        ReplayFrom(authPos, ackTick);
        smoothOffset = before - Pos;   // keep the visual where it was; ease the gap to zero in Decay()
    }

    // Re-simulate buffered inputs after ackTick from a known-good position, rewriting their stored predictions.
    void ReplayFrom(Vector2 fromPos, uint ackTick)
    {
        var cfg = Game.Instance.MovementCfg;
        Vector2 p = fromPos;
        for (uint t = ackTick + 1; t <= Tick; t++)
        {
            if (!buffer.TryGet(t, out var f)) break;
            Vector2 before = p;
            p = MovementStep.Step(p, f.input, Time.fixedDeltaTime, cfg.moveSpeed, Walkable);
            p = ResolveSelfCollision(p, before);   // re-resolve vs current ghost positions so replay == live path
            f.predictedPos = p;
            buffer.Store(f);
        }
        Pos = p;
        prevPos = Pos;   // a correction jumps the logical pos; smoothOffset eases the visual, so don't also interp across it
    }

    // Predict the local player's own push-apart: resolve self against same-room ghosts (rendered positions,
    // symmetric mover weighting) with the SAME deterministic CollisionStep the server runs, and return self's
    // resolved position (remotes are server-owned, discarded). `candidate` is the just-integrated position;
    // `beforeMove` is the position before this tick's movement, so invMass mirrors the server's pre-collision
    // moved-this-tick rule. No-op when alone in the room. Called on the live tick AND reconcile replay.
    Vector2 ResolveSelfCollision(Vector2 candidate, Vector2 beforeMove)
    {
        var gm = GhostManager.Instance; var game = Game.Instance;
        if (gm == null || game == null) return candidate;
        int rc = gm.RoommateCount(localId);
        if (rc == 0) return candidate;
        EnsureCollisionCapacity(rc);
        var cfg = game.MovementCfg;
        gm.FillRoommateBodies(localId, cfg.collisionRadius, _remotes);
        float invMass = (candidate - beforeMove).sqrMagnitude > MovedEps ? 1f : 0f;
        var self = new CollisionBody { id = localId, pos = candidate, radius = cfg.collisionRadius, invMass = invMass };
        return CollisionStep.ResolveOne(self, _remotes, rc, Walkable, cfg.collisionIterations, _scratch);
    }

    void EnsureCollisionCapacity(int rc)
    {
        if (_remotes.Length < rc) _remotes = new CollisionBody[Mathf.NextPowerOfTwo(rc)];
        if (_scratch.Length < rc + 1) _scratch = new CollisionBody[Mathf.NextPowerOfTwo(rc + 1)];
    }

    void HardSnap(Vector2 authPos) { Pos = authPos; prevPos = authPos; smoothOffset = Vector2.zero; }

    // Called every frame from LocalPlayer.Update to ease smoothOffset toward zero over correctionSmoothTime.
    public void Decay(float dt)
    {
        if (smoothOffset == Vector2.zero) return;
        float t = Game.Instance.MovementCfg.correctionSmoothTime;
        smoothOffset = (t <= 0f) ? Vector2.zero : Vector2.Lerp(smoothOffset, Vector2.zero, Mathf.Clamp01(dt / t));
        if (smoothOffset.sqrMagnitude < 1e-6f) smoothOffset = Vector2.zero;
    }

    static bool Walkable(Vector2 p)
    {
        var gm = Game.Instance;
        var c = gm.WorldToCell(p);
        return gm.IsWalkable(c.x, c.y);
    }
}
