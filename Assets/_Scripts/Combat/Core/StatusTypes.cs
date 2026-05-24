// Pure status-effect data: the catalog-resolved def, one active instance, and the per-player collection.
// No GateMod/Netcode/scene refs (StatusLogic, in Minifantasy.InstanceSim, reduces these to a GateMod).

// Well-known effect ids: the value IS the catalog index AND the wire defId. Keep in sync with StatusCatalog order.
public enum StatusKind : byte { HitStun = 0, AttackCooldown = 1, Poison = 2, Freeze = 3, Slow = 4 }

// How a re-applied effect of the same kind combines with an existing instance.
public enum StackPolicy : byte { Refresh, Stack, Independent }

// One catalog entry, resolved to ticks/raw-gate fields for the deterministic sim.
public struct StatusEffectDef
{
    public byte id;
    public int durationTicks;     // 0 = no inherent lifetime (AttackCooldown supplies its own at apply-time)
    public bool blocksMove;
    public bool blocksAttack;
    public float moveScale;       // 1 = no slow
    public int periodTicks;       // 0 = no periodic effect
    public int amountPerTick;     // damage per period (× stacks)
    public StackPolicy policy;
    public byte maxStacks;
    public byte visualId;
}

// One live effect on a player.
public struct ActiveEffect
{
    public byte defId;
    public int remainingTicks;
    public byte stacks;
    public int sincePeriodTick;
    public uint appliedTick;
    public bool selfInflicted;    // true = owner-predicted (AttackCooldown); false = adopted from server
}

// Per-player active-effect collection. Plain data; StatusLogic does all the work. Fixed capacity, compacted
// on removal (swap-remove), so iteration is effects[0..count). A class (one instance per player/predictor;
// Approach 1 needs no per-frame copy), reused each tick — no steady-state allocation.
public sealed class StatusState
{
    public const int Cap = 8;
    public readonly ActiveEffect[] effects = new ActiveEffect[Cap];
    public int count;

    public void Clear() => count = 0;
}
