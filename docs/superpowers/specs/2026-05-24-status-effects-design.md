# Status Effects — Stacking, Server-Authoritative, Predicted — Design

- **Date:** 2026-05-24
- **Status:** Approved (brainstorming 2026-05-24) — implementation plan to follow (`writing-plans` next).
- **Parent / builds on:** the in-instance determinism keystone from `2026-05-24-attack-replication-design.md` and `2026-05-24-underworld-collision-prediction-design.md`. Reuses and **subsumes** the existing action-gate WIP (`GateMod`/`AbilityGate`), which was written as the seam for exactly this ("status effects later just call Set/Clear with their own source id" — [AbilityGate.cs:6](Assets/_Scripts/InstanceSim/AbilityGate.cs)).
- **Scope:** A scalable, data-driven **status-effect framework** for in-instance (underworld) combat: timed, stacking effects that are **server-authoritative**, **client-predicted + reconcilable**, and reduce to the existing `GateMod` the deterministic sim already consumes. Plus a minimal server-authoritative **HP pool** so damage and damage-over-time are real. Ships five effects: **HitStun, AttackCooldown, Poison, Freeze, Slow**. In-instance only; overworld untouched.

## 1. Goal

Today there is no status-effect system. Two ad-hoc pieces exist:

1. **Feint cooldown** lives inside the attack state machine as `AttackState.cooldown` ([AttackTypes.cs:42](Assets/_Scripts/Combat/Core/AttackTypes.cs)), set on feint to `tl.feintCooldown` ([AttackLogic.cs:34](Assets/_Scripts/Combat/Core/AttackLogic.cs)), decremented in the Idle branch, and gating attack-start ([AttackLogic.cs:22-23](Assets/_Scripts/Combat/Core/AttackLogic.cs)). It is replicated as quantized bits in `SnapshotEntry.selfExtra` ([SnapshotEntry.cs:17](Assets/_Scripts/Net/SnapshotEntry.cs)) and drives the `Feinted` event ([AttackSimSystem.cs:110](Assets/_Scripts/Net/AttackSimSystem.cs)).
2. **The action gate** (`GateMod` + `AbilityGate`) is a per-player set of source-keyed move/attack blocks reduced to one effective `GateMod`, consumed by `InstanceStep` ([InstanceStep.cs:27-30](Assets/_Scripts/InstanceSim/InstanceStep.cs)), quantized to one wire byte, replicated to the owner ([SnapshotEntry.cs:27](Assets/_Scripts/Net/SnapshotEntry.cs), [ReplicationHub.cs:108](Assets/_Scripts/Net/ReplicationHub.cs)), and predicted by the owner ([PredictionSystem.cs:48,64](Assets/_Scripts/Net/PredictionSystem.cs), [LocalPlayer.cs:28](Assets/_Scripts/Net/LocalPlayer.cs)). It has **no durations, no identity, no stacking-over-time, and no real writers** besides the debug `gate` console command ([CommandBootstrap.cs:105-149](Assets/_Scripts/CommandBootstrap.cs)).

Replace both with one framework: a deterministic, timed, stacking `StatusState` per player that **reduces to `GateMod`** (so the whole sim/wire/predict path below it is reused unchanged), drives **periodic damage** (DOT), carries **identity** for distinct rendering, and is applied **server-authoritatively** at the hit seam (and self-inflicted on feint). The feint cooldown becomes just another effect (`AttackCooldown`), deleting the bespoke `cooldown` field and its wire bits.

