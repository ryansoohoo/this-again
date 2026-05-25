# Weapon Status Effects + 3-Layer Effect Visuals — Design

Date: 2026-05-24
Status: approved (brainstorm), pending spec review → plan

## Summary

Extend the existing status-effect framework so each weapon can carry **one** on-hit status
effect (besides the always-applied HitStun): e.g. **Bleed**, **Fire** (damage-over-time) and
**Fear** (a new forced-flee control effect). When a weapon has an effect:

1. its attack animation gains a themed **FX overlay** across the same anticipation/hit/follow-through;
2. on a successful strike the victim plays a one-shot **status-hit FX**; and
3. while the effect is active the victim plays an over-time **tick FX** that follows them and pulses
   roughly once per damage tick.

Effects become **ScriptableObject assets** so weapons today — and spells/abilities/items later —
can all reference the same effect by pointing at one asset. All three visual layers are **cosmetic**
and ride the status mask that is already replicated; they add no wire. (Fear's client-side prediction
adds one small field — a quantized flee angle — to the self status block; see Netcode.)

## Goals / Non-goals

**Goals**
- Per-weapon single on-hit effect, data-driven via an effect SO referenced by the weapon.
- New effects: Bleed (DoT), Fire (DoT), Fear (forced flee + can't-attack). Poison/Freeze/Slow stay.
- Three cosmetic visual layers (attacker overlay, victim hit one-shot, victim over-time tick).
- A **source-agnostic effect-application seam** so a future spell/ability/projectile/AoE system
  applies effects through the same code path (designed-for, not built).
- Keep the deterministic, server-authoritative sim (the `InstanceStep` keystone) intact and unit-tested.

**Non-goals (later)**
- No spell/ability system, projectiles, cast bars, or mana in this work — only the seam they will use.
- No items/inventory — only that effects are first-class assets items can later grant.
- No full deterministic rollback of the *whole* status history (Approach 2). Fear movement IS
  owner-predicted (the owner runs `InstanceStep` on the adopted flee direction), reusing the existing
  movement reconcile; we are not adding per-tick status rollback beyond what prediction already does.
- No death/respawn (unchanged from current combat).
- More than 8 effect *kinds* (the cosmetic remote mask is one byte; see Constraints).

## Decisions (from brainstorm)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Data model | **Per-effect ScriptableObject asset** (modular, item/spell-friendly) |
| 2 | New mechanics | Bleed/Fire reuse DoT; **Fear gets new sim logic** |
| 3 | Fear behavior | **Flee with direction frozen at hit** + can't attack |
| 4 | Attack visual | **Themed FX overlay** on the weapon swing (reused across weapons) |
| 5 | FX sync | **Cosmetic, no new wire** (driven by the existing effect mask) |
| 6 | Scalability | Effect SO is the shared currency; extract a source-agnostic apply seam |
| 7 | Fear netcode | **Owner-predicted** — client runs `InstanceStep` on an adopted `fleeDir`; +1 quantized ushort on the self status block |
| 8 | Weapon effects | `onHitEffect` is an **array** (multi-effect weapons later); v1 authors one entry |

## Data model

### `StatusEffectAsset` (new SO, `Combat/` asmdef, assets in `Assets/_Combat/Effects/`)

One asset per effect kind. Bundles gameplay + visual data, but the two stay separable so the pure
data never drags sprites into `InstanceSim`.

- **Identity:** `StatusKind kind` — the enum value remains the canonical byte id (catalog index, wire
  defId, mask bit). Code still references `StatusKind.HitStun` / `.Fear` by name.
- **Gameplay** (compiled into the *pure* `StatusEffectDef`, which crosses into `InstanceSim`):
  `durationSeconds, blocksMove, blocksAttack, moveScale, periodSeconds, amountPerTick, policy,
  maxStacks` **+ new** `forcedMove` (`None` / `FleeFrozen`) and `forcedMoveScale` (flee speed as a
  fraction of moveSpeed).
- **Visual** (read **only** by the View layer; never enters `InstanceSim`):
  `tickFx` (Sprite[] + fps + columns), `hitFx` (Sprite[] + fps), `attackOverlayFx` (Sprite[] + fps),
  `tintColor`.

### `StatusCatalog` refactor

- Replace inline `Entry[]` with `StatusEffectAsset[] effects`, ordered so `effects[i].kind == (StatusKind)i`.
- `Defs` still returns `StatusEffectDef[]` (pure) — same consumers (`StatusLogic`, `InstanceStep`,
  `AttackSimSystem`, `PredictionSystem`) unchanged.
- Add `StatusEffectAsset Visual(int defId)` for the View layer to resolve sprites/tint by mask bit.
- `OnValidate` still nulls the cached `Defs`. `StatusCatalogBuilder` (editor) assembles the array from
  the SO assets and validates ordering (kind == index) + that HitStun/AttackCooldown exist at 0/1.
- The catalog is still wired on **Game** (the existing footgun; `Game.Awake` LogErrors if null).

`StatusEffectDef` gains `byte forcedMove` and `float forcedMoveScale`. `ActiveEffect` gains
`Vector2 fleeDir` (frozen flee direction, quantized; zero when the effect has no forced move). The
owner receives `fleeDir` via the self status block, so its prediction matches the server. Both remain
pure value types.

## Effects

### Bleed / Fire (no new logic)

New `StatusEffectAsset` assets + `StatusKind.Bleed=5`, `Fire=6`. Both are DoT (periodSeconds +
amountPerTick), exactly like Poison, with their own numbers, `tintColor`, and FX sheets. Suggested
authoring (tunable): Bleed = `Stack` policy, longer + lower per-tick; Fire = `Refresh`, shorter +
higher per-tick. No code change beyond the enum values and catalog assets.

### Fear (new sim logic; owner-predicted)

`StatusKind.Fear=7`. Authoring: `blocksAttack=true`, `blocksMove=true` (WASD can't fight the flee),
`forcedMove=FleeFrozen`, `forcedMoveScale≈0.8`, a duration, and (optionally) DoT.

- **Apply:** at strike time `OnStrike` knows both positions, so it computes
  `fleeAngle = AimQuant.Encode(victimPos − sourcePos)` (fallback to the strike aim if the two coincide)
  and stores the decoded `fleeDir` on the victim's new `ActiveEffect.fleeDir`. Quantizing through
  `AimQuant` (the same discipline aim uses) guarantees the server and owner derive the identical vector.
- **Step:** `InstanceStep` applies the flee as a move override (same slot as the lunge), and now
  **outputs the move vector it actually applied** so the predictor can buffer the true value:
  ```
  lunge = AttackLogic.LungeVelocity(atk, tl)        // null while feared (attack interrupted)
  move  = lunge ?? InstanceStep.FreeMove(rawMove, g) // gate blocksMove → zero
  if (StatusLogic.ActiveForcedMove(status, defs, out dir, out scale) && dir.sqrMagnitude > eps)
      move = dir * scale                             // forced flee overrides WASD
  pos = MovementStep.Step(pos, move, dt, speed, walkable)
  result.moveApplied = move                          // NEW: the exact pre-collision vector used
  ```
- **Owner-predicted (the key netcode point):** the owner already runs this same `InstanceStep` every
  predicted tick (`PredictionSystem.FixedTickInstance`). Once it has adopted Fear **with `fleeDir`**
  (wired on the self block), its prediction flees identically to the server — no reconcile drift during
  fear. Two changes make this correct:
  1. **Wire `fleeDir`:** the self status block carries one quantized `selfFleeAngle` (ushort) alongside
     the existing per-effect `defId/remaining/stacks`. `AdoptExternal` applies it to the adopted
     forced-move effect. (One field suffices: v1 has a single forced-move kind / one instance at a time.)
  2. **Buffer the real move:** `FixedTickInstance` stores `result.moveApplied` in the input ring instead
     of re-deriving `lunge ?? FreeMove(...)` (which is zero during fear and would make `ReplayFrom` undo
     the flee on any correction). This also removes the existing fragile re-derivation.
  Remotes stay cosmetic (mask only); their position is server-driven as usual.
- **Adopt:** Fear is server-applied + adopted as an external effect (by kind), like hitstun/poison; the
  owner additionally copies `selfFleeAngle` so it can predict the flee.

`StatusLogic` changes: `Apply(...)` gains an optional `Vector2 forcedDir = default` stored on the new
effect (and refreshed on re-apply per policy); new `bool ActiveForcedMove(StatusState, StatusEffectDef[],
out Vector2 dir, out float scale)` that surfaces the highest-priority active forced-move effect.
`InstanceResult` gains `Vector2 moveApplied`.

## Visual layers (cosmetic; driven by the existing mask, no new wire)

All three are SpriteRenderers on `Ghost.prefab`, driven every frame by `GhostManager` (remotes,
`GhostManager.cs:138`) and `LocalPlayer` (self, `LocalPlayer.cs:112`) with the same `mask` byte that
already drives `StatusView`/`DmgView`, resolving `defId → StatusEffectAsset` via `StatusCatalog.Visual`.
They use **their own child renderers**, so they never write another component's `SpriteRenderer.color`
(avoiding the documented multi-writer/full-bright footgun).

- **Layer A — attacker swing overlay** (`AttackView`, +1 rig layer above `weaponFront`):
  when the attacking weapon has a primary on-hit effect (`onHitEffects[0]`), play that effect's `attackOverlayFx`
  driven by **attack phase + progress**, rotated toward `state.lockedAim`/`residualDeg`. Same phase
  timing as the swing → the attack visibly "becomes" themed. No effect → overlay disabled (current
  behavior). Remotes get it free: `weaponId` is already replicated and `AttackView.Render` already
  receives the `AttackDefinition`. Driven by phase/progress (not the weapon's per-direction column), so
  it works with the generic 4×4 PixeLike2 FX sheets and needs no per-direction overlay art.
- **Layer B — victim one-shot status-hit FX** (`StatusFxView`, new component, own `HitFxBody` renderer):
  on an effect's mask bit **rising edge** (0→1), play that effect's `hitFx` once on the victim.
  *Known v1 limitation:* re-applying an already-active effect (bit stays 1) won't replay the one-shot.
- **Layer C — victim over-time tick FX** (`StatusFxView`, own `TickFxBody` renderer):
  while the bit is set, loop the effect's `tickFx` with period = the effect's authored `periodSeconds`
  (pulses ~once per DoT tick) and follows the victim (it's a child of the rig). With multiple active
  effects, show the single highest-priority one (priority mirrors `StatusView`).

`StatusView` (existing tint) is folded in to read `tintColor` from `StatusCatalog.Visual(defId)`
instead of the hardcoded freeze/poison/slow switch, so Bleed/Fire/Fear tint data-drivenly. HitStun
still has no tint (DmgView owns the hurt sprite). Render order stays StatusView → StatusFxView →
DmgView so DmgView's hurt sprite still wins its own renderer.

## Per-weapon binding

`AttackDefinition` keeps an **array** of on-hit effects so multi-effect weapons are possible later,
but each entry now references an effect **asset**: `OnHitEffect[] onHitEffects` where
`OnHitEffect = { StatusEffectAsset effect; float magnitudeScale = 1f; }` (the per-weapon scale is the
data-oriented stat hook, default 1). HitStun is **implicit + always applied** (charge-scaled), so it
is no longer an array entry. **v1 authors exactly one** non-HitStun entry per weapon (the "one effect
per weapon" rule), but the data does not enforce it. `AttackTimeline` carries the resolved entries
(effect defId + scale). `OnStrike` per victim: apply flat `damage`, always apply charge-scaled HitStun,
then loop `onHitEffects` applying each via the seam (below). For the attacker overlay (Layer A) the
**primary (first)** entry's theme is used. `WeaponCatalog` is unchanged (byte id → `AttackDefinition`;
effects are reached via `def.onHitEffects`).

## Scalability for spells/abilities (designed-for, not built)

- **`StatusEffectAsset` is the shared "effect" currency.** Weapons reference it via
  `AttackDefinition.onHitEffects[]`; a future `SpellDef`/`AbilityDef` SO references the same assets. No
  effect logic is weapon-specific.
- **Source-agnostic apply seam.** Extract the per-victim application out of `OnStrike` into
  `Net/CombatEffects.cs`:
  ```
  CombatEffects.ApplyEffect(StatusState target, Vector2 targetPos, Vector2 sourcePos,
                            in StatusEffectDef def, uint tick,
                            float scale = 1f, int durationOverride = -1)
  ```
  It derives the flee direction from `sourcePos` (for `FleeFrozen`) and calls `StatusLogic.Apply`.
  Melee calls it with `sourcePos = attacker.worldPos` now; a future spell/projectile/AoE calls the
  same function with `sourcePos = cast origin / blast center` — so a fireball can fear-away-from-blast.
- **Victim visual layers (B, C) are already source-agnostic** — driven by the victim's mask, so spells
  get hit/tick FX for free. Only Layer A is weapon-bound; spells bring their own caster/projectile
  visuals later but reuse the same per-effect themed FX on the SO.
- **Scaling hook.** The seam's `scale` is the future stat / spell-power hook (data-oriented design):
  defaults to 1 now, scales effect magnitude/duration later. Targets are `ServerPlayer` in v1;
  generalizing to monsters is a later concern, not designed in now (YAGNI).

## Netcode summary

- **DoT (Bleed/Fire):** server applies on hit, ticks in `StatusLogic.Step` (server applies HP), owner
  adopts the effect (gate + visuals). Same as Poison. No new wire.
- **Fear:** server-applied + server-computed quantized `fleeDir`; the self status block carries one
  `selfFleeAngle` (ushort) so the **owner predicts the flee** by running the same `InstanceStep` on the
  adopted `fleeDir` (no reconcile drift during fear). Remotes are cosmetic (mask) + server-driven
  position. This one ushort is the only wire addition in the whole feature.
- **All visuals:** cosmetic, from the existing remote `effectMask` (`SnapshotEntry.effectMask`,
  `ReplicationHub.cs:125`) + replicated `weaponId`/pose. No new wire.

## Constraints / caveats

- **Mask byte is now full.** Kinds 0–7 (HitStun..Fear) fill the cosmetic remote `effectMask` byte
  (`1<<defId`). A 9th effect kind requires widening `effectMask`/`ActiveMask` (byte→ushort) on
  `SnapshotEntry`. Fine for v1; flagged.
- **One-shot hit FX** does not re-fire on re-application of an already-active effect (cheap cosmetic
  tradeoff).
- **Fear is owner-predicted** — the owner runs `InstanceStep` on the adopted `fleeDir`, so the flee is
  smooth locally (no easing toward the server path). Requires the `selfFleeAngle` wire field + buffering
  `InstanceStep`'s applied move so reconcile replay is faithful.
- **`SpriteRenderer.color` has multiple writers** (PlayerView/AttackView/StatusView/DmgView). The new
  views use their own renderers; do not add another writer to the shared body/weapon renderers.

## Build order (one spec, phased — bottom-up)

1. **Data refactor (no behavior change):** `StatusEffectAsset` SO; `StatusCatalog`/`StatusCatalogBuilder`
   migration to the SO array; `StatusEffectDef.forcedMove/forcedMoveScale` + `ActiveEffect.fleeDir`
   (default/unused yet); migrate existing kinds (HitStun, AttackCooldown, Poison, Freeze, Slow) to
   assets; `AttackDefinition.onHitEffects[]` (array of effect-asset refs) replacing `OnHitEffect[] onHit`,
   HitStun now implicit; `OnStrike` updated to the new seam (`CombatEffects.ApplyEffect`) with current
   behavior preserved. Verify: clean compile, existing EditMode tests green, host combat unchanged.
2. **New DoT effects:** author Bleed/Fire assets + `StatusKind` values; assign to a couple of weapons.
3. **Fear (+ owner prediction):** `ActiveForcedMove`, `Apply(forcedDir)`, `InstanceStep` forced-move
   override + `InstanceResult.moveApplied`; `OnStrike` computes the quantized fleeDir; wire
   `selfFleeAngle` on the self block (`SnapshotEntry`/`ReplicationHub`); `AdoptExternal` copies it;
   `FixedTickInstance` buffers `moveApplied`; author the Fear asset; EditMode tests (incl. replay parity).
4. **Visuals:** import/slice the PixeLike2 fx sheets; add the 3 rig renderers via a re-runnable setup
   tool (pre-placed, per the scene-objects preference); `StatusFxView`; `AttackView` overlay layer;
   data-driven `StatusView` tint; wire `GhostManager`/`LocalPlayer` to drive `StatusFxView`.

## Testing

- **EditMode (pure, deterministic):**
  - Bleed/Fire DoT accrual over ticks (periodic damage × stacks; expiry).
  - Fear sets `blocksAttack`; `ActiveForcedMove` returns the stored `fleeDir`; `InstanceStep` flees in
    `fleeDir` and reports it in `moveApplied`.
  - **Fear prediction parity:** with the same `fleeDir` + inputs, `PredictionSystem` live-step and
    `ReplayFrom` (using the buffered `moveApplied`) land on the same position as the server `InstanceStep`.
  - `StatusLogic.ActiveMask` includes the new bits; catalog ordering (kind == index) holds.
  - Server-vs-replay parity of `InstanceStep` given identical inputs + effect data.
- **Manual host test** (the known MCP-can't-click-Host limitation): overlay themed swing per weapon;
  one-shot hit FX on the victim; looping tick FX following the victim; fear flee + reconcile feel; tap
  vs full strike unchanged.

## File-by-file (anticipated)

- `Combat/Core/StatusTypes.cs` — `StatusKind` += Bleed/Fire/Fear; `StatusEffectDef` += forcedMove/scale;
  `ActiveEffect` += `fleeDir`.
- `Combat/StatusEffectAsset.cs` — **new** SO (gameplay + visual fields).
- `Combat/StatusCatalog.cs` — `StatusEffectAsset[]`; `Defs` build from SOs; `Visual(defId)`.
- `Combat/AttackDefinition.cs`, `Combat/Core/AttackTimeline.cs` — `onHitEffects[]` (array of effect-asset
  refs + per-entry scale) replaces `OnHitEffect[] onHit`; HitStun implicit.
- `Combat/AttackView.cs` — overlay rig layer driven by phase/progress.
- `Combat/StatusView.cs` — tint from `Visual(defId).tintColor`.
- `Combat/StatusFxView.cs` — **new** (Layers B + C, own renderers).
- `InstanceSim/StatusLogic.cs` — `Apply(forcedDir)`, `ActiveForcedMove`.
- `InstanceSim/InstanceStep.cs` — forced-move override + `InstanceResult.moveApplied`.
- `Net/CombatEffects.cs` — **new** source-agnostic apply seam.
- `Net/AttackSimSystem.cs` — `OnStrike` uses the seam + computes the quantized fleeDir.
- `Net/SnapshotEntry.cs`, `Net/ReplicationHub.cs` — `selfFleeAngle` (ushort) on the self block.
- `Net/PredictionSystem.cs` — `AdoptExternal` copies `fleeDir`; `FixedTickInstance` buffers `moveApplied`.
- `Net/GhostManager.cs`, `Net/LocalPlayer.cs` — drive `StatusFxView`.
- `Editor/StatusCatalogBuilder.cs` — assemble from SO assets.
- `Editor/<EffectFxSetupTool>.cs` — **new** re-runnable rig/prefab wiring + sheet slicing.
- `Tests/EditMode/...` — Bleed/Fire/Fear + catalog/mask tests.
