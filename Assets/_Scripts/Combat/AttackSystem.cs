using UnityEngine;

// Owner-side attack logic, owned + ticked by LocalPlayer (mirrors PredictionSystem). Holds the AttackState and
// steps the pure AttackLogic each frame; emits a one-shot log when the hit window opens (future: damage).
public sealed class AttackSystem
{
    AttackState state;
    AttackPhase prevPhase;

    public AttackState State => state;
    public PhaseScales Scales = PhaseScales.One;  // future: stats write this

    public void Tick(float dt, AttackIntent intent, AttackDefinition def)
    {
        if (def == null) return;
        prevPhase = state.phase;
        state = AttackLogic.Step(state, intent, def.Timeline, Scales, dt);
        if (prevPhase != AttackPhase.Hit && state.phase == AttackPhase.Hit)
            Debug.Log($"[attack] hit window open ({def.attackId})"); // verification hook; replace with damage later
    }
}