### Decisions (Ryan, brainstorming 2026-05-24)
- **Full data-driven framework now**, not a minimal durations-on-`AbilityGate` patch. Effects are catalog data with timing, gate contribution, stacking policy, periodic hook, visual id, and a runtime scaling hook — the data-oriented foundation, so poison/freeze/future effects are pure data.
- **Ship five effects** to exercise every code path: HitStun (root+silence on hit), AttackCooldown (silence on feint, self-predicted), Poison (periodic DOT), Freeze (hard root), Slow (`moveScale`<1). Covers `blocksMove`, `blocksAttack`, `moveScale`, and periodic damage.
- **Real HP pool, no death yet.** Server-authoritative HP, replicated for display, reduced by on-hit damage + DOT, clamped at 0 (log on reaching 0). Death/respawn/invuln are a **separate feature axis**, deferred.
- **Netcode: Approach 1 — deterministic state, pragmatic reconcile.** `StatusState` joins the deterministic per-player sim (stepped by server, owner-predict, and replay) and reduces to `GateMod`. **Self-inflicted effects are predicted and trusted** (the owner derives them from its own input exactly as the server will → authoritative-by-construction, never clobbered by snapshots). **External effects are adopted** from the replicated status block each snapshot, with the existing eased position correction. Built so full rollback (Approach 2) drops in later without reworking the data.
- **Rejected — Approach 2 (full deterministic rollback)** for v1: reconcile re-running the entire sim (movement+attack+status+collision) over un-acked inputs is the rigorous ideal but a large lift the codebase deliberately deferred ("Phase 4"), and v1's effects mostly zero movement so the residual is tiny and eased. Designed as a clean later upgrade.
- **Rejected — Approach 3 (server-only, no effect prediction):** regresses the feint cooldown's current instant feel and is the weakest on the "client-side predictable" requirement.
- **Hit detection = broadphase placeholder.** Hitstun/poison need a victim; the deferred pixel-perfect narrowphase (from the collision spec) is not built here. v1 uses a **broadphase hit query** at the existing `OnStrike` seam (`CollisionStep.Overlap` + a forward/arc check from `lockedAim`). Same seam swaps to the narrowphase later with no wire/client change.
- **On-hit effects are weapon/attack data** (data-oriented trigger), default `[damage, HitStun]`; a poison weapon adds `Poison`. Freeze/Slow are demonstrated via the repurposed debug `effect` console command (and are equally weapon-applicable later).
- **Visuals thin in v1** (SpriteRenderer tint per `visualId` + log), authored on the existing prefab (no runtime `Instantiate`, per the scene-objects preference). Richer particles later.

### Assumptions (reasonable defaults; flag if wrong)
- **Sim 60 Hz, snapshots 30 Hz** ([Game.cs](Assets/_Scripts/Game.cs) `fixedDeltaTime=1/60`; `snapshotHz=30` [ReplicationSettings.cs](Assets/_Scripts/Net/Aoi/ReplicationSettings.cs)). Effect durations are stored in **integer ticks** for deterministic countdown, derived from authored seconds at catalog-build time.
- **Region-only in-instance AOI** ⇒ every same-room player is already in the owner's snapshot (used by the broadphase hit query's client-side rendering and by remote effect rendering). Overworld keeps radius AOI; effects don't apply there.
- **Effect cap = 8 simultaneous** active effects per player (inline fixed buffer; zero-alloc, copyable for the prediction ring). Over cap → drop the instance with the lowest `remainingTicks`; 8 is ample for v1.
- **Self-inflicted effect types are owner-authoritative-by-construction** — only `AttackCooldown` in v1, derived from the feint bit the server also receives. No upstream wire needed to apply it.

### In scope (v1)
- Pure `StatusState` / `StatusEffectDef` / `StatusLogic` (apply/step/reduce) + `StatusCatalog` SO + `StackPolicy`, replacing `AbilityGate`.
- `InstanceStep` extended to step status, derive the gate, and apply self-inflicted effects emitted by the attack step.
- Feint cooldown migrated to the `AttackCooldown` effect; `AttackState.cooldown` and its wire bits deleted.
- Server-authoritative HP pool; on-hit effect lists on attacks; broadphase hit query → damage + effects at `OnStrike`; Poison DOT into HP.
- Replication: status block (self) + active-effect bitmask (remotes) + HP, on the existing AOI snapshot.
- Owner prediction + reconcile of status (Approach 1).
- Thin tint visuals + a health HUD readout; debug `effect` command (replaces `gate`).
- EditMode unit tests for the pure core + a server-vs-replay determinism test.

### Out of scope (deferred)
- **Approach 2 full rollback** (reconcile re-running attack+status+collision); kept as a no-rewrite upgrade path.
- **Death / respawn / invulnerability / i-frames**; HP just clamps at 0.
- **Pixel-perfect hit narrowphase** (broadphase placeholder stands in); per-frame opaque masks.
- **Rich effect visuals** (particles, shaders) beyond a tint; **status-bar UI** beyond a basic HP/effect readout.
- **Resistances / diminishing returns / cleanse / dispel**, crowd-control budgets — the catalog leaves room but none are built.
- Overworld/enemy/NPC effects (no enemies yet); overworld movement unchanged.

