# Attack Replication — Prediction, Reconciliation & Rollback — Design

- **Date:** 2026-05-24
- **Status:** Approved (brainstorming) — ready for implementation plan
- **Scope:** Replicate the existing local-only attack system across the network for **in-instance (underworld) players only**, riding the existing area-of-interest snapshot stream so attacks reach exactly the players who can already see the attacker. Server-authoritative attack state + lunge, owner-side prediction/reconciliation/rollback, a hybrid wire (snapshot pose + reliable transition events), and a server-side **hit seam** that does nothing yet but scales to hitboxes. **No damage/health/hitboxes in v1.**

## 1. Goal

Today the attack system ([Combat/](Assets/_Scripts/Combat/)) is **local-visual only**: `LocalPlayer` ticks `AttackSystem` ([Combat/AttackSystem.cs](Assets/_Scripts/Combat/AttackSystem.cs)) on the fixed tick and renders the result on the **self** ghost only ([LocalPlayer.cs:99](Assets/_Scripts/Net/LocalPlayer.cs)). The server knows nothing about attacks, and remote ghosts carry an `AttackView` that is never driven. So a second player never sees your swing.

Make attacks **server-authoritative and replicated**, reusing the netcode model already in place (input-replication + server sim + per-viewer AOI snapshots, no per-player NetworkObjects). Extend the determinism we already have — `AttackLogic` and `MovementStep` are both pure, fixed-tick, and shared between client prediction and server sim — so the attack joins movement as a first-class predicted+reconciled action.

### Decisions (Ryan, brainstorming 2026-05-24)
- **Hybrid wire** (Ryan, over per-tick-state-only and event-only): a **per-tick pose** in the snapshot (continuous state + mid-fight resync for newly-visible viewers) **plus reliable, tick-stamped transition events** (`Started`/`Struck`/`Feinted`) so the crisp discrete beats are never lost to 30 Hz sampling.
- **Server-derived lunge** (Ryan, over keeping the client-baked lunge): the client sends **raw** move + attack input; the **server derives the lunge** from authoritative attack state, so position is fully authoritative from day one. Cost accepted: the owner's rollback replay re-derives the lunge from the corrected attack state (a unified attack+move replay).
- **Server-authoritative attack state.** The server runs `AttackLogic` per in-instance player from the tick-stamped attack input (mirrors `PlayerSimSystem`), so the future hit resolution is server-side and cheat-resistant.
- **Full predict + reconcile + rollback for the owner's own attack**, mirroring `PredictionSystem` for movement.
- **Ride the existing AOI snapshot.** Attack data is sent only to a player's current AOI viewers (the snapshot loop already targets per-viewer). In-instance visibility is made **region-only** (drop the radius test inside a room) so same-room players are guaranteed to see each other regardless of room size/radius.
- **Overworld = no attack replication.** Combat stays gated to in-instance on the client, and the server only steps/sends attacks for `inInstance` players. Overworld snapshot entries carry no attack block.
- **Hit seam now, no damage.** The server emits an authoritative `OnStrike` hook on the →Hit transition; v1 logs only.

### Assumptions (reasonable defaults; flag if wrong)
- **Aim is quantized at the input boundary** (to a `ushort` angle) and the **same quantized value drives local prediction and the wire**, so server and client feed `AttackLogic` bit-identical intent → reconciliation stays exact.
- **The owner's equipped weapon id travels in the per-tick command** (1 byte), so the server is stateless about equip and there is no equip/attack ordering race. The wire trusts the owner's weapon id in v1 (ownership validation is future).
- **Reconcile snaps the attack state** on divergence (no easing of a phase pop); divergence is rare (deterministic, same inputs) and only becomes meaningful once the server rejects actions (cooldown/hit/anti-cheat) — i.e. v1's reconcile/rollback is correct-but-mostly-dormant scaffolding, which is the point of "scale for it".
- **Remote attack animation samples the snapshot pose at `snapshotHz` (30).** Attack frames (~0.08–0.12 s) are coarser than the 0.033 s snapshot interval, so per-snapshot pose is smooth enough; local frame-advance for remotes is a deferred smoothness seam.
- **The lunge curve moves into `AttackTimeline`** (pure, Combat asmdef) so the deterministic lunge can be computed without referencing the `AttackDefinition` SO — finishing the lunge-into-core consolidation begun on 2026-05-24.

