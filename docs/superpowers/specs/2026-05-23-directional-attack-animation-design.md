# Cursor-Aimed Attacks: Commitment & Feint Flow — Design

- **Date:** 2026-05-23
- **Status:** Approved (brainstorming) — ready for implementation plan
- **Scope:** A scalable, **data-oriented** attack system with a commitment/feint flow: hold left-click to wind up (re-aimable), release to commit a locked-direction strike, right-click to feint (cancel into a cooldown). Timing is **per-frame, per-phase, in data** (anticipation / hit / follow-through), with a separate **tap** wind-up. Strikes point exactly at the cursor (nearest authored direction + residual rotation). **Local-visual only** — the hit frame is *marked* (deterministic), but **no damage/hitbox resolution** and no network replication.

## 1. Goal

Hold LMB to wind up an attack aimed at the cursor (re-aimable while holding). Release to **commit**: direction locks and it strikes (hit frame) then recovers (follow-through). A quick **tap** plays a short, separately-authored wind-up then strikes. **RMB** during the wind-up **feints** — cancels into a cooldown.

This is the commitment/feint combat the underworld movement spec deferred, built **local-visual first**: the full input→flow→animation loop, with the hit frame *marked* for a later damage spec.

**Data-oriented timing is a first-class requirement:** every frame's duration lives in the asset (no Unity animation markers), grouped by phase, so the time until the hitbox appears is **deterministic and computable from data**, and each phase's speed is **independently scalable at runtime** (future stats: slows, buffs). Scalability also means direction is per-attack data — one pure picker serves diagonal (Slash) and cardinal (Thrust/Ranged) attacks unchanged.

### Decisions (Ryan, brainstorming 2026-05-23)
- **Flow:** hold LMB → wind up (re-aimable, feintable); release → commit (locked direction, strike + recover); RMB during wind-up → feint → cooldown.
- **Timing is per-frame, in data, no animation markers.** Each phase is an ordered list of `(spriteColumn, duration)`; frames may skip columns and have distinct durations.
- **Tap = a separate authored wind-up.** Releasing **before** the hold wind-up finishes plays the asset's `tapAnticipation` list (e.g. column 1→3 @0.1s), then the hit. Releasing **after** the hold wind-up completes strikes directly from the wound-up frame.
- **Deterministic time-to-hit** computed from the data (sum of wind-up durations × scale).
- **Per-phase runtime time-scale for stats** (`anticipationScale` / `hitScale` / `followThroughScale`, default 1.0; `tapAnticipation` scales with `anticipationScale`). v1 ships the hook at 1.0; stats plug in later.
- **Hit scope = mark + animate only.** Hit frames set an `InHitWindow` flag (+ a one-shot log); **no hitboxes/damage** in v1.
- **Aim = nearest anim + free tilt** (closest authored direction, then rotate the rig by the residual). Whole-rig tilt by default; weapon-only tilt is a free fallback. ≤45° lean for a 4-direction set.
- **Attack-body + weapon, 3 composited layers** (weapon-behind / attack-body / weapon-front). **No mirroring** — directions are sheet rows; `_f`/`_b` are render layers.
- **Code-driven sprite playback**, not Animator states.
- **Local-visual first.** Only the local player attacks in v1. Intent edges `(pressed/held/released/feint, aimDir, +future attackId)` are the server-replication seam.
- **Input:** LMB hold/release = attack; RMB = feint; WASD stays movement; **drop double-click-to-move**.