## 2. Fit with the existing pipeline (why it's shaped this way)

- **`GateMod` is the contract; everything below it is reused.** The sim already consumes a `GateMod` ([InstanceStep.cs:27](Assets/_Scripts/InstanceSim/InstanceStep.cs)), quantizes it to a wire byte ([GateMod.cs:20-34](Assets/_Scripts/InstanceSim/GateMod.cs)), replicates it, and predicts under it. By making `StatusState` **reduce to `GateMod`** with the same OR-blocks / product-moveScale math `AbilityGate` uses today ([AbilityGate.cs:19-34](Assets/_Scripts/InstanceSim/AbilityGate.cs)), the gate-consuming half of the stack needs no logic change — only its *source* changes from a manual set to a timed collection.
- **Status is a sibling of `AttackState` on the keystone.** The determinism model (server sim == owner predict == replay) already carries `AttackState` through `InstanceStep`. `StatusState` rides the same path: pure data in `Combat/Core`, pure logic in `Minifantasy.InstanceSim`, orchestrated by `AttackSimSystem` (server) and `PredictionSystem` (owner) — exactly mirroring how attack state is handled.
- **The hit seam already exists.** `OnStrike(id, sp, def, tick)` ([AttackSimSystem.cs:121](Assets/_Scripts/Net/AttackSimSystem.cs)) is the documented forward hook for "sweep same-region players and route to a damage spec." v1 fills it with the broadphase query + effect application; the narrowphase swaps in later behind the same seam.
- **The snapshot already has an attack block + a gate byte.** Extending `SnapshotEntry` with a status block / bitmask / HP follows the established hybrid-wire pattern ([SnapshotEntry.cs](Assets/_Scripts/Net/SnapshotEntry.cs)); it rides the existing per-viewer AOI snapshot loop. The lone `gate` byte is **subsumed** (the owner now derives the gate from its status; remotes get a type bitmask instead).
- **Assembly boundaries (do not cross):** pure status **data** (`StatusState`, `StatusEffectDef`, `ActiveEffect`, `StackPolicy`) lives in `Combat/Core` (like `AttackState`/`AttackTimeline`) and stores **raw** gate fields so Combat needs no `GateMod` ref. Pure status **logic** (`StatusLogic`, reduces to `GateMod`) lives in `Minifantasy.InstanceSim` (with `GateMod`). The `StatusCatalog` **SO** lives in `Combat` (like `WeaponCatalog`). Server/owner orchestration + HP + wire live in `Net`/Assembly-CSharp. No pure-core file references Netcode/`Game`/scene. (This is why `StatusEffectDef` holds raw fields, not a `GateMod` — `InstanceSim` refs `Combat`, so the reverse dep would be circular.)
- **Convention** (`Assets/_Scripts/ARCHITECTURE.md`): Input → Logic → Data → Visual. `StatusState`=Data, `StatusLogic`=pure Logic, `AttackSimSystem`/`PredictionSystem`=orchestration Logic, `StatusView`+HUD=Visual.

## 3. Component overview

```
 OWNER CLIENT (FixedUpdate, 60 Hz)                          SERVER (AttackSimSystem, 60 Hz)
 ────────────────────────────────                           ───────────────────────────────
 PredictionSystem.FixedTickInstance:                        StepInstanceFixed (per in-instance player):
   1. StatusLogic.Step(ref status, defs, dt) → gate           1. StatusLogic.Step(ref sp.status, defs, dt)
        (tick durations, expire; DOT events ignored             → gate + DOT events (apply dmg to sp.hp)
         client-side — server owns HP)                         2. InstanceStep.Step(ref atk, ref status,
   2. InstanceStep.Step(ref atk, ref status, ref p, …gate)        ref pos, in cmd, …gate)
        attack→lunge→movement; feint emits AttackCooldown          feint emits AttackCooldown → status
   3. ResolveSelfCollision(p)   (unchanged)                    3. Phase-B collision resolve (unchanged)
   4. store frame{ input, status, predictedPos };             4. On strike: broadphase query → for each victim
      send InputCommand (unchanged)                              apply on-hit effects + damage (external)
                                                              5. AOI snapshot: self status block + HP;
 OnSnapshot:                                                     remotes effect-bitmask + HP
   adopt EXTERNAL effects from status block;                          │
   keep predicted SELF-inflicted effects;                             ▼
   Reconcile(authPos, ackTick, snap)  (unchanged path)        per-viewer SnapshotEntry[]
```

