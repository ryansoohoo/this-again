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
and ride the status mask that is already replicated; no new wire fields are added.

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
- No owner-prediction of Fear movement (server-authoritative + reconcile in v1; predicted is a documented upgrade).
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
`Vector2 fleeDir` (frozen flee direction; zero when not a forced-move effect or when adopted by a
non-authoritative predictor). Both remain pure value types.

## Effects

### Bleed / Fire (no new logic)

New `StatusEffectAsset` assets + `StatusKind.Bleed=5`, `Fire=6`. Both are DoT (periodSeconds +
amountPerTick), exactly like Poison, with their own numbers, `tintColor`, and FX sheets. Suggested
authoring (tunable): Bleed = `Stack` policy, longer + lower per-tick; Fire = `Refresh`, shorter +
higher per-tick. No code change beyond the enum values and catalog assets.

### Fear (new sim logic; stays off the wire and off the GateMod byte)

`StatusKind.Fear=7`. Authoring: `blocksAttack=true`, `blocksMove=true`, `forcedMove=FleeFrozen`,
`forcedMoveScale≈0.8`, a duration, and (optionally) DoT.

- **Apply:** at strike time `OnStrike` knows both positions, so it computes
  `fleeDir = normalize(victimPos − sourcePos)` (fallback to the strike aim if the two coincide) and
  stores it on the victim's new `ActiveEffect.fleeDir`.
- **Step:** `InstanceStep` applies the flee as a move override (same slot as the lunge). Order:
  ```
  lunge = AttackLogic.LungeVelocity(atk, tl)        // null while feared (attack interrupted)
  move  = lunge ?? InstanceStep.FreeMove(rawMove, g) // gate blocksMove → zero
  if (StatusLogic.ActiveForcedMove(status, defs, out dir, out scale) && dir.sqrMagnitude > eps)
      move = dir * scale                             // forced flee overrides WASD
  pos = MovementStep.Step(pos, move, dt, speed, walkable)
  ```
- **Server vs owner (the key netcode point):** server and owner run the *same* `InstanceStep`. The
  **server's** Fear effect carries `fleeDir` → it flees and the authoritative position replicates. The
  **owner's** adopted Fear has `fleeDir = 0` (not wired) → the override is skipped, the gate's
  `blocksMove` stops it in place, and it reconciles toward the server's fleeing position via the
  existing snap + eased `smoothOffset`. This is **not** a determinism violation of the shared-code
  keystone — it is the same model as "the server has inputs the client doesn't," resolved by position
  reconcile. (Upgrade path: wire `fleeDir` on the self status block to fully predict fear movement.)
- **Adopt:** Fear is server-applied + adopted as an external effect (by kind), exactly like
  hitstun/poison today; the owner needs only the kind (to stop + be unable to attack), not `fleeDir`.

`StatusLogic` changes: `Apply(...)` gains an optional `Vector2 forcedDir = default` stored on the new
effect (and refreshed on re-apply per policy); new `bool ActiveForcedMove(StatusState, StatusEffectDef[],
out Vector2 dir, out float scale)` that surfaces the highest-priority active forced-move effect.

## Visual layers (cosmetic; driven by the existing mask, no new wire)

All three are SpriteRenderers on `Ghost.prefab`, driven every frame by `GhostManager` (remotes,
`GhostManager.cs:138`) and `LocalPlayer` (self, `LocalPlayer.cs:112`) with the same `mask` byte that
already drives `StatusView`/`DmgView`, resolving `defId → StatusEffectAsset` via `StatusCatalog.Visual`.
They use **their own child renderers**, so they never write another component's `SpriteRenderer.color`
(avoiding the documented multi-writer/full-bright footgun).

- **Layer A — attacker swing overlay** (`AttackView`, +1 rig layer above `weaponFront`):
  when the attacking weapon's `AttackDefinition.onHitEffect` is set, play that effect's `attackOverlayFx`
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

`AttackDefinition` gains `StatusEffectAsset onHitEffect` (nullable; the one non-HitStun effect),
replacing the general `OnHitEffect[] onHit`. `AttackTimeline` carries the resolved effect (or its
defId). `OnStrike` per victim: apply flat `damage`, always apply charge-scaled HitStun (as today),
then if `onHitEffect != null` apply it through the new seam (below). `WeaponCatalog` is unchanged
(byte id → `AttackDefinition`; the effect is reached via `def.onHitEffect`).

