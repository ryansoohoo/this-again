# Interest-Managed Player Replication — Design

- **Date:** 2026-05-23
- **Status:** Approved (brainstorming) — ready for implementation plan
- **Scope:** Replace NGO auto-replication of players with hand-rolled, area-of-interest snapshots so the game scales to 100+ players. Players only.

## 1. Goal

Today every client observes **every** player: NGO auto-spawns each player's `NetworkObject` on all
clients and streams its `NetworkTransform` to all of them, regardless of whether they could ever see each
other. At 100+ players that is the throughput wall.

Replace this with **server-authoritative, interest-managed replication**: the server holds all players and
sends each client a snapshot of *only* the players that client can currently see (same region, within
radius). Remote players are drawn as plain local "ghost" GameObjects — **no per-player `NetworkTransform`,
no `NetworkVariable`**. A player you can't see is never loaded on your machine.

### Decisions (Ryan, brainstorming 2026-05-23)
- **Hand-rolled replication**, not NGO's visibility system. Custom snapshot stream + custom ghosts. (Ryan
  chose this over the lighter "keep NetworkTransform + gate with NGO visibility" option.)
- **Keep a thin `PlayerLink` `NetworkObject`** per connection for identity + RPC routing (owner→server
  `SenderClientId`, Relay plumbing). We do **not** go fully NetworkObject-less.
- **Ghosts are runtime-`Instantiate`d** from an Inspector-referenced prefab. The systems/managers are
  pre-placed scene objects (honors the "scene objects over runtime instantiation" preference for *systems*;
  per-player visuals are inherently dynamic — NGO already instantiates a player per connection today).
- **No client-side prediction in v1.** The local player renders from the snapshot like everyone else —
  identical server-round-trip feel to today (the current `NetworkTransform` is already server-authoritative
  with no prediction, so this is parity, not a regression).
- **Underworld isolation becomes an explicit server-side region filter**, replacing today's
  "isolation by distance only."

### In scope (v1)
- Server holds authoritative player state; owner sends intent via RPC (replaces `moveInput` NetworkVariable).
- A per-client, per-tick **area-of-interest** query (region match + radius, with show/hide hysteresis).
- A **targeted snapshot** RPC: server → each client, carrying only that client's visible players (+ its own
  authoritative position + its own instance flag).
- **Ghost** GameObjects on the client: spawned/despawned from the snapshot, interpolated between snapshots,
  reusing the existing visual/animation/shadow rig and `PlayerView`.
- Region-keyed underworld isolation (overworld ↔ rooms, and room ↔ room, can never co-occur in a snapshot).
- Tunable settings (radius, tick rate) via `JsonPref` + a tuner panel.

### Out of scope (deferred — see §11)
- Client-side prediction / reconciliation for the local player.
- Dedicated server and any host-CPU/topology work (we stay on the Relay host-is-a-player model).
- Replication of anything other than players (no enemies/projectiles exist; terrain + time-of-day stay
  deterministic and unreplicated).
- Snapshot deltas / unreliable+event channel / dense-crowd priority caps (v1 sends a reliable full AOI
  snapshot each tick).

## 2. Fit with the existing pipeline (why the design is shaped this way)

- **Current model** (verified): each player is one `NetworkObject` + `NetworkTransform` +
  `PlayerMovement` (`NetworkBehaviour`). Movement is server-authoritative — the owner writes the `moveInput`
  NetworkVariable ([PlayerMovement.cs:22](Assets/_Scripts/Player/PlayerMovement.cs)) and sends
  `SetTargetServerRpc`; the server runs `Pathfinder` + `PlayerMotion` in `ServerStep`
  ([PlayerMovement.cs:172](Assets/_Scripts/Player/PlayerMovement.cs)) and writes the transform, which
  `NetworkTransform` replicates to everyone. `PlayerView` derives walk/idle + 8-way facing from the
  replicated transform. Only two NetworkVariables exist: `moveInput` (owner→everyone) and `inInstance`
  (server→everyone, [PlayerMovement.cs:29](Assets/_Scripts/Player/PlayerMovement.cs)).
- **Terrain + time-of-day are already deterministic** (pure functions of `(cell, seed)` / `ServerTime`, no
  replication). This change extends that philosophy to *visibility*: the client computes/draws only what the
  server tells it is relevant.
- **Architecture convention** (`Assets/_Scripts/ARCHITECTURE.md`): Input → Logic → Data → Visual. Data =
  plain `*State`/`*Settings`; Logic = `*System`; Visual = `*View`. New types follow this.