| Unit | Side | Kind | Responsibility |
|------|------|------|----------------|
| `StatusEffectDef` | shared | Data (pure) | One resolved catalog entry: `durationTicks`, raw gate fields (`blocksMove`/`blocksAttack`/`moveScale`), `periodTicks`/`amountPerTick`, `StackPolicy`/`maxStacks`, `visualId`. |
| `StatusState` | shared | Data (pure) | Fixed-cap (8) inline array of `ActiveEffect{ defId, remainingTicks, stacks, sincePeriodTick, appliedTick, selfInflicted }`. Zero-alloc, copyable. |
| `StatusLogic` | shared | Logic (pure) | `Apply` (per `StackPolicy`), `Step` (countdown/expire/periodic events → `GateMod`), `Reduce` (→`GateMod`). No `Time`/random/Netcode. |
| `StatusCatalog` | shared | Data (SO) | Byte-id'd authored effects → `StatusEffectDef[]`; built by a Tools menu; wired on `Game`. |
| `InstanceStep` (extended) | shared | Logic (pure) | Now steps status, derives the gate internally, applies self-inflicted effects the attack step emits. |
| `AttackSimSystem` (server) | server | Logic | Steps status before/with `InstanceStep`; applies DOT to HP; broadphase hit query → on-hit effects/damage; fills snapshot. |
| `PredictionSystem` (owner) | client | Logic | Predicts status forward; stores it per frame; adopts external effects + keeps self-inflicted on snapshot. |
| `ServerPlayer` (extended) | server | Data | Gains `StatusState status` + `int hp`; drops `AbilityGate gate`. |
| `SnapshotEntry` (extended) | wire | Data | Self status block; remote effect bitmask; HP. Replaces the lone `gate` byte. |
| `StatusView` + Health HUD | client | Visual | Tint per `visualId`; HP readout. Reads prediction (self) / snapshot (remotes). |

## 4. The data model

