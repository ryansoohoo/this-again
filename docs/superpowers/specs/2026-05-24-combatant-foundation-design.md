# Base Hittable Character (Combatant) Foundation — Design

- **Date:** 2026-05-24
- **Status:** Design approved (shape); pending spec review → implementation plan
- **Topic:** Decouple "being a hittable character" from "being a connected player," and introduce the shared character-data model, so future AI plugs into the same combat path players already use.

---

## 1. Context & Motivation

**Future vision:** AI characters that exist in both the overworld and the underworld. Players can fight them **only in the underworld**. AI health is **persistent** and AI **spawning is deterministic**. Combat AI in the underworld will later use an **instruction-priority brain**.

Persistent health + deterministic spawning *force* AI and players to derive from **one shared character-data model** — you cannot deterministically respawn or persist something that isn't fully data-defined. That is the throughline: players and AI are both **characters**, defined by **data**.

**This spec is step 1 of that arc: the base hittable architecture.** It is deliberately the smallest correct seam — a *server-side* decoupling of the combat/hittable state from the player/networking state, plus the shared character-data model. No AI brain, no NPC spawning, no NPC replication/rendering.

**Why this first:** the pure combat sim (`InstanceSim/` — `InstanceStep`, `StatusLogic`, `CollisionStep`, `CombatEffects`) is *already* combatant-agnostic: it operates on plain state (`AttackState`, `StatusState`, `Vector2`) and a bare `ulong` id (`CollisionBody.id`), never on `ServerPlayer`. The only thing welding combat to "player" is **server-side ownership**: `ServerPlayer` mixes networking and combat state; the registry holds only players; the hit sweep iterates only players; HP comes from a single global `StatusCatalog.maxHp`. Decoupling that is the entire foundation. Everything else (spawn / brain / render / persist) builds on top.

---

## 2. Scope

### In
- Extract a `Combatant` base class out of `ServerPlayer` (`ServerPlayer : Combatant`).
- One registry holding **all combatants**; players remain a typed subset for networking/AOI/input.
- Widen the hit seam (`OnStrike`) and the collision broadphase (Phase B) to consider **all combatants in a region**, not just players.
- A pure faction rule `CombatRules.CanHit(attacker, victim)` — the friend/foe gate.
- Shared character data: `CharacterDef` ScriptableObject **+ a deterministic stat-modifier layer**. Migrate `maxHp` off `StatusCatalog` onto `CharacterDef`.
- Two concrete defs authored as data: **Player** and **Goblin** (the first `Enemy`).

### Out (explicitly deferred to later steps)
- AI brain (instruction-priority).
- NPC spawning (deterministic spawner).
- NPC replication + rendering — **`SnapshotEntry`, `GhostManager`, `Ghost.prefab` are all unchanged.**
- HP-persistence *policy* for AI (the foundation stores `hp` durably on the combatant; how/when it persists is later).
- Death / respawn (still none — clamp at 0 and log, as today).
- Goblin visuals (art identified — PixeLike2 `goblin_*` sheets — wired in the render step; this step references no sprite).

**Non-goal guarantee:** existing 2-player host combat behavior is **identical** after this change. Players remain the only combatants; they simply flow through the generalized types.

---

## 3. Architecture

### 3.1 `Combatant` (base class) — default assembly, `_Scripts/Combat/Combatant.cs`

**Inheritance, not composition.** `ServerPlayer : Combatant`. Chosen for minimal churn: every existing `sp.worldPos / sp.hp / sp.status / sp.attackState` access stays valid (inherited), so the only edits at call sites are widening element types in the sweeps. Future AI becomes a sibling `class Npc : Combatant`. (Composition — `ServerPlayer.combat` — reads cleaner but rewrites every `sp.worldPos`→`sp.combat.worldPos` call site and needs owner back-refs for stepping; rejected for this step. Struct-of-arrays DOD is a full rewrite; rejected.)