## Scalability for spells/abilities (designed-for, not built)

- **`StatusEffectAsset` is the shared "effect" currency.** Weapons reference it via
  `AttackDefinition.onHitEffect`; a future `SpellDef`/`AbilityDef` SO references the same assets. No
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
- **Fear:** server-applied + server-computed `fleeDir`; server `InstanceStep` flees → authoritative
  position replicates; owner adopts Fear (stops + can't attack) and reconciles. No new wire.
- **All visuals:** cosmetic, from the existing remote `effectMask` (`SnapshotEntry.effectMask`,
  `ReplicationHub.cs:125`) + replicated `weaponId`/pose. No new wire.

## Constraints / caveats

- **Mask byte is now full.** Kinds 0–7 (HitStun..Fear) fill the cosmetic remote `effectMask` byte
  (`1<<defId`). A 9th effect kind requires widening `effectMask`/`ActiveMask` (byte→ushort) on
  `SnapshotEntry`. Fine for v1; flagged.
- **One-shot hit FX** does not re-fire on re-application of an already-active effect (cheap cosmetic
  tradeoff).
- **Fear is server-authoritative, not owner-predicted** — during fear the owner eases toward the
  server path rather than predicting the flee.
- **`SpriteRenderer.color` has multiple writers** (PlayerView/AttackView/StatusView/DmgView). The new
  views use their own renderers; do not add another writer to the shared body/weapon renderers.

## Build order (one spec, phased — bottom-up)

1. **Data refactor (no behavior change):** `StatusEffectAsset` SO; `StatusCatalog`/`StatusCatalogBuilder`
   migration to the SO array; `StatusEffectDef.forcedMove/forcedMoveScale` + `ActiveEffect.fleeDir`
   (default/unused yet); migrate existing kinds (HitStun, AttackCooldown, Poison, Freeze, Slow) to
   assets; `AttackDefinition.onHitEffect` replacing `onHit[]`; `OnStrike` updated to the new seam
   (`CombatEffects.ApplyEffect`) with current behavior preserved. Verify: clean compile, existing
   EditMode tests green, host combat unchanged.
2. **New DoT effects:** author Bleed/Fire assets + `StatusKind` values; assign to a couple of weapons.
3. **Fear:** `ActiveForcedMove`, `Apply(forcedDir)`, `InstanceStep` override, `OnStrike` fleeDir; author
   the Fear asset; EditMode tests.
4. **Visuals:** import/slice the PixeLike2 fx sheets; add the 3 rig renderers via a re-runnable setup
   tool (pre-placed, per the scene-objects preference); `StatusFxView`; `AttackView` overlay layer;
   data-driven `StatusView` tint; wire `GhostManager`/`LocalPlayer` to drive `StatusFxView`.

## Testing

- **EditMode (pure, deterministic):**
  - Bleed/Fire DoT accrual over ticks (periodic damage × stacks; expiry).
  - Fear sets `blocksAttack`; `ActiveForcedMove` returns the stored `fleeDir`; `InstanceStep` flees when
    `fleeDir` present and stops when absent (server-vs-owner data divergence) — same code path.
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
- `Combat/AttackDefinition.cs`, `Combat/Core/AttackTimeline.cs` — `onHitEffect` replaces `onHit[]`.
- `Combat/AttackView.cs` — overlay rig layer driven by phase/progress.
- `Combat/StatusView.cs` — tint from `Visual(defId).tintColor`.
- `Combat/StatusFxView.cs` — **new** (Layers B + C, own renderers).
- `InstanceSim/StatusLogic.cs` — `Apply(forcedDir)`, `ActiveForcedMove`.
- `InstanceSim/InstanceStep.cs` — forced-move override.
- `Net/CombatEffects.cs` — **new** source-agnostic apply seam.
- `Net/AttackSimSystem.cs` — `OnStrike` uses the seam + computes fleeDir.
- `Net/GhostManager.cs`, `Net/LocalPlayer.cs` — drive `StatusFxView`.
- `Editor/StatusCatalogBuilder.cs` — assemble from SO assets.
- `Editor/<EffectFxSetupTool>.cs` — **new** re-runnable rig/prefab wiring + sheet slicing.
- `Tests/EditMode/...` — Bleed/Fire/Fear + catalog/mask tests.