### 4.1 `StatusEffectDef` (resolved, pure — built from the SO)
```
struct StatusEffectDef {
    byte   id;              // index in the catalog == wire id
    // timing
    int    durationTicks;   // 0 = instantaneous (apply periodic/damage once, never persists)
    // gate contribution (raw, so Combat needs no GateMod ref)
    bool   blocksMove;
    bool   blocksAttack;
    float  moveScale;       // 1 = no slow
    // periodic (DOT)
    int    periodTicks;     // 0 = none
    int    amountPerTick;   // damage per period
    // stacking
    StackPolicy policy;     // Refresh | Stack | Independent
    byte   maxStacks;
    // presentation
    byte   visualId;        // data-driven client rendering selector
}
enum StackPolicy { Refresh, Stack, Independent }
```
- **Authored in seconds** on the SO; the catalog builder converts to ticks via fixed dt (`ceil(seconds * 60)`), keeping authoring human and runtime deterministic.
- **Scaling hook (future stats):** `Apply` takes a `float scale = 1f` (and/or per-effect magnitude scale) applied to `durationTicks`/`amountPerTick` at apply-time and stored resolved on the instance — the same "build the seam at 1.0 now" discipline as `PhaseScales` ([AttackTypes.cs:21-28](Assets/_Scripts/Combat/Core/AttackTypes.cs)).
- **Per-caller duration override:** `Apply` also takes an optional `durationTicksOverride` (−1 = use the def's). This is how **AttackCooldown preserves per-weapon cooldowns**: the catalog entry carries no fixed duration; the feint applies it with the attack's authored `feintCooldown` ([AttackTimeline.cs:13](Assets/_Scripts/Combat/Core/AttackTimeline.cs)) as the override. So different weapons keep different cooldowns — no regression from today's per-`AttackDefinition` value.

### 4.2 `StatusState` + `ActiveEffect` (pure runtime)
```
struct ActiveEffect {
    byte  defId;
    int   remainingTicks;    // counts down; <=0 → expired
    byte  stacks;            // >=1; Stack policy increments up to maxStacks
    int   sincePeriodTick;   // ticks since last periodic fire
    uint  appliedTick;       // owner tick at application (reconcile keying)
    bool  selfInflicted;     // predicted+trusted vs adopted-from-server
}
struct StatusState {
    ActiveEffect e0..e7;     // inline fixed buffer (cap 8); count
}
```
Value type so it is copied into the prediction ring buffer per frame (for replay) with no allocation, exactly like `AttackState` is today.

### 4.3 v1 catalog entries (authored)
| Effect | duration | blocksMove | blocksAttack | moveScale | period / amt | policy | visualId |
|--------|---------:|:----------:|:------------:|----------:|:------------:|--------|---------|
| HitStun | short (~0.3s) | yes | yes | 0 | — | Refresh | flash |
| AttackCooldown | `feintCooldown` | no | yes | 1 | — | Refresh | none |
| Poison | medium (~3s) | no | no | 1 | every ~0.5s / N | **Stack** | green tint |
| Freeze | medium (~1.5s) | yes | no | 0 | — | Refresh | blue tint |
| Slow | medium (~2s) | no | no | 0.5 | — | Refresh | (subtle) |

## 5. `StatusLogic` (the new pure core)

```
// Reduce active effects to one effective gate (same math AbilityGate used).
GateMod Reduce(in StatusState s, StatusEffectDef[] defs):
    blockMove=false; blockAttack=false; scale=1
    for each active e in s:
        d = defs[e.defId]
        blockMove   |= d.blocksMove
        blockAttack |= d.blocksAttack
        scale       *= clamp01(d.moveScale)
    return { blocksMove=blockMove, blocksAttack=blockAttack, moveScale=clamp01(scale) }

// One deterministic tick: age effects, expire, accrue periodic events. Returns the gate
// for THIS tick (computed pre-decrement so the effect is "active" the tick it's applied).
GateMod Step(ref StatusState s, StatusEffectDef[] defs, PeriodicSink sink):
    GateMod g = Reduce(s, defs)              // gate reflects current set
    for each active e in s:
        d = defs[e.defId]
        if d.periodTicks > 0:
            e.sincePeriodTick++
            while e.sincePeriodTick >= d.periodTicks:
                e.sincePeriodTick -= d.periodTicks
                sink.Add(e.defId, d.amountPerTick * e.stacks)   // server applies to HP
        if d.durationTicks > 0:
            e.remainingTicks--
            if e.remainingTicks <= 0: remove e
    return g

// Apply per stacking policy. selfInflicted/appliedTick set by caller.
// durationOverride = -1 uses d.durationTicks (AttackCooldown passes the weapon's feintCooldown).
Apply(ref StatusState s, in StatusEffectDef d, uint tick, bool self, float scale=1, int durationOverride=-1):
    dur = (durationOverride >= 0 ? durationOverride : scaled(d.durationTicks))
    switch d.policy:
      Refresh:     find by defId → set remainingTicks = dur; else add (stacks=1)
      Stack:       find by defId → stacks=min(maxStacks,stacks+1), refresh remaining=dur; else add
      Independent: always add a new instance (until cap)
    on cap-overflow: drop the instance with the lowest remainingTicks
```
- **Determinism:** integer math only; `Step` is order-independent for the gate (OR/product) and per-instance for timing. Same inputs → same `StatusState` on server, predict, and replay.
- The `PeriodicSink` is **server-only-consumed** (applies to HP); on the client predictor it's a no-op sink (HP is server-authoritative, replicated — not predicted).

## 6. The deterministic tick (integration into `InstanceStep`)

`InstanceCtx` gains the catalog (`StatusEffectDef[] defs`) and the periodic sink; `InstanceStep.Step` grows a `ref StatusState`:
```
InstanceStep.Step(ref AttackState atk, ref StatusState status, ref Vector2 pos,
                  in InstanceInput cmd, in InstanceCtx ctx):
    GateMod g = StatusLogic.Step(ref status, ctx.defs, ctx.sink)   // age effects → gate
    atk = AttackLogic.Step(atk, cmd.attack, ctx.timeline, ctx.scales, ctx.dt, g.CanAttack)
    if attack step requested an effect (feint → AttackCooldown):
        StatusLogic.Apply(ref status, ctx.defs[AttackCooldownId], cmd.tick, self:true,
                          durationOverride: ctx.timeline.feintCooldownTicks)  // per-weapon
    Vector2? lunge = AttackLogic.LungeVelocity(atk, ctx.timeline)
    Vector2 move = lunge ?? FreeMove(cmd.rawMove, g)
    pos = MovementStep.Step(pos, move, ctx.dt, ctx.speed, ctx.walkable)
```
- **Feint cooldown migration:** `AttackLogic.Step` stops writing `s.cooldown` and instead **signals** "apply AttackCooldown" on feint (return flag / out-param). `InstanceStep` applies it with the attack's per-weapon `feintCooldown` as the duration override (§4.1), taking effect next tick (this tick's gate was already computed). The Idle-branch cooldown check and decrement are **deleted** — attack-start is now blocked purely by `g.CanAttack` (AttackCooldown sets `blocksAttack`). `AttackTimeline` keeps `feintCooldown` (renamed/exposed as `feintCooldownTicks` for the deterministic override).
- **Deletes:** `AttackState.cooldown` ([AttackTypes.cs:42](Assets/_Scripts/Combat/Core/AttackTypes.cs)); `SnapshotEntry.selfExtra` cooldown bits ([SnapshotEntry.cs:17](Assets/_Scripts/Net/SnapshotEntry.cs)); the `Feinted` detection via `cooldown>0` ([AttackSimSystem.cs:110](Assets/_Scripts/Net/AttackSimSystem.cs)) → keyed off the phase transition + an emitted-cooldown flag instead. (`selfExtra`'s `windupComplete` bit stays.)

## 7. Application triggers

- **Self-inflicted — AttackCooldown on feint:** emitted by the attack step (§6), applied identically by server and owner → predicted, never clobbered.
- **On-hit — HitStun/Poison/damage:** new **on-hit effect list** data on the attack/weapon (`{ effectId, magnitude }[]` + base damage; default `[damage, HitStun]`, poison weapon adds `Poison`). At `OnStrike` ([AttackSimSystem.cs:121](Assets/_Scripts/Net/AttackSimSystem.cs)):
  ```
  victims = broadphase(attacker.regionKey, forward arc from lockedAim, weapon range)   // CollisionStep.Overlap + arc test
  for each v in victims (deterministic id order, exclude self):
      v.hp -= weapon.damage
      for each (effectId, mag) in weapon.onHit: StatusLogic.Apply(ref v.status, defs[effectId], tick, self:false, mag)
  ```
  Server-only; victims learn via replication. **Placeholder for the deferred pixel narrowphase** — same seam.
- **Debug — any effect:** the `gate` console command ([CommandBootstrap.cs:105](Assets/_Scripts/CommandBootstrap.cs)) becomes `effect <name> [stacks|secs] | clear [name]`, applying/clearing catalog effects on the host's own player (host-only, same constraint as today). Demonstrates Freeze/Slow/Poison without a weapon.

## 8. Health

- `ServerPlayer.hp` (int), initialized from catalog/config max; reduced by on-hit damage + Poison DOT (via the server `PeriodicSink`); **clamped at 0**, log once on reaching 0 (no death). Server-authoritative; **not predicted** (damage onset is unpredictable, like external effects).
- Replicated per in-instance entry (self + remotes) for HUD/health-bar display. Max HP is catalog/config data (not per-entry on the wire).

## 9. Networking (Approach 1)

### 9.1 Wire
- **Upstream `InputCommand`:** unchanged (the feint bit already exists; effects are server/self-derived).
- **Downstream `SnapshotEntry`:**
  - **Self in-instance entry:** a **status block** replacing the lone `gate` byte — `count:byte` + per-effect `{ defId:byte, remainingTicks:ushort, stacks:byte }`. The owner derives its gate from this (folded with predicted self-inflicted effects). Serialized only when in-instance (mirrors the current gate-byte gating, [SnapshotEntry.cs:27](Assets/_Scripts/Net/SnapshotEntry.cs)).
  - **Remote in-instance entries:** a compact **active-effect-type bitmask** (`byte`, one bit per catalog effect ≤ 8) for cosmetic rendering — no remaining/stacks needed.
  - **HP:** `ushort` current HP on in-instance entries (self + remotes).
- Rides the existing per-viewer AOI snapshot loop ([ReplicationHub.cs](Assets/_Scripts/Net/ReplicationHub.cs)); region-only in-instance AOI already guarantees roommates are present.

### 9.2 Prediction & reconcile (the crux)
- **Owner predicts** its `StatusState` forward each `FixedTickInstance`/`FixedTick`: `StatusLogic.Step` ages effects + derives the gate it then predicts movement under. The per-tick frame stores a **copy of `StatusState`** alongside `predictedPos`.
- **On snapshot (`OnSnapshot`/`Reconcile`, [LocalPlayer.cs:159](Assets/_Scripts/Net/LocalPlayer.cs)):**
  - **External effects:** replace the owner's externally-sourced effects with the authoritative status block (adopt). These are things the owner could not predict (it didn't know it was hit).
  - **Self-inflicted effects:** **kept as predicted** — not overwritten by the block. The owner applies them deterministically from its own input the same tick the server will, so they are authoritative-by-construction. (Edge: a server-rejected feint while stunned is harmless — `blocksAttack` is already set by the stun, so the duplicate cooldown overlaps an existing block.) Merge is keyed by `defId` + `selfInflicted` + `appliedTick`.
  - **Position:** unchanged `Reconcile` (snap or eased `smoothOffset`); `ReplayFrom` continues to replay stored movement vectors. Because v1 external effects mostly zero movement (HitStun/Freeze: `moveScale=0`+`blocksMove`) the post-adopt residual is tiny; Slow's small over-prediction is corrected + eased. (Exact re-derivation under a newly-adopted gate is Approach 2, deferred.)