Lives in the **default assembly** (not `Combat/Core`) because it holds `Queue<AttackEvent>` and `AttackEvent` is defined in `Net/` (default assembly). `Combat/Core` (the pure `Minifantasy.Combat` asmdef) cannot reference it.

**Fields pulled up into `Combatant`** (the hittable / combat-produced state):

| Field | Type | Notes |
|---|---|---|
| `entityId` | `ulong` | Stable id. `= clientId` for players. NPCs draw from a **reserved high range** (documented constant, value finalized in plan) so they never collide with client ids and the existing `ulong` wire / `CollisionBody.id` keep working when AI lands. |
| `faction` | `Faction` | Player / Enemy. |
| `worldPos` | `Vector2` | |
| `regionKey` | `Vector2Int` | `(0,0)` overworld; else underworld room origin. |
| `inInstance` | `bool` | |
| `hp` | `int` | Server-authoritative. `Alive => hp > 0`. |
| `stats` | `Stats` | Per-combatant resolved stats (§3.5). |
| `status` | `StatusState` | Active status effects. |
| `attackState` | `AttackState` | |
| `attackScales` | `PhaseScales` | Per-phase time scaling (the data-oriented timing hook); AI attackers use it too. |
| `weaponId` | `byte` | Catalog id. |
| `prevAttackPhase` | `AttackPhase` | For transition detection. |
| `pendingEvents` | `Queue<AttackEvent>` | Combat-produced; drained into per-viewer RPCs by the networking layer. |

**Fields that stay on `ServerPlayer`** (player / networking / input only):
`motion` (`PlayerMotion`), `submittedInput`, `snap`, `lastProcessedTick`, `lastInput`, `serverInputs` (`RingBuffer<InputCommand>`), `overworldReturnCell`.

