using UnityEngine;
using UnityEngine.InputSystem;

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

    InputRingBuffer buffer;
    Vector2 smoothOffset;                                // decays to zero so corrections don't snap
    Vector2 prevPos;                                     // logical pos before the latest FixedTick step

    public Vector2 RenderedPos => Pos + smoothOffset;

    // Visual position for rendering BETWEEN fixed ticks: interpolate prev->current by the fixed-step alpha, then
    // add the (separately decaying) correction offset. Pos only changes on the fixed tick, so without this the
    // sprite is piecewise-constant across render frames and PlayerView's delta-derived Speed collapses to zero
    // on every inter-tick frame, flickering Walk<->Idle. alpha is clamped, so it is safe to pass anything.
    public Vector2 VisualPos(float alpha) => Vector2.Lerp(prevPos, Pos, Mathf.Clamp01(alpha)) + smoothOffset;

    public void Activate(Vector2 startPos)
    {
        var cfg = Game.Instance.MovementCfg;
        buffer = new InputRingBuffer(Mathf.Max(8, Mathf.NextPowerOfTwo(cfg.inputBufferCapacity)));
        Pos = startPos;
        prevPos = startPos;
        smoothOffset = Vector2.zero;
        Tick = 0;
        Active = true;
    }

    public void Deactivate() => Active = false;

    // Called from LocalPlayer.FixedUpdate while Active: predict locally and send the tick-stamped input.
    public void FixedTick(float dt)
    {
        var cfg = Game.Instance.MovementCfg;
        Vector2 input = SampleInput();
        Tick++;
        prevPos = Pos;                                  // remember where we were so the visual can interpolate across the step
        Pos = MovementStep.Step(Pos, input, dt, cfg.moveSpeed, Walkable);
        buffer.Store(new InputFrame { tick = Tick, input = input, predictedPos = Pos });
        ReplicationHub.Instance.SubmitInputTickRpc(Tick, input);
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
            p = MovementStep.Step(p, f.input, Time.fixedDeltaTime, cfg.moveSpeed, Walkable);
            f.predictedPos = p;
            buffer.Store(f);
        }
        Pos = p;
        prevPos = Pos;   // a correction jumps the logical pos; smoothOffset eases the visual, so don't also interp across it
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

    static Vector2 SampleInput()
    {
        if (InputState.Typing) return Vector2.zero;
        var kb = Keyboard.current;
        if (kb == null) return Vector2.zero;
        Vector2 d = Vector2.zero;
        if (kb.wKey.isPressed) d.y += 1f;
        if (kb.sKey.isPressed) d.y -= 1f;
        if (kb.aKey.isPressed) d.x -= 1f;
        if (kb.dKey.isPressed) d.x += 1f;
        return d;
    }

    static bool Walkable(Vector2 p)
    {
        var gm = Game.Instance;
        var c = gm.WorldToCell(p);
        return gm.IsWalkable(c.x, c.y);
    }
}