### In scope (v1)
- A combined per-tick **`InputCommand`** (raw move + attack edges + quantized aim + weapon id), replacing the movement-only tick input.
- A shared, pure **`InstanceStep`** (attack→lunge→move) run identically by client predict, client replay, and server sim.
- **`AttackLogic.LungeVelocity`** + lunge curve in `AttackTimeline` (lunge fully in the deterministic core).
- Server **`AttackSimSystem`**: authoritative attack state per in-instance player, transition→event emission, and the `OnStrike` hit seam.
- Owner-side **`AttackPrediction`**: per-tick predicted-state ring + reconcile/rollback against the authoritative self-state, unified with movement replay.
- A **`SnapshotEntry` attack block** (pose; self entry carries reconcile extras) + a reliable **`AttackEvent`** RPC, both AOI-targeted via the existing snapshot loop.
- A shared **`WeaponCatalog`** SO (`byte id ↔ AttackDefinition`).
- Remote rendering: **`GhostAttack`** holder fed by `GhostManager` → the existing `AttackView`.
- **In-instance region-only AOI** visibility.

### Out of scope (deferred)
- Damage, health, hitboxes, hit reactions, target detection (the `OnStrike` seam is the forward hook).
- Overworld combat / its replication (combat is in-instance only).
- Remote local-frame-advance smoothing; snapshot deltas; an unreliable-pose / reliable-event channel split.
- Weapon ownership/anti-cheat validation; combos/input buffering.

## 2. Fit with the existing pipeline (why it's shaped this way)

- **Netcode model** (verified): no per-player NetworkObject. One scene `ReplicationHub` ([Net/ReplicationHub.cs](Assets/_Scripts/Net/ReplicationHub.cs)) carries every RPC. The owner sends tick-stamped input (`SubmitInputTickRpc` [ReplicationHub.cs:140](Assets/_Scripts/Net/ReplicationHub.cs)); the server drains contiguous ticks and steps `MovementStep` in `PlayerSimSystem.StepInstanceFixed` ([PlayerSimSystem.cs:29](Assets/_Scripts/Net/PlayerSimSystem.cs)); per-viewer AOI snapshots (`SendSnapshots` [ReplicationHub.cs:85](Assets/_Scripts/Net/ReplicationHub.cs)) ack `lastProcessedTick`; the owner reconciles in `PredictionSystem` ([Net/PredictionSystem.cs](Assets/_Scripts/Net/PredictionSystem.cs)).
- **The attack is already half-networked, by accident of good design.** The lunge already flows into movement prediction via `OverrideInput` ([PredictionSystem.cs:14](Assets/_Scripts/Net/PredictionSystem.cs), set in [LocalPlayer.cs:98](Assets/_Scripts/Net/LocalPlayer.cs)), and `AttackLogic` is pure + fixed-tick. The gaps are only: (a) the server doesn't step attacks, (b) attacks aren't on the wire, (c) remote `AttackView`s aren't driven.
- **Determinism is the existing reconciliation contract.** `MovementStep` ([Net/Movement/MovementStep.cs](Assets/_Scripts/Net/Movement/MovementStep.cs)) is shared by client predict + replay + server so replay matches the server. Attacks extend that contract: one shared `InstanceStep`.
- **AOI already answers "who can see whom"** ([Net/Aoi/AreaOfInterestSystem.cs](Assets/_Scripts/Net/Aoi/AreaOfInterestSystem.cs)): same `regionKey` + radius + hysteresis, targeted per viewer in `SendSnapshots`. Attacks piggyback on this exact set, so the visibility requirement is free.
- **Assembly boundaries (do not cross):** pure cores are isolated — `Minifantasy.Combat` ([Combat/Core/](Assets/_Scripts/Combat/Core/), `AttackLogic`/`AttackState`/`AttackTimeline`/`AttackDirections`) and `Minifantasy.Movement` ([Net/Movement/](Assets/_Scripts/Net/Movement/), `MovementStep`/`InputRingBuffer`), neither referencing the other or Netcode. The wire structs carry **primitives only**, so `Minifantasy.Movement` never references `Minifantasy.Combat`. `AttackIntent` is reconstructed from primitive bits on each side.
- **Convention** (`Assets/_Scripts/ARCHITECTURE.md`): Input → Logic → Data → Visual; Data = `*State`/`*Settings`, Logic = `*System`, Visual = `*View`. New types follow this.

