using System.Collections.Generic;
using UnityEngine;

// Server-side per-player action gates: a set of source-keyed GateMods reduced to one effective GateMod.
// The reduction is order-independent (OR of the block flags, product of moveScale), so overlapping effects
// compose and clearing one source never re-enables what another still blocks — that is the overlap / race
// safety. v1 has no real writers besides the debug console command; status effects later just call Set/Clear
// with their own source id. Pure logic (lives in the sim assembly, instantiated on ServerPlayer); only the
// quantized Effective value replicates to the owner.
public sealed class AbilityGate
{
    readonly Dictionary<int, GateMod> sources = new();

    public void Set(int sourceId, GateMod mod) => sources[sourceId] = mod;
    public bool Clear(int sourceId) => sources.Remove(sourceId);
    public void ClearAll() => sources.Clear();
    public int Count => sources.Count;

    public GateMod Effective
    {
        get
        {
            if (sources.Count == 0) return GateMod.None;
            bool blockMove = false, blockAttack = false;
            float scale = 1f;
            foreach (var m in sources.Values)
            {
                blockMove |= m.blocksMove;
                blockAttack |= m.blocksAttack;
                scale *= Mathf.Clamp01(m.moveScale);
            }
            return new GateMod { blocksMove = blockMove, blocksAttack = blockAttack, moveScale = Mathf.Clamp01(scale) };
        }
    }
}
