using UnityEngine;

// Owner-side attack state holder. The state is stepped by PredictionSystem via the shared InstanceStep (so it
// stays in lockstep with movement, the server, and replay); this stores the result and emits a one-shot log on
// the hit-window rising edge (verification hook; the server is authoritative for damage later).
public sealed class AttackSystem
{
    AttackState state;
    AttackPhase prevPhase;

    public AttackState State => state;
    public PhaseScales Scales = PhaseScales.One;  // future: stats write this

    public void SetState(AttackState s)
    {
        if (prevPhase != AttackPhase.Hit && s.phase == AttackPhase.Hit)
            Debug.Log("[attack] hit window open"); // verification hook; replace with damage later
        prevPhase = s.phase;
        state = s;
    }
}