## 3. Component overview

```
 OWNER CLIENT (fixed tick)            SERVER (host, fixed tick)            VIEWER CLIENTS
 ────────────────────────            ─────────────────────────            ──────────────
 PlayerInput → InputCommand          drain contiguous InputCommands
   {rawMove, atkBits, aimQ, wId}     per in-instance player:
        │  SubmitInputTickRpc ──────►   InstanceStep(atk, pos, cmd)  ◄── shared pure fn
        ▼                                 = AttackLogic.Step
 InstanceStep(atk,pos,cmd)                 + AttackLogic.LungeVelocity
   (predict locally, immediate)            + MovementStep
 buffer {cmd, predPos, predAtk}          detect attack transition
 self AttackView ← predicted atk           ├─ enqueue AttackEvent ──events──►  GhostManager → GhostAttack
        ▲                                  └─ OnStrike() hit-seam (v1: log)     → AttackView.Render(pose,def)
        │                            SnapshotEntry += attack pose ──@30Hz────►  position interp (lunge included)
        └── snapshot(ackTick, authoritative self pos+atk) ──┐                   (events + pose only to AOI viewers)
   reconcile: predicted ≠ authoritative at ackTick →        │
   snap pos+atk, replay buffered cmds via InstanceStep ◄────┘  (rollback)
```

| Unit | Side | Kind | Responsibility |
|------|------|------|----------------|
| `InstanceStep` | shared | Logic (pure) | One deterministic in-instance tick: `AttackLogic.Step` → `LungeVelocity` → `MovementStep`. Used by client predict, client replay, server. |
| `AttackLogic.LungeVelocity` | shared | Logic (pure) | The lunge vector from `(AttackState, AttackTimeline)`; replaces `LocalPlayer.AttackMoveOverride`. |
| `InputCommand` | wire | Data | `{tick, rawMove, attackBits, aimAngle, weaponId}` (primitives). |
| `AttackSimSystem` | server | Logic | Authoritative attack state per in-instance player; emits `AttackEvent`s + `OnStrike`. |
| `AttackPrediction` | owner | Logic | Predicted-state ring; reconcile/rollback vs authoritative self-state (unified with movement replay). |
| `SnapshotEntry` (attack block) | wire | Data | Per-attacker pose; self entry carries reconcile extras. |
| `AttackEvent` + `AttackEventRpc` | wire | Data/transport | Reliable, tick-stamped `Started`/`Struck`/`Feinted`, AOI-targeted. |
| `WeaponCatalog` | shared | Data (SO) | `byte id ↔ AttackDefinition`. |
| `GhostAttack` → `AttackView` | viewer | Visual | Hold replicated pose; drive the existing `AttackView`; one-shot beats from events. |

## 4. The determinism keystone — `InstanceStep` + lunge in the core

Because the lunge (attack) now feeds movement and must match across predict/replay/server, the attack→move ordering lives in **one pure function**:

```csharp
// Minifantasy.InstanceSim (refs Minifantasy.Combat + Minifantasy.Movement); pure, unit-tested.
public static class InstanceStep {
    public static void Step(ref AttackState atk, ref Vector2 pos, in InstanceInput cmd, in InstanceCtx ctx) {
        atk = AttackLogic.Step(atk, cmd.attack, ctx.timeline, ctx.scales, ctx.dt);
        Vector2? lunge = AttackLogic.LungeVelocity(atk, ctx.timeline);   // null outside hit/follow
        Vector2 move = lunge ?? cmd.rawMove;                            // lunge overrides WASD; rooted during windup
        pos = MovementStep.Step(pos, move, ctx.dt, ctx.speed, ctx.walkable);
    }
}
```