> Split principle: **`Combatant` = what the combat sim (hit / collision / status / attack) and shared data touch. `ServerPlayer` = what networking / input touch.** `snap` is a replication concern (don't interpolate across a teleport) → stays on `ServerPlayer` until NPCs replicate.

### 3.2 Registry — `_Scripts/Net/PlayerRegistry.cs`

Keep the class (minimal churn) but expose two views:
- `Dictionary<ulong, ServerPlayer> Players` — **clients only, unchanged**. Drives AOI, snapshots, and the input RPCs keyed by `SenderClientId`.
- `Combatants` — **all combatants** (players now; +NPCs later). Used by the hit + collision phases.

Players register into both. `Add(clientId, …)` stays for players; a general `Register(Combatant)` / `Remove(entityId)` is added for the future spawner. (Renaming to `CombatantRegistry` is possible but touches several files; decision deferred to the plan — lean is keep the name + add the view.)

### 3.3 Faction + `CombatRules.CanHit` — pure, `Combat/Core/` (`Minifantasy.Combat`)

```
enum Faction : byte { Player, Enemy }   // + Neutral later
```

`CombatRules.CanHit(Faction attacker, Faction victim)` — pure faction matrix, unit-tested:
- Player ↔ Player → **true** (preserves current in-instance PvP; party/friendly-fire refinement is later)
- Player ↔ Enemy, Enemy ↔ Player → **true**
- Enemy ↔ Enemy → **false**

**The "underworld-only" gate is the existing `inInstance` / region check** in `OnStrike` (`if (!victim.inInstance || victim.regionKey != sp.regionKey) continue;`) plus the self-skip. These already enforce "combat only in an instance" and now apply uniformly to AI — an overworld AI has `inInstance = false`, so it is automatically unhittable. `OnStrike` adds exactly one line: `if (!CombatRules.CanHit(attacker.faction, victim.faction)) continue;`.

> `CanHit` is pure so it is reusable by the future client-side hit prediction, not just server hit detection.

### 3.4 Hit + collision sweeps widen to combatants — `_Scripts/Net/AttackSimSystem.cs`

- **`OnStrike`** iterates `_reg.Combatants` in the attacker's region (was `_reg.Players`). A victim is **any `Combatant`** — stationary ones (e.g. a standing goblin, `invMass = 0`) are still hittable. `ApplyDamage`'s parameter generalizes `ServerPlayer` → `Combatant` (it only touches `hp`).
- **Collision (Phase B)** builds `CollisionBody` from **all combatants in each region** (was the player-only `_pending`), with `invMass = 1` if the combatant moved this tick else `0` (pinned). So a standing goblin can't be walked through. `CollisionStep.Resolve` is unchanged (already id-based).
- **Phase A (input integration)** still iterates **`Players`** — only players have input this step; AI integration is the brain step and will add AI to the moved-this-tick set before Phase B. For now the combatant set == players, so behavior is identical; the seam is what changes.

Concretely: capture each combatant's `startPos`, integrate players from input (Phase A), then group **all** combatants by region and resolve (Phase B), deriving `invMass` from `worldPos != startPos`.

### 3.5 Shared character data + stat-modifier layer

**`CharacterDef`** ScriptableObject — `_Scripts/Combat/CharacterDef.cs` (default assembly, alongside `WeaponCatalog` / `StatusCatalog`):
- identity: `id` / display name (`"Player"`, `"Goblin"`)
- `faction` (`Faction`)
- base stats: `maxHp` for v1, authored as a base-stats block indexed so new stats (`damageMod`, `defense`, `moveSpeed`, `mass`, …) slot in without a schema break.
- `CreateStats()` → builds a `Stats` seeded with this def's base values.
- *(future, not v1: default `weaponId`, visual / prefab ref — noted, not added.)*

**Stat-modifier layer** — pure, `_Scripts/Combat/Core/Stats.cs` (`Minifantasy.Combat`):
- `enum StatKind : byte { MaxHp = 0 }` (grows: `Damage`, `Defense`, `MoveSpeed`, `Mass`, …)
- `enum ModOp : byte { Add, Mul }`
- `struct StatModifier { StatKind stat; ModOp op; float value; int sourceId; }`
- `class Stats`: holds base values (a `float[]` indexed by `(int)StatKind`, fed by `CharacterDef` — **`Stats` does not reference `CharacterDef`**, keeping it pure and in Core) plus a `List<StatModifier>`. Resolution:

  ```
  effective(stat) = (base + Σ add) × Π mul
  ```

  **Order-independent** (all adds, then all muls) → deterministic regardless of insertion order. `GetInt(MaxHp)` rounds to int. `Add(modifier)` / `RemoveBySource(sourceId)` support future equip/unequip/buff-expire. v1: the modifier list is usually empty; the **mechanism** is what we are building.

> This is the **persistent base-stat** layer (items / levels / long-lived buffs that change max HP, damage, defense). It is intentionally **separate** from the transient `StatusState` / `GateMod` layer (hitstun / slow / freeze / DoT, per-tick, time-limited). Different lifetimes; they do not merge. `StatusEffectDef.moveScale` etc. remain the transient path.

**`maxHp` migration:** remove `StatusCatalog.maxHp`; `CharacterDef.maxHp` (via `Stats`) replaces it. `Combatant.hp` initializes to `stats.GetInt(MaxHp)`. Repoint all readers (`ReplicationHub` enter-instance; `StatusCatalogBuilder` editor tool; any HUD reading it). The player's `CharacterDef` is wired on `Game` (new field, like the catalogs).

### 3.6 HP / alive semantics

`hp` lives on `Combatant`. Player enter-instance still resets `hp = stats.GetInt(MaxHp)` ("full HP each run" — preserved, now sourced from stats). `ApplyDamage` clamps at 0 and logs once; **no death/respawn**. `Alive => hp > 0` is exposed for future use. AI HP-persistence policy is deferred — the foundation just stores `hp` durably on the combatant (it is not auto-reset except by the player enter path).

---

## 4. The Goblin (first Enemy)

Author two `CharacterDef` assets under `Assets/_Combat/Characters/`:
- **`Player.asset`** — `faction = Player`, `maxHp = 100` (today's value).
- **`Goblin.asset`** — `faction = Enemy`, `maxHp` default (e.g. `60`, **tunable data** Ryan owns).

Created by a small editor tool **Tools > Combat > Build Character Defs** (mirrors `WeaponCatalogBuilder` / `StatusCatalogBuilder`): creates the two defs with default stats and assigns the **Player** def onto `Game`. Re-runnable; Ryan tunes the numbers in the Inspector afterward. (Consistent with the existing catalog-builder pattern; the value tuning stays Ryan's.)

Goblin art exists — PixeLike2 `goblin_dagger / goblin_spear / goblin_archer / goblin_mage / goblin_mechanic` — and is wired in the later render step. This step references no sprite.

---

## 5. What does NOT change

`SnapshotEntry` / the wire, `GhostManager`, `Ghost.prefab`, AI, NPC spawning, death/respawn, and all player-facing combat behavior. The pure sim (`InstanceStep` / `StatusLogic` / `CollisionStep` / `CombatEffects`) is untouched.

---

## 6. Determinism & data-oriented notes

- `Stats` resolution is pure, order-independent, integer for HP → deterministic. The modifier layer **is** the runtime scaling hook requested in [[data-oriented-deterministic-design]].
- `CombatRules.CanHit` is pure → unit-testable and reusable by future client-side hit prediction.
- A `Combatant` is fully constructible from `CharacterDef` (+ modifiers) → **deterministic-spawn-ready** (the spawner itself is a later step).

---

## 7. Verification

**EditMode tests** in `Minifantasy.Combat.Tests`:
- `CombatRules.CanHit` — the full faction matrix (Player/Enemy combinations, both directions, self).
- `Stats` — base only; single Add; single Mul; Add+Mul order-independence; HP rounding; `RemoveBySource`.

**MCP gates** (per [[unity-mcp-verification]]): `refresh_unity scope=all` (new `.cs` files must import before compile), clean compile via `read_console`, Play boot with no errors.

**No new host test required** — player combat behavior is unchanged. (Prior features' pending host tests are unaffected by this change.)

---

## 8. Footguns to respect

1. **Game-wiring footgun** (recurring): the new player `CharacterDef` ref MUST be assigned on `Game` (the `GridManager` instance, as a scene override). `Game.Awake` LogErrors if null — same class of bug as the `WeaponCatalog` / `StatusCatalog` footguns.
2. **`maxHp` migration**: grep every `StatusCatalog.maxHp` reader and repoint it; remove the now-dead field. Don't leave two sources of max HP.
3. **Asmdef boundaries** (cf. the `Commands` asmdef lesson): pure types (`Faction`, `StatKind`, `ModOp`, `StatModifier`, `Stats`, `CombatRules`) → `Combat/Core/` (`Minifantasy.Combat`). `Combatant` → default assembly (it refs `AttackEvent` in `Net/`). Do **not** put `Combatant` in Core.
4. **New `.cs` files**: `refresh_unity scope=all`, not `scope=scripts`.
5. **Curated assets**: create the new `CharacterDef` assets via the builder tool with sensible defaults; Ryan tunes the stat values (cf. [[feedback-biome-assets]] — don't silently overwrite values Ryan has tuned).

---

## 9. Future steps this unblocks (not in scope)

1. **NPC replication + rendering** — reserve the NPC id range on the wire; `GhostManager` renders `Combatant`s by `CharacterDef` → goblin sprites.
2. **Deterministic NPC spawner** — `CharacterDef` + site seed → `Combatant` placed in a room.
3. **AI brain** (instruction-priority) — drives `Npc` "input" → the same `InstanceStep`.
4. **AI HP-persistence policy**.
5. **Pixel-perfect hitbox narrowphase** — already planned; lands behind the same `OnStrike` seam.
6. **Party / friendly-fire refinement** in `CanHit`.