### Assumptions (reasonable defaults; flag if wrong)
- **Feint allowed only while LMB is held in the wind-up** (the `Anticipation` phase, before release). Once released (tap or full), the attack is **committed** — no feint, no re-aim, direction locked through follow-through.
- **Any early release is a "tap"** (plays `tapAnticipation`); only releasing after the hold wind-up has fully played is a "full" strike.
- **Cooldown only after a feint.** A completed attack returns to idle with no extra cooldown.
- **Anticipation holds the last wind-up frame** while held (doesn't loop); **feint snaps to idle** (no feint art).
- **Movement isn't locked** during attacks (the attack-body just overrides the walk anim).

### In scope (v1)
- Pure, testable **direction picker** `(dirs, aim) → (index, residualDeg)`.
- **`AttackDefinition`** SO: 3 sliced sheets + `columnsPerRow`; **timed-frame phase lists** (`anticipation`, `tapAnticipation`, `hit`, `followThrough`); `feintCooldown`; per-row direction vectors; a deterministic `TimeToHit` helper.
- A **`PhaseScales`** hook (per-phase multipliers, default 1) — the stat seam.
- **`AttackState`** with a phase + cooldown timer; **`AttackSystem`** state machine (owner-only logic, owned by `LocalPlayer`).
- **3-layer attack rig** + phase-aware **`AttackView`** on `Ghost.prefab`.
- **Input** edges (LMB down/held/up, RMB) + cursor aim; remove double-click-to-move.

### Out of scope (deferred)
- **Damage, hitboxes, target detection, posture/block/parry/dodge** — combat-hit spec. v1 only *marks* the hit window.
- **Stat values themselves** (the buffs/slows) — only the scaling hook ships now.
- **Network replication of attacks**, combos/input buffering, re-aim after commit, move-slow on windup, dedicated feint art, weapon selection, pointer-over-UI suppression.

## 2. Sprite-sheet reality (verified from the art)

Each Minifantasy attack ships up to three sheets, each a **4-row grid** sliced 32×32 row-major by [SpriteGridSliceTool.cs](Assets/Editor/SpriteGridSliceTool.cs): `*_Characters_*` (the attacking body), `*_f` (weapon in front of body), `*_b` (weapon behind body; blank for a sword slash, populated for two-handers / the bow's North frame). **4 rows = 4 directions; columns = frames** (count varies per attack). Frame `(row, col)` = `sheet[row*columnsPerRow + col]`. Convention is per-attack: **Slash diagonal**, **Thrust/Ranged cardinal** — hence direction and phase timing live in per-attack data.

## 3. Attack phases & the state machine

Each `AttackDefinition` holds four **timed-frame lists** (`TimedFrame { int column; float duration; }`), shared across all directions:
- **`anticipation`** — the hold wind-up: plays in order, then **holds** the last frame while LMB is held. Re-aimable, feintable.
- **`tapAnticipation`** — the quick wind-up played on an early release, before the hit.
- **`hit`** — the strike; `InHitWindow` is true here.
- **`followThrough`** — recovery.

A frame's real duration = `duration * phaseScale` (`anticipationScale` for `anticipation`+`tapAnticipation`, `hitScale`, `followThroughScale`; all default 1.0).

**States:** `Idle`, `Anticipation`, `TapWindup`, `Hit`, `FollowThrough`. The last three are "committed" (direction locked, no feint, no re-aim). A feint cooldown is a timer checked in `Idle`.

```
        LMB down (cooldown elapsed)
  Idle ───────────────────────────► Anticipation ──── RMB ──► feint: cooldownRemaining = feintCooldown ─► Idle
   ▲                                  │  re-Pick(aim) every tick (re-aim)
   │                                  │  play anticipation[]; hold last frame when done
   │                                  │
   │                          LMB up / not held  ── lock direction ──┐
   │                                  │                               │
   │              ┌── released BEFORE anticipation done (TAP) ────────┤
   │              ▼                                                    │
   │        TapWindup ── play tapAnticipation[] ──►  Hit ◄────────────┘ (released AFTER done: straight to Hit)
   │                                                  │ play hit[]  (InHitWindow)
   │                                                  ▼
   └────────── (queue empty) ◄──── FollowThrough ◄────┘  play followThrough[]
```

Transition detail (`AttackSystem.Tick(dt, intent, def)`), using a small per-phase frame cursor `(frameIndexInPhase, phaseElapsed)`:
- **Idle:** `if (cooldownRemaining>0) cooldownRemaining -= dt;` then `if (intent.pressed && cooldownRemaining<=0 && def!=null)` → **Anticipation** (load `anticipation`, cursor=0, initial `Pick`).
- **Anticipation:** re-`Pick(def.Dirs, intent.aimDir)` each tick; advance the cursor through `anticipation` (clamp-hold on the last frame when exhausted — `windupDone`).
  - `intent.feint` → **Idle**, `cooldownRemaining = def.feintCooldown`.
  - `intent.released || !intent.held` → **lock** `dirIndex/residualDeg`; if `!windupDone` → **TapWindup** (load `tapAnticipation`), else → **Hit** (load `hit`).
- **TapWindup:** advance through `tapAnticipation`; on done → **Hit** (load `hit`).
- **Hit:** advance through `hit`; on done → **FollowThrough** (load `followThrough`). `InHitWindow` true throughout.
- **FollowThrough:** advance through `followThrough`; on done → **Idle**.

A same-frame press+release enters Anticipation then commits next tick via `!intent.held` (handles instant taps → TapWindup). Derived: `InHitWindow = phase==Hit`; `IsAttacking = phase != Idle`. On the `InHitWindow` rising edge, emit one `GameLog` (verification + future-damage seam).

**Determinism:** `def.TimeToHit(tapped, scales)` = `(tapped ? Σ tapAnticipation.duration : 0) * anticipationScale` — the time from release/commit to the first hit frame, straight from data.

## 4. Fit with the existing architecture

- **Convention** (`Assets/_Scripts/ARCHITECTURE.md`): Input → Logic → Data → Visual. New types live in **`Assets/_Scripts/Combat/`**: the pure core (picker, timed-frame state machine, data structs) in a `Combat/Core/` **`Minifantasy.Combat` asmdef** (leaf, UnityEngine-only — mirrors the `Net/Movement` `Minifantasy.Movement` asmdef so the logic is unit-testable), and the game-coupled glue (`AttackDefinition`, `AttackSystem`, `AttackView`) in Assembly-CSharp. Not in the `Minifantasy.Commands` or `Net/Aoi` asmdefs.
- **Player visual = `Ghost.prefab`** ([GhostManager.cs:47](Assets/_Scripts/Net/GhostManager.cs)), instantiated per player; carries [PlayerView.cs](Assets/_Scripts/Player/PlayerView.cs) + Animator + layered body `SpriteRenderer`s. The rig + `AttackView` are **authored on this prefab** (scene-objects-over-runtime-instantiation), present on every ghost, driven only for the self.
- **Owner input + per-owner logic home:** [LocalPlayer.cs:68](Assets/_Scripts/Net/LocalPlayer.cs) reads `PlayerInput.Read(cam)` each `Update` and owns `PredictionSystem` as a plain logic class. `AttackSystem` mirrors it — a plain class owned/ticked by `LocalPlayer`, writing `AttackState`, read by the self-ghost's `AttackView`.
- **Decoupled from netcode:** runs in `Update` off the cursor; no RPCs, fixed-tick, or snapshots. The intent **edges are the future server seam** (commitment combat will be server-authoritative).
- **`PlayerView` coordination:** while `IsAttacking`, `AttackView` hides the normal body renderers and shows the rig; `PlayerView` keeps writing params underneath (harmless), resumes when idle. Day/night tint in `PlayerView.LateUpdate` tints all child `SpriteRenderer`s (cached `GetComponentsInChildren(true)`), so rig layers tint automatically.

## 5. Component overview

```
LocalPlayer.Update (local owner only)
  in = PlayerInput.Read(cam) → { moveDir, lmbDown, lmbHeld, lmbUp, rmbDown, cursorWorld }
  aim = cursorWorld − SelfWorldPos
  intent = { pressed=lmbDown, held=lmbHeld, released=lmbUp, feint=rmbDown, aimDir=aim }
  AttackSystem.Tick(dt, intent, currentAttack)        ── §3 state machine; writes AttackState
  SelfGhost.AttackView.Render(AttackSystem.State, currentAttack)   ── visual only

Ghost.prefab
  ├─ (normal body SRs, Animator, PlayerView)          ← hidden while IsAttacking
  ├─ AttackView                                         ← driven only for the self-ghost
  └─ AttackRig (pivot; localEulerAngles.z = residual)
       ├─ WeaponBack  SR   (sortingOrder = body − 1)
       ├─ Body        SR   (sortingOrder = body + 0)
       └─ WeaponFront SR   (sortingOrder = body + 1)
```

| Unit | Kind | Responsibility |
|------|------|----------------|
| `AttackDirections` | Logic (pure static) | `Pick(dirs, aim) → (index, residualDeg)`: nearest by max dot, residual via `Vector2.SignedAngle`. Scene-free → unit-tested. |
| `TimedFrame` | Data (struct) | `{ int column; float duration; }` — one timed sprite frame. |
| `AttackDefinition` | Data (ScriptableObject) | One asset per attack: 3 row-major `Sprite[]` + `columnsPerRow`; `anticipation`/`tapAnticipation`/`hit`/`followThrough` (`TimedFrame[]`); `feintCooldown`; `DirectionEntry[]`; `attackId`; `TimeToHit(tapped, scales)`. |
| `PhaseScales` | Data (struct) | `{ float anticipation, hit, followThrough; }` default 1.0 — the runtime stat hook. |
| `AttackState` | Data (plain class) | `phase, def, dirIndex, residualDeg, frameIndexInPhase, phaseElapsed, cooldownRemaining`; derived `CurrentColumn`, `InHitWindow`, `IsAttacking`. |
| `AttackIntent` | Data (struct) | `{ bool pressed, held, released, feint; Vector2 aimDir; }` (+future `attackId`). Server seam. |
| `AttackSystem` | Logic (plain class) | Owned/ticked by `LocalPlayer`. The §3 state machine over the timed-frame lists. Writes `AttackState` only. |
| `AttackView` | Visual (`MonoBehaviour` on `Ghost.prefab`) | `Render(state, def)`: body↔rig swap on `IsAttacking`, set the 3 layers' sprites for `row*columnsPerRow + CurrentColumn`, set pivot rotation. |

## 6. Data

### 6.1 `AttackDefinition` (`Combat/AttackDefinition.cs`, ScriptableObject, `[CreateAssetMenu]`)
```
Sprite[] bodyFrames, weaponFrontFrames, weaponBackFrames;  // row-major; back may be empty
int columnsPerRow;                 // sheet columns (for row*cols+col indexing)
TimedFrame[] anticipation;         // hold wind-up (plays then holds last; re-aimable; feintable)
TimedFrame[] tapAnticipation;      // quick wind-up on early release (e.g. [{1,0.1},{3,0.1}])
TimedFrame[] hit;                  // strike (InHitWindow)
TimedFrame[] followThrough;        // recovery
float feintCooldown = 0.5;         // lockout after a feint
DirectionEntry[] directions;       // canonicalDir + row, per direction
string attackId;
float TimeToHit(bool tapped, PhaseScales s)  // (tapped ? Σ tapAnticipation.duration : 0) * s.anticipation
struct TimedFrame    { int column; float duration; }
struct DirectionEntry{ Vector2 canonicalDir; int row; }
```
- **Authoring:** an editor tool populates the three `Sprite[]` + `columnsPerRow` from an assigned sliced texture (sub-sprites in slice order); a `Diagonal`/`Cardinal` preset fills `directions`' vectors (author confirms each `row`); the four phase lists + `feintCooldown` are set in the Inspector. Runtime reads fields only — no `AssetDatabase`/`Resources`.
- Validation (editor): every `TimedFrame.column < columnsPerRow`; `anticipation`, `hit`, `followThrough` non-empty (`tapAnticipation` may default to the first anticipation column); array lengths `== columnsPerRow * rowCount`.

### 6.2 `AttackState` (`Combat/AttackState.cs`, plain class)
- `enum Phase { Idle, Anticipation, TapWindup, Hit, FollowThrough }`
- `Phase phase; AttackDefinition def; int dirIndex; float residualDeg; int frameIndexInPhase; float phaseElapsed; float cooldownRemaining;`
- `int CurrentColumn` → the current phase list's `[frameIndexInPhase].column` (the wound-up hold returns the last anticipation column).
- `bool InHitWindow => phase==Phase.Hit;`  `bool IsAttacking => phase != Phase.Idle;`

### 6.3 `AttackIntent` (`Combat/AttackIntent.cs`, struct)
- `bool pressed, held, released, feint;` — LMB down/level/up + RMB down; all false while `InputState.Typing`.
- `Vector2 aimDir;` — `(cursorWorld − selfWorldPos)`; picker normalizes. Future: `attackId`.

## 7. Logic

### 7.1 `AttackDirections.Pick` (`Combat/AttackDirections.cs`, pure static)
- `aimN = aim.normalized` (return `(0,0)` if `aim≈0`); `index = argmax_i dot(dirs[i].normalized, aimN)`; `residualDeg = Vector2.SignedAngle(dirs[index], aim)` (CCW-positive, matches Unity Z-euler so `pivot.localEulerAngles.z = residualDeg` aims it; negate if art is mirrored).
- EditMode tests: exact-hit = 0 residual, ≤45° for a 4-set, correct nearest at cardinal/diagonal midpoints, both presets.

### 7.2 `AttackSystem` (`Combat/AttackSystem.cs`, plain class owned by `LocalPlayer`)
- Holds `AttackState State` and a `PhaseScales scales` (default 1; settable later by stats).
- `void Tick(float dt, AttackIntent intent, AttackDefinition def)` — the §3 state machine. A private `AdvancePhase(list, scale, dt)` accumulates `phaseElapsed`, steps `frameIndexInPhase` when it exceeds `list[i].duration*scale`, and returns whether the list is exhausted (the Anticipation phase clamp-holds instead of finishing). On the `InHitWindow` rising edge, emit one `GameLog`.
- Pure logic: no scene access beyond the `def`/intent passed in.

### 7.3 `LocalPlayer` wiring (`Net/LocalPlayer.cs`, modified)
- Add `readonly AttackSystem attack = new();` + serialized `AttackDefinition currentAttack`.
- In `Update`, when `SelfWorldPos.HasValue`: build `AttackIntent` from the `PlayerInput` read + `aim = intent.cursorWorld − SelfWorldPos.Value`; `attack.Tick(Time.deltaTime, intent, currentAttack)`; resolve+cache the self-ghost `AttackView` (re-resolve when `GhostManager.SelfGhost` changes); `view.Render(attack.State, currentAttack)`.
- Remove the `intent.hasClickTarget → SetTargetRpc` path (double-click-to-move); server target code may remain unused (deferred cleanup).

## 8. Visual

### 8.1 Rig (authored on `Ghost.prefab`)
- `AttackRig` empty child at the character center (tunable toward the shoulder); its local Z is the residual tilt.
- `WeaponBack`/`Body`/`WeaponFront` `SpriteRenderer`s at `sortingOrder` body−1/body/body+1, same `sortingLayer` as the body. Disabled by default.

### 8.2 `AttackView` (`Combat/AttackView.cs`, `MonoBehaviour` on `Ghost.prefab`)
- Serialized: `pivot`, the 3 layer SRs, the normal-body root (toggle) + `Animator`.
- `Render(AttackState s, AttackDefinition def)`:
  - `!s.IsAttacking` → disable rig, show body; early-out (covers Idle **and** feint cooldown).
  - else → enable rig, hide body; `row = def.directions[s.dirIndex].row; i = row*def.columnsPerRow + s.CurrentColumn;` set `Body/WeaponFront` sprites = `def.*[i]`, `WeaponBack` = `weaponBackFrames.Length>0 ? def.weaponBackFrames[i] : null` (hide if null); `pivot.localEulerAngles = (0,0,s.residualDeg)`.
- Reads state only; inert on remote ghosts (never called) in v1.
- **Weapon-only-tilt fallback:** apply `residualDeg` to a nested weapon-pivot under `Body` — one-line change, no data/logic impact.

## 9. Input changes (`Player/PlayerInput.cs`)

- **`Intent`:** drop `hasClickTarget`/`clickWorld` + double-click state; add `bool lmbDown, lmbHeld, lmbUp, rmbDown; Vector2 cursorWorld;`.
- **`Read(cam)`:** keep WASD/`Typing` movement. Add (false while `Typing`): `lmbDown = leftButton.wasPressedThisFrame`, `lmbHeld = leftButton.isPressed`, `lmbUp = leftButton.wasReleasedThisFrame`, `rmbDown = rightButton.wasPressedThisFrame`; always `cursorWorld = cam.ScreenToWorldPoint(mousePos)` (z = `|cam.z|`). `LocalPlayer` maps these into `AttackIntent`.

## 10. File manifest

**New (`Assets/_Scripts/Combat/`)**
- `AttackDirections.cs` — pure picker.
- `TimedFrame.cs` / `PhaseScales.cs` — small data structs (may co-locate in `AttackDefinition.cs`).
- `AttackDefinition.cs` — SO (sheets + columnsPerRow + 4 timed-frame phase lists + feintCooldown + directions + `TimeToHit`).
- `AttackState.cs` — phase/state data.
- `AttackIntent.cs` — input edges struct.
- `AttackSystem.cs` — the state machine (owned by `LocalPlayer`).
- `AttackView.cs` — phase-aware visual on `Ghost.prefab`.

**New (editor)**
- `Assets/Editor/AttackDefinitionTool.cs` — populate `Sprite[]`/`columnsPerRow` from a sliced sheet + Cardinal/Diagonal preset + phase/column validation.

**New (tests)**
- `Assets/Tests/EditMode/AttackDirections.Tests.*` — picker correctness.
- `Assets/Tests/EditMode/AttackTiming.Tests.*` — `TimeToHit` determinism + phase-cursor advancement (tap vs full, scale applied).

**Modified**
- `Assets/_Scripts/Net/LocalPlayer.cs` — own+tick `AttackSystem`; build `AttackIntent`; drive the self-ghost `AttackView`; serialized `currentAttack`; remove double-click-to-move.
- `Assets/_Scripts/Player/PlayerInput.cs` — LMB down/held/up + RMB + `cursorWorld`; remove double-click-to-move.
- `Ghost.prefab` — add `AttackRig` (pivot + 3 SRs) + `AttackView` (Inspector-wired).
- `Assets/_Scripts/Player/PlayerView.cs` — only if a `SetBodyVisible(bool)` helper is cleaner than `AttackView` toggling renderers directly (decide in planning).

## 11. Phasing

1. **Picker + data + timing tests.** `AttackDirections`, `TimedFrame`/`PhaseScales`, `AttackDefinition` (4 phase lists + `TimeToHit`), `AttackState` (phases), `AttackIntent`; EditMode tests for the picker and for `TimeToHit`/phase advancement (tap vs full, scale applied). *Deliverable:* green tests; types compile.
2. **One sliced Slash asset.** Slice the sword Slash body/`_f`/`_b` at 32px; build the editor populate tool; author the `Slash` `AttackDefinition` (diagonal preset, per-frame durations, the four phase lists incl. a `tapAnticipation`, feint cooldown). *Deliverable:* a validated asset; `[row,col]` indexing matches the sheet; `TimeToHit` returns the expected numbers.
3. **Rig + view (static).** Add `AttackRig` + `AttackView` to `Ghost.prefab`; render a forced direction/column. *Deliverable:* forcing a frame shows the 3-layer composite, correctly sorted/tinted, blank `_b` harmless.
4. **Flow state machine + input.** `AttackSystem` (hold→anticipation+re-aim, release→tap-or-full commit, RMB→feint+cooldown) owned by `LocalPlayer`; `PlayerInput` edges; remove double-click-to-move; `InHitWindow` log. *Deliverable:* hold winds up + re-aims; release strikes toward the cursor and locks; a tap plays the tap wind-up then strikes; RMB feints into a cooldown; completed attacks return to idle; WASD unaffected; console clean.

## 12. Verification plan

Per the unity-mcp flow (`execute_code` broken; **refresh `scope=all` → poll `editor_state.isCompiling` → `read_console` clean → Play → screenshot**; MCP can't drive the IMGUI Host button — use Multiplayer Play Mode or a single local client):

- **Phase 1:** `run_tests` (EditMode) — picker exact/≤45°/midpoint both presets; `TimeToHit` matches hand-computed sums; phase cursor advances per-frame and respects `PhaseScales`.
- **Phase 2:** open the asset; `bodyFrames.Length == columnsPerRow*rows`; a spot-checked `[row,col]` matches the cell; validation passes; `TimeToHit(tap)` and `TimeToHit(full)` print expected values.
- **Phase 3:** in Play, force each of the 4 directions/columns; screenshot shows weapon-behind/body/weapon-front sorted right, tinted, blank `_b` fine.
- **Phase 4 (the flow):**
  - **Hold** → wind-up plays then holds; moving the cursor **re-aims** (row + tilt follow); no hit log yet.
  - **Release after full wind-up** → direction **locks**, plays **hit** (logs `InHitWindow` once) → follow-through → idle.
  - **Tap** → plays the **tap wind-up** then the hit (timing matches `TimeToHit(tap)`).
  - **RMB during wind-up** → snaps to idle; LMB does nothing until `feintCooldown` elapses, then works again.
  - WASD still moves; double-click no longer path-moves; console clean.

## 13. Open assumptions & deferred

- **Whole-rig vs weapon-only tilt** decided visually in Phase 4 (one-line switch).
- **Pivot origin** starts at sprite center; nudge to the shoulder if off (tunable).
- **`feintCooldown` + `PhaseScales` location** — on `AttackDefinition`/per-attack and a default `PhaseScales` for now; a global `CombatSettings` (`JsonPref` + TunerPanels) is the alternative if they should be uniform/tunable live.
- **Row order is per-attack**, set by eyeballing each sheet; the preset only fills vectors.
- **`Ghost.prefab` provenance** — confirm it's hand-authored (not tool-regenerated) before adding the rig (a prior prefab-build tool was a footgun; appears retired).
- **Repo carries WIP** — commit the new `Combat/` files on their own; don't bundle shared files (scene, `Ghost.prefab`, settings).
- **Deferred:** damage/hitboxes/targets, stat values, attack replication, combos/buffering, move-slow-on-windup, dedicated feint art, weapon selection, pointer-over-UI. `AttackIntent` edges + `aimDir` + `attackId` + `PhaseScales` are the forward seams.