Two supporting moves keep it pure and testable:
1. **Lunge in the core.** `AttackTimeline` gains `lungeCurve` (copied in `AttackDefinition.BuildTimeline`); `AttackLogic.LungeVelocity(state, tl)` returns the lunge vector (`lockedAim * clamp01(curve.Evaluate(LungeProgress(state, tl)))`) or null outside Hit/FollowThrough, and `Vector2.zero` during windup phases (rooted). `LocalPlayer.AttackMoveOverride` is deleted.
2. **A small new pure asmdef `Minifantasy.InstanceSim`** referencing `Minifantasy.Combat` + `Minifantasy.Movement` houses `InstanceStep` + `InstanceInput`/`InstanceCtx` + tests. (`InstanceCtx` carries `timeline`, `scales`, `dt`, `speed`, and a `Func<Vector2,bool> walkable` — no Netcode/Game/SO refs.) *Alternative if avoiding a new asmdef: place `InstanceStep` in Assembly-CSharp and lose unit-testability of the keystone — not recommended.*

This is the same discipline that already makes movement reconciliation safe, now covering the attack.

## 5. Data

### 5.1 `InputCommand` (`Net/InputCommand.cs`, struct : `INetworkSerializable`)
`{ uint tick; Vector2 rawMove; byte attackBits; ushort aimAngle; byte weaponId; }`
- `attackBits`: bit0 pressed, bit1 held, bit2 released, bit3 feint.
- `aimAngle`: aim direction quantized to a `ushort` (≈0.0055° steps). The client quantizes **before** predicting and sending; both sides reconstruct the identical `Vector2` for `AttackIntent.aimDir` (determinism).
- Replaces `SubmitInputTickRpc(uint tick, Vector2 input)`. `rawMove` is the raw WASD (not the lunge-baked value sent today) — the server derives the lunge.
- The server reconstructs `AttackIntent` (a `Minifantasy.Combat` type) from the primitive bits + aim; the wire struct never references Combat.

### 5.2 `SnapshotEntry` attack block (`Net/SnapshotEntry.cs`, extended)
Today: `ulong id; float x,y; byte flags` (`SnapBit=1`, `InInstanceBit=2`). Add:
- A new flag `AttackingBit`. When set, the entry appends: `byte weaponId; byte packed` (phase 3b + frameIndex 3b + dirIndex 2b); `byte residualQ` (quantized weapon tilt for `rotateToAim`).
- **Self entry only** (the entry whose `id == viewer`): additionally append `byte selfExtra` (windupComplete bit + quantized cooldown) so the owner can reconcile precisely.
- `NetworkSerialize` reads mirror writes conditionally on `AttackingBit`/self. ≈4 bytes per attacking player, 0 when idle — and only ever for in-instance players.

### 5.3 `AttackEvent` (`Net/AttackEvent.cs`, struct : `INetworkSerializable`)
`{ ulong attackerId; byte kind; byte weaponId; uint tick; ushort aimAngle; }`, `kind ∈ {Started, Struck, Feinted}`. Sent in a batch `AttackEventRpc(AttackEvent[] events, ClientRpcParams)` targeted to one viewer, flushed alongside that viewer's snapshot. **Reliable** so no transition is dropped; tick-stamped so the client can place it.

### 5.4 `WeaponCatalog` (`Combat/WeaponCatalog.cs`, ScriptableObject)
`AttackDefinition[] weapons;` indexed by `byte id`; `Get(byte id)`; `IndexOf(AttackDefinition)`. One asset referenced by the hub (server: timing/lunge) and clients (remotes: frames). `LocalPlayer.weapons[]` becomes a view onto the catalog so equipped index == wire id.

### 5.5 `ServerPlayer` additions (`Net/PlayerRegistry.cs`)
Add `AttackState attackState; PhaseScales attackScales; byte weaponId; InputRingBuffer-of-commands` (or reuse the existing per-tick buffer widened to `InputCommand`). Authoritative; never replicated directly — only via the snapshot/events.

### 5.6 `AttackTimeline.lungeCurve` (`Combat/Core/AttackTimeline.cs`)
Add `AnimationCurve lungeCurve;` populated by `AttackDefinition.BuildTimeline`. Keeps the deterministic lunge computable from Combat types alone (no SO ref from `InstanceStep`).

## 6. Logic