- **Folder layout:** networking lives in `Assets/_Scripts/Net/` (today: `RelayConnector`, `NetState`). The
  new replication types go there; ghost visuals reuse `Assets/_Scripts/Player/`.
- **Assembly boundary (do not cross):** the `Minifantasy.Commands` asmdef (`Assets/_Scripts/Commands/`) is
  pure + unit-tested — **add no new types there**. Everything new lives in `Assembly-CSharp`.
- **Relay topology unchanged:** `RelayConnector` ([Net/RelayConnector.cs](Assets/_Scripts/Net/RelayConnector.cs))
  still hosts/joins; the host is server + a player. AOI is exactly what relieves a single-host uplink — each
  client receives only nearby players, so server upload scales with local density, not total player count.

## 3. Component overview

```
 OWNER (client)                    SERVER (host)                          OTHER CLIENT
 ───────────                       ───────────                            ────────────
 PlayerLink (owner):               PlayerLink (server, per connection):
   reads PlayerInput                 PlayerMotion sim (ServerStep)
   SubmitInputServerRpc ─────────►   regionKey, inInstance, worldPos
   SetTarget/Halt/Enter/Leave ───►   Pathfinder
                                   ReplicationHub.Tick() @ 15 Hz:
                                     AreaOfInterestSystem(viewer):
                                       regionKey == && dist ≤ radius
                                       (show/hide hysteresis)
                                            │ per client
   GhostManager.Apply(snapshot) ◄──SnapshotClientRpc(self + nearby)──► GhostManager.Apply(snapshot)
     spawn/despawn ghosts (prefab)                                       …interpolate, run PlayerView
     interpolate, drive PlayerView
     expose self-ghost as LocalPlayer
```

Five units, each independently understandable + testable:

| Unit | Side | Kind | Responsibility |
|------|------|------|----------------|
| `PlayerLink` | both | `NetworkBehaviour` (per connection) | Owner: read input, send intent RPCs, expose the local-player facade. Server: own the authoritative `PlayerMotion`/`regionKey`/`inInstance`, run the movement sim. |
| `AreaOfInterestSystem` | server | Logic (pure) | Given all players' `(clientId, pos, regionKey)` + settings + prior visibility, return each client's visible set with hysteresis. |
| `ReplicationHub` | both | `NetworkBehaviour` (scene singleton) | Server: on a fixed tick, build + send each client its snapshot via a targeted `ClientRpc`. Client: receive + forward to `GhostManager`. |
| `GhostManager` | client | Logic (scene singleton) | Spawn/despawn ghost GameObjects from snapshots, interpolate them, drive `PlayerView`, expose the self-ghost as the local player. |
| Ghost prefab | client | Asset | Visual-only player rig (sprites + Animator + Shadow child + `PlayerView`); **no** NetworkObject/Transform. |

## 4. Data

### 4.1 `SnapshotEntry` (`Net/SnapshotEntry.cs`, struct : `INetworkSerializable`)
One replicated player: `ulong id; float x; float y; byte facing8; byte flags;`
- `flags` bits: `bit0 = snap` (teleported — do not interpolate across this jump), `bit1 = inInstance`
  (meaningful for the self entry; drives command gating), `bit2 = moving` (animation hint).
- A snapshot RPC carries `SnapshotEntry[]` for one client (its self entry + its visible others).

### 4.2 `ReplicationSettings` (`Net/ReplicationSettings.cs`, `[Serializable]`, tunable via `JsonPref`)
- `float showRadius = 96` — start observing a player at this distance (cells).
- `float hideRadius = 128` — stop observing past this distance (hysteresis: `hide > show` prevents
  edge flicker). **Invariant:** `hideRadius < 129` (the underworld inter-room gap, §7) so radius is
  defense-in-depth on top of the region filter; and `showRadius ≥ ~80` (the view radius) so players don't
  pop in on-screen.
- `int snapshotHz = 15` — snapshot send rate. Movement is ~4 tiles/s, so 15 Hz + interpolation is smooth;
  ~3 KB/s per client at ~15 visible players.

### 4.3 Server player state (lives on `PlayerLink`, server-only — no separate registry type)
The set of spawned `PlayerLink`s (one per connection, from `NetworkManager.ConnectedClients`) **is** the
registry. Each carries server-only: `PlayerMotion motion` (reused as-is), `Vector2Int regionKey`
(`(0,0)` = overworld; else the underworld room origin), `bool inInstance`, and the authoritative
`transform.position`. No data is replicated by these fields directly — only the snapshot stream leaves the
server.

