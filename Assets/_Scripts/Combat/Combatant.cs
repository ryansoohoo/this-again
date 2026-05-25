using System.Collections.Generic;
using UnityEngine;

// Base for every hittable character — players today, AI later (`class Npc : Combatant`). Holds the combat/spatial
// state the sim, the OnStrike hit seam, and the collision broadphase operate on; deliberately free of any
// player/networking/input concerns (those stay on ServerPlayer). Server-only — nothing here replicates directly;
// the snapshot stream carries derived values. Lives in the default assembly (it references AttackEvent in Net/).
public class Combatant
{
    public const ulong NpcIdBase = 1UL << 40;   // NPC entityIds start here so they never collide with NGO clientIds

    public ulong entityId;                  // stable id; == clientId for players, >= NpcIdBase for NPCs
    public Faction faction = Faction.Player;
    public Vector2 worldPos;
    public Vector2Int regionKey;            // (0,0) overworld; else underworld room interior origin
    public bool inInstance;

    public int hp;                          // server-authoritative; set from stats on instance enter
    public bool Alive => hp > 0;
    public Stats stats;                     // resolved character stats (maxHp now; scaling hooks for the rest)

    public readonly StatusState status = new();   // active status effects; reduces to the gate the sim consumes

    // Authoritative attack state (in-instance only). Stepped via the shared InstanceStep.
    public AttackState attackState;
    public PhaseScales attackScales = PhaseScales.One;
    public byte weaponId;                   // equipped weapon (catalog id)
    public AttackPhase prevAttackPhase;     // for transition detection (events + hit seam)
    public Queue<AttackEvent> pendingEvents;    // drained into per-viewer event RPCs each snapshot
}
