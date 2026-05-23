using System.Collections.Generic;
using UnityEngine;

// Pure visibility query: which players should a given viewer observe? Same region AND within radius, with
// show/hide hysteresis (a player already visible stays out to hideRadius; a new one appears only inside
// showRadius). The viewer always observes itself. No Netcode / engine-object deps -> unit-testable.
// O(n^2) over players (fine for hundreds); a region-bucketed spatial hash is a future optimization behind
// this same signature.
public static class AreaOfInterestSystem
{
    // Fills `result` with the ids the viewer should observe. `prior` is the viewer's previous visible set
    // (for hysteresis); pass an empty set on the first tick. `result` is cleared first.
    public static void VisibleFor(ulong viewerId, IReadOnlyList<AoiPlayer> players, ReplicationSettings s,
                                  HashSet<ulong> prior, HashSet<ulong> result)
    {
        result.Clear();

        AoiPlayer viewer = default; bool found = false;
        for (int i = 0; i < players.Count; i++)
            if (players[i].id == viewerId) { viewer = players[i]; found = true; break; }
        if (!found) return;

        result.Add(viewerId);   // always see yourself

        float show2 = s.showRadius * s.showRadius, hide2 = s.hideRadius * s.hideRadius;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            if (p.id == viewerId) continue;
            if (p.regionKey != viewer.regionKey) continue;     // hard region isolation (overworld / each room)
            float d2 = (p.pos - viewer.pos).sqrMagnitude;
            float r2 = prior.Contains(p.id) ? hide2 : show2;   // hysteresis
            if (d2 <= r2) result.Add(p.id);
        }
    }
}
