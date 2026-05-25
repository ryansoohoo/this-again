using UnityEngine;

// One attack-motion's swing-overlay frames (front + back layers) for a given status effect's color.
[System.Serializable]
public struct MotionOverlay { public AttackMotion motion; public Sprite[] front; public Sprite[] back; }

// One status effect as a reusable asset: gameplay (compiled to the pure StatusEffectDef the sim consumes) plus
// visual data (read ONLY by the View layer — never enters Minifantasy.InstanceSim). Weapons reference these;
// spells/abilities/items will too. Order in StatusCatalog MUST place index i at kind == (StatusKind)i.
[CreateAssetMenu(menuName = "Minifantasy/Status Effect", fileName = "Effect")]
public sealed class StatusEffectAsset : ScriptableObject
{
    [Header("Identity")]
    public StatusKind kind;            // canonical id == catalog index == wire defId == mask bit

    [Header("Gameplay (seconds; compiled to ticks)")]
    public float durationSeconds;
    public bool blocksMove;
    public bool blocksAttack;
    public float moveScale = 1f;       // 1 = none
    public float periodSeconds;        // 0 = no DOT
    public int amountPerTick;
    public StackPolicy policy = StackPolicy.Refresh;
    public byte maxStacks = 1;
    public ForcedMoveKind forcedMove = ForcedMoveKind.None;
    public float forcedMoveScale = 1f; // flee speed as a fraction of moveSpeed

    [Header("Visual (View layer only)")]
    public byte visualId;
    public byte visualPriority;     // higher = shown first when several effects are active (tint + FX selection)
    public Color tintColor = Color.white;   // rig tint while active (StatusView); white = no tint
    public Sprite[] hitFx;                   // one-shot on apply, on the victim (StatusFxView Layer B)
    public float hitFps = 14f;
    public Sprite[] tickFx;                  // over-time loop on the victim, pulses at periodSeconds (Layer C)
    public float tickFps = 10f;
    public string fxColor;                    // pack color name for the swing overlay ("fire","ice","poisson",...); empty = no overlay
    public MotionOverlay[] attackOverlays;    // per attack-motion swing overlay (front/back), already in THIS effect's color

    // Front/back swing-overlay frames for a weapon's attack motion (this effect's color). False if none authored.
    public bool TryGetOverlay(AttackMotion m, out Sprite[] front, out Sprite[] back)
    {
        if (attackOverlays != null)
            for (int i = 0; i < attackOverlays.Length; i++)
                if (attackOverlays[i].motion == m) { front = attackOverlays[i].front; back = attackOverlays[i].back; return front != null && front.Length > 0; }
        front = null; back = null; return false;
    }

    public StatusEffectDef ToDef() => new StatusEffectDef
    {
        id = (byte)kind,
        durationTicks = Mathf.CeilToInt(durationSeconds * 60f),
        blocksMove = blocksMove,
        blocksAttack = blocksAttack,
        moveScale = moveScale,
        periodTicks = Mathf.CeilToInt(periodSeconds * 60f),
        amountPerTick = amountPerTick,
        policy = policy,
        maxStacks = (byte)Mathf.Max(1, maxStacks),
        visualId = visualId,
        forcedMove = forcedMove,
        forcedMoveScale = forcedMoveScale,
    };
}