- **HP** is display-only on the client: read from the snapshot, never predicted.

## 10. Visuals (data-driven, scene-authored)
- `StatusView` (Visual): reads the active-effect set — predicted for self, snapshot bitmask for remotes — and applies a **SpriteRenderer color tint** (and a one-shot log) selected by `visualId`. Authored on the existing player/ghost prefab (toggle existing renderer state; **no runtime `Instantiate`**).
- **Health HUD:** a minimal readout (self HP number/bar; optional small bars over remote ghosts) reading replicated HP.
- Prefab note: if a tint needs no new child objects, the player-prefab build tool (`PlayerPrefabBuildTool`) needs **no change**; if any child is added, update the tool (it regenerates the untracked prefab). Prefer no new children in v1.

## 11. File manifest

**New**
- `Assets/_Scripts/Combat/Core/StatusState.cs` — `StatusState`, `ActiveEffect`, `StackPolicy` (pure data).
- `Assets/_Scripts/Combat/Core/StatusEffectDef.cs` — resolved pure def.
- `Assets/_Scripts/Combat/StatusCatalog.cs` — SO (byte-id'd) → `StatusEffectDef[]` (mirrors `WeaponCatalog`).
- `Assets/_Scripts/InstanceSim/StatusLogic.cs` — `Apply`/`Step`/`Reduce`→`GateMod` (pure; with `GateMod`).
- `Assets/Editor/StatusCatalogBuilder.cs` — `Tools > Combat > Build Status Catalog` → `Assets/_Combat/StatusCatalog.asset` (mirrors `WeaponCatalogBuilder`).
- `Assets/_Scripts/Combat/StatusView.cs` — tint-per-`visualId` visual.
- Health HUD script (UI) — minimal HP readout.
- Tests under `Assets/Tests/EditMode/...` (see §12).

**Modified**
- `Assets/_Scripts/InstanceSim/InstanceStep.cs` — `ref StatusState` + `defs`/sink in `InstanceCtx`; step status, derive gate, apply self-inflicted feint cooldown.
- `Assets/_Scripts/Combat/Core/AttackLogic.cs` — feint **emits** AttackCooldown instead of writing `s.cooldown`; delete the cooldown field reads/decrement.
- `Assets/_Scripts/Combat/Core/AttackTypes.cs` — remove `AttackState.cooldown`.
- `Assets/_Scripts/Combat/AttackDefinition.cs` (+ `AttackTimeline.cs`) — on-hit effect list + base damage; route `feintCooldown` into the AttackCooldown effect.
- `Assets/_Scripts/Net/PlayerRegistry.cs` — `ServerPlayer`: add `StatusState status` + `int hp`; remove `AbilityGate gate`.
- `Assets/_Scripts/Net/AttackSimSystem.cs` — step status (DOT→HP); broadphase hit query → on-hit effects/damage at `OnStrike`; feed snapshot fields; feint event off the new flag.
- `Assets/_Scripts/Net/SnapshotEntry.cs` — status block (self) + effect bitmask (remotes) + HP; remove `gate`/cooldown bits.
- `Assets/_Scripts/Net/ReplicationHub.cs` — pack the new fields; drop `DebugLocalGate` → debug-effect accessor.
- `Assets/_Scripts/Net/PredictionSystem.cs` — predict `StatusState`; store it per frame; derive gate from status; adopt-external/keep-self on reconcile.
- `Assets/_Scripts/Net/LocalPlayer.cs` — hold predicted `StatusState`; wire `OnSnapshot` adoption; replace `currentGate`-from-byte.
- `Assets/_Scripts/CommandBootstrap.cs` — `gate` command → `effect`/`status` command.
- `Assets/_Scripts/Game.cs` — `StatusCatalog` ref + Awake null-guard (the `WeaponCatalog` footgun pattern).

**Deleted**
- `Assets/_Scripts/InstanceSim/AbilityGate.cs` (+ `.meta`) — superseded by `StatusState`/`StatusLogic`. (`GateMod` stays.)

**Unchanged (called out):** `MovementStep`, `CollisionStep` (reused for the broadphase via `Overlap`), `GhostManager` rendering, AOI, `InputCommand`, overworld `PlayerSimSystem`.

## 12. Verification plan

Per the unity-mcp flow (`execute_code` broken; **refresh `scope=all` → poll `editor_state` ready → `read_console` clean → Play → screenshot**; MCP can't drive the IMGUI Host button):
- **EditMode unit tests (pure, new):**
  - `StatusLogic`: Refresh resets duration; Stack increments to `maxStacks` + scales DOT; Independent adds instances; expiry removes at 0; `Reduce` = OR-blocks/product-moveScale; periodic fires on the exact tick cadence; over-cap rule.
  - Migration: feint → AttackCooldown → `g.CanAttack==false` until expiry → attack allowed again; HitStun sets both blocks.
  - **Determinism:** a scripted `InstanceInput` sequence stepped by the "server" path vs the "predict then replay" path yields **identical `StatusState`** (the Approach-1 invariant for self-inflicted effects).
- **MCP-verifiable:** clean compile after `refresh scope=all` (new files!); all EditMode assemblies green; Play boots with no console errors.
- **Manual host (documented — required; the real test):** two clients, one underworld room.
  - Hit a player → they flash + can't move/attack briefly (HitStun) → recover. Attacker's HP-dealt shows on the victim's bar.
  - Feint → cannot re-attack until the cooldown elapses (instant on the owner, no rubberband).
  - `effect poison` on host → HP ticks down on cadence; `effect freeze`/`slow` → movement blocked/slowed and tinted; tints show on the remote's screen too.
  - Slow then move → owner predicts the reduced speed; getting hit produces only a small eased correction.

## 13. Open assumptions & deferred
- **Repo carries WIP** — commit only the files this work touches, each on their own; do **not** `git add` shared WIP (scene, prefabs, unrelated settings/assets). The `StatusCatalog.asset` is a new authored asset (commit it); player-prefab regen output stays untracked.
- **Effect cap (8) + overflow rule** and exact authored durations/damage are tunable; defaults in §4.3 are starting values for the playtest.
- **Slow reconcile residual** is the only non-trivial Approach-1 approximation; if it feels off in the playtest, the fix is Approach 2 (full replay under adopted gate) — designed-for, not built.
- **Deferred:** Approach 2 rollback; death/respawn/i-frames; pixel narrowphase; resistances/cleanse/CC budgets; rich particle visuals; enemy/overworld effects.