## 5. Logic

### 5.1 `PlayerLink` (`Net/PlayerLink.cs`, `NetworkBehaviour`, replaces `PlayerMovement`)
The current Player prefab's `NetworkObject` stays and is still NGO's auto-spawned PlayerObject per
connection (so ownership + `SenderClientId` + Relay routing are free). `PlayerMovement` is **split**:

- **Owner side** (the old owner half): `Update` reads `PlayerInput`; on change, `SubmitInputServerRpc(byte
  dir8)` (unreliable, sent only on change — replaces the `moveInput` NetworkVariable). Click-to-move,
  `Halt`, `RequestEnter/LeaveInstance` keep their existing ServerRpcs. Exposes the **local-player facade**
  (`PlayerLink.LocalInstance`): `CurrentCell()` (reads the self-ghost transform), `InInstance` (from the
  last self snapshot — replaces the `inInstance` NetworkVariable read), `Halt()`,
  `RequestEnterInstance(cell)`, etc. — so camera / `EncounterManager` / commands retarget here from
  `PlayerMovement.LocalInstance`.
- **Server side** (the old server half, essentially unchanged): owns `PlayerMotion`; `ServerStep` consumes
  the submitted input, runs `Pathfinder`, and writes `transform.position` (now read by the AOI/Hub, not a
  NetworkTransform). `EnterInstanceServerRpc` sets `regionKey = packed(Underworld.RegionOriginForSite(...))`
  + teleports via `SpawnCell`; `LeaveInstanceServerRpc` sets `regionKey = (0,0)` + returns. On teleport it
  raises a per-player **snap** flag consumed by the next snapshot (so ghosts don't streak across the
  16k-cell jump). `NetworkTransform.Teleport` calls are removed with the component.
- **`CheckObjectVisibility = clientId => clientId == OwnerClientId`** (server always observes). This hides
  the *empty* link object from other clients (saves the spawn message + 100 stray GameObjects per client).
  It is static (owner-only, never recomputed) — **not** the AOI mechanism; AOI is the custom snapshot.

### 5.2 `AreaOfInterestSystem` (`Net/AreaOfInterestSystem.cs`, pure static — unit-testable)
- `VisibleFor(viewer, allPlayers, settings, priorVisible)` → the set of player ids the viewer should
  observe: `p.regionKey == viewer.regionKey && dist(p, viewer) ≤ (priorVisible.Contains(p) ? hideRadius :
  showRadius)`. Self is always included.
- A region-bucketed spatial hash keeps the per-tick cost ~O(n) instead of O(n²): bucket players by
  `regionKey` first (overworld vs each room), then by a coarse cell grid within a region, and only test
  players in neighbouring buckets.
- Pure inputs/outputs → directly unit-testable (region isolation, radius, hysteresis) the way the world-gen
  code already is.

### 5.3 `ReplicationHub` (`Net/ReplicationHub.cs`, `NetworkBehaviour`, scene singleton on a NetworkObject)
- Scene-placed, observed by **all** clients (so its `ClientRpc` reaches everyone, including ghosts'
  owners whose `PlayerLink` is hidden from others).