### 6.1 `AttackSimSystem` (`Net/AttackSimSystem.cs`, server) — mirrors `PlayerSimSystem`
The in-instance server step is unified onto `InstanceStep`: for each `inInstance` player, drain contiguous `InputCommand`s; per command run `InstanceStep.Step(ref sp.attackState, ref sp.worldPos, cmd, ctx)` (ctx from `WeaponCatalog.Get(cmd.weaponId).Timeline`); advance `lastProcessedTick`. Around each step, compare pre/post `attackState.phase` to:
- enqueue `AttackEvent`s (`Started` on Idle→Anticipation, `Struck` on →Hit, `Feinted` on Anticipation→Idle-with-cooldown), and
- call `OnStrike(attackerId, weaponId, worldPos, lockedAim, tick)` on the →Hit edge (**the hit seam**; v1 logs).

This **replaces** the `MovementStep`-only body of `PlayerSimSystem.StepInstanceFixed` ([PlayerSimSystem.cs:29](Assets/_Scripts/Net/PlayerSimSystem.cs)) for in-instance players (overworld grid movement is untouched). Non-instance players are skipped (no attack).

### 6.2 `ReplicationHub` wiring (`Net/ReplicationHub.cs`, modified)
- `SubmitInputTickRpc` carries an `InputCommand` (was `tick, Vector2`).
- `SendSnapshots` ([ReplicationHub.cs:85](Assets/_Scripts/Net/ReplicationHub.cs)): when building viewer V's entries, pack each visible attacker's pose into its `SnapshotEntry` (self entry gets the reconcile extras), and append any pending `AttackEvent` whose `attackerId ∈ visibleNow` to V's event batch, sent via `AttackEventRpc` to V. Pending events are produced by `AttackSimSystem` since the last flush and cleared after the snapshot pass. **This reuses the existing AOI computation**, so events/pose reach exactly the viewers who can see the attacker.
- Client `SnapshotClientRpc` → routes attack pose to `GhostManager`/`AttackPrediction` (self) as it already routes position; `AttackEventRpc` → `GhostManager` for remotes / `AttackPrediction` for self.

### 6.3 `AttackPrediction` (`Net/AttackPrediction.cs`, owner) — sibling to `PredictionSystem`
- Shares the per-tick counter with `PredictionSystem`. The owner's `FixedUpdate` runs `InstanceStep` once (replacing today's separate `attack.Tick` + `prediction.FixedTick`), so attack + position are predicted together; it buffers `{tick, cmd, predictedPos, predictedAttackState}` and sends the `InputCommand`.
- On snapshot: movement reconcile (position) is as today; additionally, if the buffered `predictedAttackState` at `ackTick` differs from the authoritative self-state, **snap** both pos+attack to authoritative at `ackTick` and **replay** buffered commands `ackTick+1..tick` through `InstanceStep` (the rollback — re-derives the lunge each replayed tick from the corrected attack state). Movement-only divergence keeps the existing cheap path.
- *Separation note:* movement and attack prediction stay distinct units sharing the tick + the replay loop; the loop and combined buffer live in Net (which references both pure asmdefs + `InstanceSim`), so neither pure asmdef is coupled.

### 6.4 AOI in-instance region-only (`Net/Aoi/AreaOfInterestSystem.cs`, modified)
In `VisibleFor`, when `viewer.regionKey != (0,0)` (in a room), include all same-region players unconditionally (skip the radius/hysteresis test). Overworld keeps radius + hysteresis. Guarantees same-room mutual visibility independent of room size/radius. Pure → unit-tested.

## 7. Visual

### 7.1 Remote ghosts (`Net/GhostManager.cs` + `GhostAttack`)
- `GhostManager.Apply` ([GhostManager.cs:31](Assets/_Scripts/Net/GhostManager.cs)) writes each non-self ghost's attack pose (phase/frame/dir/residual/weaponId) into a small `GhostAttack` data holder on the ghost; `AttackEvent`s route to it for one-shot SFX/VFX (seam) and to snap the pose on the exact strike tick.
- Each frame the ghost drives the **existing** `AttackView.Render(state, catalog.Get(weaponId))` ([Combat/AttackView.cs](Assets/_Scripts/Combat/AttackView.cs)) from the replicated pose. Position (lunge included) keeps coming from the existing interpolation. Idle → `AttackView` hides the rig as it already does.

