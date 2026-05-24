using UnityEngine;

// Shared byte-id <-> status-effect map (the wire carries defId == StatusKind). Authored in seconds; Defs
// resolves to ticks (60 Hz) for the deterministic sim. Order MUST match StatusKind. Wired on Game; built by
// Tools > Combat > Build Status Catalog. Mirrors WeaponCatalog.
[CreateAssetMenu(menuName = "Minifantasy/Status Catalog", fileName = "StatusCatalog")]
public sealed class StatusCatalog : ScriptableObject
{
    [System.Serializable]
    public struct Entry
    {
        public string name;            // documentation only
        public float durationSeconds;  // 0 = supplied at apply-time (AttackCooldown)
        public bool blocksMove;
        public bool blocksAttack;
        public float moveScale;        // 1 = none
        public float periodSeconds;    // 0 = no DOT
        public int amountPerTick;
        public StackPolicy policy;
        public byte maxStacks;
        public byte visualId;
    }

    public int maxHp = 100;
    public Entry[] entries;

    StatusEffectDef[] _defs;
    public StatusEffectDef[] Defs => _defs ??= Build();

    StatusEffectDef[] Build()
    {
        int n = entries != null ? entries.Length : 0;
        var defs = new StatusEffectDef[n];
        for (int i = 0; i < n; i++)
        {
            var e = entries[i];
            defs[i] = new StatusEffectDef
            {
                id = (byte)i,
                durationTicks = Mathf.CeilToInt(e.durationSeconds * 60f),
                blocksMove = e.blocksMove,
                blocksAttack = e.blocksAttack,
                moveScale = e.moveScale,
                periodTicks = Mathf.CeilToInt(e.periodSeconds * 60f),
                amountPerTick = e.amountPerTick,
                policy = e.policy,
                maxStacks = (byte)Mathf.Max(1, e.maxStacks),
                visualId = e.visualId,
            };
        }
        return defs;
    }

    void OnValidate() => _defs = null;   // rebuild after Inspector edits
}