- **Server**, every `1/snapshotHz`: gather all `PlayerLink`s' `(clientId, pos, regionKey, facing, moving,
  snap, inInstance)`; for each connected client, `AreaOfInterestSystem.VisibleFor(...)` → build that
  client's `SnapshotEntry[]` (self first, with its `inInstance` bit; then visible others) →
  `SnapshotClientRpc(entries, new ClientRpcParams{ Send = { TargetClientIds = [clientId] }})`. One send per
  client per tick (payloads differ per client). **Reliable** in v1 (correct enter/leave; tiny payloads).
- **Client:** `SnapshotClientRpc` runs locally → `GhostManager.Apply(entries)`. Per-player snap bits are
  cleared after the tick.

### 5.4 `GhostManager` (`Net/GhostManager.cs`, scene singleton, client logic)
- Holds a serialized **Ghost prefab** reference (Inspector-wired — no `Resources.Load`).
- `Apply(entries)`: for each entry, `Instantiate` the ghost on first sight (positioned, no lerp) or update
  its interpolation target; entries absent from the latest snapshot are despawned (short grace to absorb a
  dropped tick). `snap`-flagged entries set position directly (no lerp).
- `Update`: interpolate each ghost between its last two snapshot positions (buffer of 2, `snapshotHz`-paced)
  → smooth motion at the render rate; `PlayerView` on the ghost derives walk/idle + facing from that motion,
  exactly as it does today from the replicated transform.
- Marks the entry whose `id == NetworkManager.LocalClientId` as the **self-ghost**: the camera follows it,
  `EncounterManager`/commands read it via `PlayerLink.LocalInstance`. (Brief startup gap until the first
  snapshot — camera defaults to the spawn area.)

## 6. Visual

- **Ghost prefab** = the current Player prefab's **visual subtree** (SpriteRenderers, Animator, the "Shadow"
  child + sorting layer, `PlayerView`, body-tint from the day/night work) with **NetworkObject,
  NetworkTransform, and `PlayerMovement` removed**. Reusing it means **no art/animation rebuild**.
- **Player (link) prefab** = the current Player prefab with the **visual subtree, Animator, `PlayerView`,
  and NetworkTransform removed**, and `PlayerMovement` replaced by `PlayerLink`. It is invisible — pure
  identity/RPC. This stays assigned as `NetworkManager.NetworkConfig.PlayerPrefab`.
- `PlayerView` is unchanged in behavior; it just runs on a ghost instead of the networked player.

## 7. Underworld (the explicit guarantee)

`Underworld` ([World/Underworld.cs](Assets/_Scripts/World/Underworld.cs)) is unchanged. The region key makes
isolation explicit:

- **overworld** players: `regionKey = (0,0)`.
- **underworld** players: `regionKey =` the room's interior origin from
  `Underworld.RegionOriginForSite(siteX, siteY)` ([Underworld.cs:34](Assets/_Scripts/World/Underworld.cs)) —
  always ≥ `(16384,16384)`, so never `(0,0)`.

`AreaOfInterestSystem` filters by `regionKey` **first**, so:
- overworld ↔ underworld players can **never** appear in each other's snapshot (different keys),
- two **different rooms** have different keys → never co-occur,
- the radius then culls density **within** a region.

This replaces today's emergent "isolation by distance only" with a hard server-side filter. As defense in
depth, `hideRadius < 129` (adjacent rooms' nearest interior cells are ≥129 apart: `Stride 152 − RoomSize
24 − 1`), so even a same-key edge case can't bridge rooms. Teleport sets `regionKey` + raises the snap flag
so the ghost jumps cleanly.

## 8. Determinism, multiplayer & performance

- **Server upload scales with local density**, not total players: each client receives only its AOI set.
  Spread-out players ⇒ tiny snapshots; this is the 100+ win.
- **Server CPU per tick** is ~O(n) via the region-bucketed spatial hash; per-player movement sim is the same
  cost as today (cheap stepping + occasional A* on click). No new O(n²).
- **No new replicated state**: zero NetworkVariables on players; the only player NetworkObject (`PlayerLink`)
  is hidden from non-owners and carries no transform/vars. The single always-replicated object is the
  scene `ReplicationHub`, which broadcasts nothing — it only sends targeted snapshots.
- **Determinism preserved**: terrain + time-of-day remain pure/unreplicated; this change only governs which
  *players* a client loads.

## 9. File manifest

**New**
- `Assets/_Scripts/Net/PlayerLink.cs` — owner facade + server sim (replaces `PlayerMovement`).
- `Assets/_Scripts/Net/ReplicationHub.cs` — per-client snapshot send/receive (`NetworkBehaviour` singleton).
- `Assets/_Scripts/Net/AreaOfInterestSystem.cs` — pure visibility query (region + radius + hysteresis).
- `Assets/_Scripts/Net/GhostManager.cs` — ghost lifecycle + interpolation + local-player facade.
- `Assets/_Scripts/Net/SnapshotEntry.cs` — `INetworkSerializable` per-player snapshot struct.
- `Assets/_Scripts/Net/ReplicationSettings.cs` — radius/tick settings (`JsonPref`).
- `Assets/_Prefabs/Ghost.prefab` — visual-only player rig (derived from `Player.prefab`).
- A `ReplicationHub` + `GhostManager` GameObject pre-placed in `SampleScene` (with the Ghost prefab wired).

**Modified**
- `Assets/_Prefabs/Player.prefab` — strip NetworkTransform + visual subtree + Animator + `PlayerView`;
  swap `PlayerMovement` → `PlayerLink`. *(Currently uncommitted-modified — see §11.)*
- `Assets/Scenes/SampleScene.unity` — add the Hub/GhostManager objects; wire references.
- `Assets/_Scripts/EncounterManager.cs` — retarget `PlayerMovement.LocalInstance` →
  `PlayerLink.LocalInstance`.
- The camera-follow component (`Assets/_Scripts/Camera/…`) — follow the self-ghost via
  `PlayerLink.LocalInstance` instead of the networked player transform.
- `Assets/_Scripts/CommandBootstrap.cs` / `Commands/CommandScope.cs` consumers — read `InInstance` from the
  `PlayerLink` facade (no NetworkVariable). *(No new types in the Commands asmdef.)*
- `Assets/_Scripts/Game.cs` — build/own `ReplicationSettings` (`replication.json` via `JsonPref`); expose
  the facade; ensure Hub/GhostManager are initialized after the world.
- `Assets/_Scripts/UI/TunerPanels.cs` — a "Replication" accordion (show/hide radius, snapshot Hz) + save.
- `Assets/_Scripts/Player/PlayerView.cs` — no logic change; confirmed to run on a ghost (reads its own
  transform).

**Deleted**
- `Assets/_Scripts/Player/PlayerMovement.cs` (+ `.meta`) — superseded by `PlayerLink` (server sim moves
  with it; owner API becomes the facade).

## 10. Phasing

1. **Server authority without NetworkTransform.** Add `PlayerLink` (server sim + owner input RPC replacing
   `moveInput`; `inInstance` → server field) and a temporary debug snapshot (send *all* players to *all*
   clients) + `GhostManager` + Ghost prefab. *Deliverable:* movement + instances work end-to-end with
   ghosts and **zero NetworkVariables/NetworkTransform**, identical feel to today (still broadcasting).
2. **Area of interest.** Add `AreaOfInterestSystem` + `ReplicationHub` per-client targeted snapshots +
   region keys + hysteresis. *Deliverable:* clients load only players in range/region; far players never
   spawn locally.
3. **Underworld + tuning.** Region-key enter/leave wiring, `ReplicationSettings` + tuner + `JsonPref`.
   *Deliverable:* overworld/room isolation verified; radius/rate tunable + persistent.

## 11. Verification plan

Per the project's unity-mcp flow (`execute_code` is broken; use **refresh `scope=all` → poll
`editor_state.isCompiling` → `read_console` (clean) → enter Play → screenshot**; the MCP screenshot captures
the camera, **not** IMGUI panels):

- **Phase 1:** host + a second client (manual Host click — MCP can't drive the IMGUI Host button); both
  move via WASD + click-to-move and see each other as ghosts; enter/leave a dungeon works; console clean of
  NetworkVariable/NetworkTransform references; profiler shows the player NetworkObject carries no transform
  sync.
- **Phase 2:** with players placed far apart, a client's hierarchy shows **no ghost** for an out-of-range
  player, and the ghost spawns when they cross `showRadius` and despawns past `hideRadius` (no flicker at the
  boundary).
- **Phase 3 (underworld):** an overworld client never sees a player who has entered the underworld (and
  vice-versa); two players in different rooms don't see each other; two in the same room do; teleport jumps
  cleanly (no interpolation streak).
- **AOI unit tests:** `AreaOfInterestSystem` — region isolation, radius in/out, hysteresis, self-always-
  visible (pure functions; add an `Assembly-CSharp`-reachable test asmdef if needed, mirroring the existing
  world-gen tests; **not** in the Commands asmdef).

## 12. Open assumptions & deferred

- **`Player.prefab` is currently uncommitted-modified.** Before editing it, Ryan should commit/stash that
  WIP so the prefab restructure (strip transform/visuals, swap component) doesn't clobber or bundle it. Same
  caution for `SampleScene.unity`.
- **Local-player feel** = server round-trip with interpolation (parity with today's server-authoritative
  NetworkTransform). Client-side prediction is deferred.
- **Reliable full-AOI snapshots** in v1 (simple, correct). Snapshot **deltas**, an **unreliable position +
  reliable enter/leave** split, and **dense-crowd priority caps** (when a town packs >N into one radius) are
  deferred optimizations.
- **Minimap / "who's online":** a client now only knows nearby players, so any all-players UI shows the
  local set (the server/host has all; remote clients don't). Acceptable/desired at 100+; revisit if a global
  roster is needed.
- **Host CPU / dedicated server** is out of scope; AOI targets the bandwidth wall, which is the current
  bottleneck on the Relay host model.