### 7.2 Self ghost
Unchanged ownership: `LocalPlayer` drives the self ghost's `AttackView` from the **predicted** `AttackState` ([LocalPlayer.cs:99](Assets/_Scripts/Net/LocalPlayer.cs)) — now sourced from `AttackPrediction`'s current state.

## 8. Determinism, multiplayer & performance
- **Bandwidth:** attack data is in-instance only (small rooms, few players), ≈4 pose bytes per attacking player per snapshot + a few events per attack. Negligible on top of the existing position stream.
- **CPU:** one extra `AttackLogic.Step` per in-instance player per drained tick (cheap, branchy). Overworld unaffected.
- **Determinism:** client predict, client replay, and server all call `InstanceStep`; aim is quantized identically on both sides → predicted self-state matches authoritative absent server-side rejection.
- **Topology:** unchanged (Relay host-is-a-player). Events/pose targeted per viewer via existing AOI.

## 9. Hitbox seam (explicit; no logic in v1)
`AttackSimSystem.OnStrike(attackerId, weaponId, pos, lockedAim, tick)` fires on the authoritative →Hit transition. v1 = log. Later it sweeps same-`regionKey` `ServerPlayer`s against a weapon-derived volume (the server already holds every position) and routes results to a damage spec — no wire or client change needed to add it.

## 10. File manifest

**New**
- `Assets/_Scripts/InstanceSim/InstanceStep.cs` (+ `InstanceInput`/`InstanceCtx`) — pure attack→lunge→move step.
- `Assets/_Scripts/InstanceSim/Minifantasy.InstanceSim.asmdef` — refs `Minifantasy.Combat` + `Minifantasy.Movement`.
- `Assets/Tests/EditMode/InstanceSim/Minifantasy.InstanceSim.Tests.asmdef` + `InstanceStepTests.cs` — determinism + replay/rollback convergence.
- `Assets/_Scripts/Net/InputCommand.cs` — combined per-tick wire struct.
- `Assets/_Scripts/Net/AttackEvent.cs` — event struct + kinds.
- `Assets/_Scripts/Net/AttackSimSystem.cs` — server authoritative attack sim + events + hit seam.
- `Assets/_Scripts/Net/AttackPrediction.cs` — owner predicted-state ring + reconcile/rollback.
- `Assets/_Scripts/Combat/WeaponCatalog.cs` — `byte id ↔ AttackDefinition` SO.
- `Assets/_Combat/WeaponCatalog.asset` — the catalog instance (ordered weapon list).
- `Assets/_Scripts/Net/GhostAttack.cs` — per-ghost replicated attack pose holder (or a field set on the Ghost rig).
- Tests: AOI in-instance region-only; `AttackLogic.LungeVelocity`; aim quantize round-trip.

**Modified**
- `Assets/_Scripts/Combat/Core/AttackTimeline.cs` — add `lungeCurve`.
- `Assets/_Scripts/Combat/Core/AttackLogic.cs` — add `LungeVelocity(state, tl)`.
- `Assets/_Scripts/Combat/AttackDefinition.cs` — `BuildTimeline` copies `lungeCurve`.
- `Assets/_Scripts/Net/ReplicationHub.cs` — `InputCommand` RPC; pack attack pose into snapshots; flush `AttackEventRpc` per viewer; `FixedUpdate` calls `AttackSimSystem.StepInstanceFixed` (was `PlayerSimSystem.StepInstanceFixed`, [ReplicationHub.cs:82](Assets/_Scripts/Net/ReplicationHub.cs)).
- `Assets/_Scripts/Net/SnapshotEntry.cs` — attack block + `AttackingBit` + self extras.
- `Assets/_Scripts/Net/PlayerRegistry.cs` — `ServerPlayer` attack fields + widened input buffer.
- `Assets/_Scripts/Net/PlayerSimSystem.cs` — the in-instance branch relocates to `AttackSimSystem` (which runs `InstanceStep`); overworld grid movement here is unchanged.
- `Assets/_Scripts/Net/PredictionSystem.cs` — owner tick runs `InstanceStep`; cooperate with `AttackPrediction` on the unified replay; buffer raw commands.
- `Assets/_Scripts/Net/LocalPlayer.cs` — build `InputCommand` (raw move + attack + aimQ + weaponId); own `AttackPrediction`; drive self `AttackView` from predicted state; delete `AttackMoveOverride`/`RotateDeg`.
- `Assets/_Scripts/Net/GhostManager.cs` — apply attack pose + events to remote ghosts; drive their `AttackView`.
- `Assets/_Scripts/Net/Aoi/AreaOfInterestSystem.cs` — in-instance region-only visibility.
- `Assets/_Scripts/Combat/AttackView.cs` — confirm it renders from an externally-supplied pose for remotes (likely no change).
- `Ghost.prefab` — add `GhostAttack` driver wiring if needed (AttackView already present).

## 11. Phasing
1. **Lunge into the core + `InstanceStep`.** `AttackTimeline.lungeCurve`, `AttackLogic.LungeVelocity`, the `Minifantasy.InstanceSim` asmdef + `InstanceStep`; repoint `LocalPlayer`/`PredictionSystem` to step via `InstanceStep` locally (still single-player). *Deliverable:* unchanged local feel; EditMode determinism + lunge-parity tests green.
2. **Server authority + wire.** `InputCommand`, `WeaponCatalog`, `ServerPlayer` attack fields, `AttackSimSystem` (state + `OnStrike` log), snapshot attack block, `AttackEvent`/RPC, AOI region-only. *Deliverable:* server computes authoritative attack state + lunge; pose/events leave the server to AOI viewers.
3. **Remote render.** `GhostAttack` + `GhostManager` drive remote `AttackView`s from pose; events for crisp beats. *Deliverable:* a second player sees your swing in the same room; overworld carries none.
4. **Owner predict/reconcile/rollback.** `AttackPrediction` + unified replay; reconcile vs authoritative self-state. *Deliverable:* own attack stays predicted+immediate; injected divergence converges (test); position stays authoritative.

## 12. Verification plan
Per the unity-mcp flow (`execute_code` broken; **refresh `scope=all` → poll `editor_state` ready → `read_console` clean → Play → screenshot**; MCP can't drive the IMGUI Host button):
- **Unit (EditMode):** `InstanceStep` determinism (same command stream → identical pos+state); lunge overrides WASD in Hit/Follow and roots during windup; `LungeVelocity` == old `AttackMoveOverride` outputs; aim quantize→reconstruct round-trip; AOI in-instance region-only (same room always visible, cross-room never, overworld radius intact); a **rollback test** (inject divergence at tick k → replay converges to authoritative).
- **MCP-verifiable:** compile clean; all EditMode green; Play boots with no console errors.
- **Manual host (documented):** two clients in one underworld room see each other's full attack (windup/strike/follow + correct weapon + diagonal aim); a feint reads correctly remotely; two players in different rooms see nothing of each other's attacks; overworld snapshots carry no attack block.

## 13. Open assumptions & deferred
- **Repo carries WIP** — commit new files on their own; don't bundle shared files (scene, `Ghost.prefab`, settings, other `_Combat` assets).
- **Buffer placement** — the combined owner-side command+predicted-state ring lives in Net (references both pure asmdefs); the existing `Minifantasy.Movement` `InputRingBuffer` stays pure (movement-only) or is generalized in planning; do **not** make `Minifantasy.Movement` depend on `Minifantasy.Combat`.
- **`Minifantasy.InstanceSim` asmdef** vs hosting `InstanceStep` in Assembly-CSharp — recommend the asmdef (keystone testability); confirm at spec review.
- **Snapshot fidelity for self reconcile** — if `selfExtra` proves insufficient (phaseElapsed-sensitive cases), widen the self entry; reconcile snapping `phaseElapsed` to 0 is acceptable (rare, sub-frame).
- **Equip trust** — weapon id from the owner's command is trusted in v1; server-side ownership validation is future.
- **Deferred:** damage/health/hitboxes/hit-reactions (the `OnStrike` seam), remote frame-advance smoothing, snapshot deltas / unreliable-pose channel, combos/buffering, overworld combat.
```
