# Logic / Data / Visual Architecture — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure every system to a one-way Input→Logic→Data→Visual flow (logic writes data, visual reads data & writes only visuals), preserving all current behavior.

**Architecture:** Plain C# state objects for data; logic systems/managers that write data only; `*View` visual readers. New types take role suffixes (`*State`/`*System`/`*View`); already-conformant systems are documented, not rewritten. Camera and Player are the real restructures; Net gets its data split out.

**Tech Stack:** Unity 6000.3.x, Netcode for GameObjects, new Input System, IMGUI/uGUI. Verification = unity-mcp **refresh → console → Play/screenshot** (no unit-test harness for these systems; `execute_code` broken).

---

## Pre-flight notes

- **Verify chain (every phase):** `refresh_unity` (use `scope=all` whenever NEW `.cs` files were added, else `scope=scripts`) → poll `mcpforunity://editor/state` until `ready_for_tools` → `read_console` types `["error","warning"]` → enter Play → `read_console` → screenshot → stop.
- **GUID safety:** `PlayerController.cs` is on the Player prefab — rename it by moving `.cs`+`.cs.meta` together. Plain-C# files (`CameraRig`) aren't GUID-referenced and can be replaced freely.
- **Commits:** plain messages, no Claude attribution. Stage only the files each task touches; never `git add -A` (the tree has the user's unrelated `BiomeTuner` deletion + `SampleScene.unity` edits).
- **Prefab step (P2):** adding the new `PlayerView` component to the Player prefab is a scene/prefab edit done via MCP — detailed in Task 2.6.

---

## Phase 0 — The contract doc

### Task 0.1: Write `ARCHITECTURE.md`

**Files:**
- Create: `Assets/_Scripts/ARCHITECTURE.md`

- [ ] **Step 1: Create `Assets/_Scripts/ARCHITECTURE.md`:**

```markdown
# Script architecture — Logic / Data / Visual

One-way dependency flow: **Input → Logic → Data → Visual.** This is a discipline of dependency
direction, not ECS. Managers/OOP are fine.

| Role | Names | Reads | Writes | Never |
|------|-------|-------|--------|-------|
| Data   | plain nouns / `*State`, `*Settings` | — | (mutated by logic) | no behavior; no refs to logic/visual |
| Logic  | `*System`, manager, `*Generator`    | input, data | data only | transforms/animators/materials/UI |
| Visual | `*View` (+ HUDs)                    | data | visual only | game data |

- **Update order enforces the arrow:** logic in `Update`, visual in `LateUpdate` (or `Game` ticks
  logic→visual in order), so visual sees the finished frame.
- **Flow:** continuous state → visual pulls each frame; discrete events → the `GameLog` event bus.
- **`Game`** is the composition root — the only place that wires across concerns.
- **Input lane:** device reading lives in logic/input helpers that write intent into data;
  input-driven UI (TunerPanels, CommandConsole) editing config/command data is part of this lane.

## System roles

- **Player/** — Data: `PlayerMotion`. Logic: `PlayerInput` (helper) + `PlayerMovement` (NetworkBehaviour).
  Visual: `PlayerView`. (Server writes the replicated transform = data; `PlayerView` reads it → animator.)
- **Camera/** — Data: `CameraState`. Logic: `CameraSystem`. Visual: `CameraView` (sole camera writer).
- **Net/** — Data: `NetState`. Logic: `RelayConnector`. Visual: `RelayTestHUD`.
- **World/** — Data: `*Settings` + `World`'s cache. Logic: `BiomeGenerator`/`GroundGenerator` + `World` queries.
  Visual: `WorldView`/`CellRenderer`/`WaterMaterial`.
- **Commands/** — Data: `Command`/`CommandResult`/`CommandScope`/`OutputType`. Logic: `CommandRegistry`/
  `CommandRouter`. Visual: `CommandConsole`/`ChatPopup`. (Own assembly — keep it pure.)
- **Core/** — `GameLog` (data bus), `InputState` (data flag), `JsonPref` (persistence util).
- **Game.cs** — composition root: builds + ticks systems (logic) then views (visual).
```

- [ ] **Step 2: Import + commit**

Run `refresh_unity scope=all` (imports the new text asset). Then:
```bash
git add "Assets/_Scripts/ARCHITECTURE.md" "Assets/_Scripts/ARCHITECTURE.md.meta"
git commit -m "Add Logic/Data/Visual architecture contract"
```
(If Unity didn't generate the `.meta` yet, commit just the `.md` and add the `.meta` next commit.)

---

## Phase 1 — Camera split

**Outcome:** `CameraRig` → `CameraState` (data) + `CameraSystem` (logic, writes state) + `CameraView` (visual, sole camera writer). `Game` ticks system then view; `WorldView` writes `CameraState.Bounds`.

### Task 1.1: Create `CameraState` (data)

**Files:**
- Create: `Assets/_Scripts/Camera/CameraState.cs`

- [ ] **Step 1: Create the file:**

```csharp
using UnityEngine;

// Data: the camera's live state. Inputs Bounds (set by WorldView) and FollowTarget (set by Game); outputs
// Position + OrthoSize (computed by CameraSystem, applied by CameraView). Plain data — no behavior.
public sealed class CameraState
{
    public Rect Bounds;            // loaded vision window the viewport must stay inside
    public Vector2? FollowTarget;  // local player world position to keep on-screen (null = none)
    public Vector3 Position;       // authoritative camera position (CameraView writes it onto the Camera)
    public float OrthoSize;        // authoritative orthographic size
}
```

### Task 1.2: Create `CameraSystem` (logic) from `CameraRig`

**Files:**
- Create: `Assets/_Scripts/Camera/CameraSystem.cs`
- Delete: `Assets/_Scripts/Camera/CameraRig.cs` (+ `.meta`) — plain class, not GUID-referenced

- [ ] **Step 1: Create `Assets/_Scripts/Camera/CameraSystem.cs`** (CameraRig's logic, operating on `CameraState.Position`/`OrthoSize` instead of the camera; the camera is read only for projection):

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

// Logic: LoL-style 2D camera control — wheel-zoom to cursor, drag/keyboard pan, spacebar recenter, bounds
// clamp, pixel-perfect snap. Reads input + CameraState + the camera's projection (aspect/ScreenToWorld), and
// writes the desired Position/OrthoSize into CameraState. Never writes the camera — CameraView does that.
public sealed class CameraSystem
{
    public struct Config
    {
        public int minCellsVisible;
        public int startCellsVisible;
        public float keyboardPanSpeed;
        public float recenterDuration;
    }

    readonly Camera cam;
    readonly Config cfg;
    readonly float cellWorld;
    readonly CameraState state;
    const float PixelsPerUnit = 16f;
    const float FollowEdgeInset = 0.15f;

    bool dragPanning;
    Vector3 dragAnchorWorld;
    bool recentering;
    Vector3 recenterFrom;
    float recenterT;
    int lastScreenW, lastScreenH;

    public CameraSystem(Camera cam, Config cfg, float cellWorld, CameraState state)
    {
        this.cam = cam; this.cfg = cfg; this.cellWorld = cellWorld; this.state = state;
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;
        state.Position = cam.transform.position;                       // seed from the camera's boot position
        state.OrthoSize = ClampOrtho(CellsToOrtho(cfg.startCellsVisible));
    }

    public void Tick(float dt)
    {
        if (Screen.width != lastScreenW || Screen.height != lastScreenH)
        {
            lastScreenW = Screen.width; lastScreenH = Screen.height;
            state.OrthoSize = ClampOrtho(state.OrthoSize);
        }

        if (SpacePressed()) { recentering = true; recenterT = 0f; recenterFrom = state.Position; }

        if (recentering) TickRecenter(dt);
        else HandlePan(dt);

        SnapOrthoPixelPerfect();
        KeepInView();
        SnapPosPixelPerfect();
        ClampPos();
    }

    float CellsToOrtho(int cells) => cells * cellWorld * 0.5f / Mathf.Min(1f, Mathf.Max(cam.aspect, 0.0001f));
    float ClampOrtho(float ortho) => Mathf.Max(ortho, CellsToOrtho(cfg.minCellsVisible));

    void SnapOrthoPixelPerfect()
    {
        float sh = Mathf.Max(1f, Screen.height);
        float minOrtho = CellsToOrtho(cfg.minCellsVisible);
        float ppt = Mathf.Round(sh / (2f * state.OrthoSize * PixelsPerUnit));
        float pptHi = Mathf.Max(1f, Mathf.Floor(sh / (2f * PixelsPerUnit * minOrtho)));
        ppt = Mathf.Clamp(ppt, 1f, pptHi);
        state.OrthoSize = sh / (2f * PixelsPerUnit * ppt);
    }

    void SnapPosPixelPerfect()
    {
        float wpt = 1f / PixelsPerUnit;
        var p = state.Position;
        p.x = Mathf.Round(p.x / wpt) * wpt;
        p.y = Mathf.Round(p.y / wpt) * wpt;
        state.Position = p;
    }

    void ClampPos()
    {
        if (state.Bounds.width <= 0f || state.Bounds.height <= 0f) return;
        float h = state.OrthoSize, w = h * cam.aspect;
        var p = state.Position;
        float minX = state.Bounds.xMin + w, maxX = state.Bounds.xMax - w;
        float minY = state.Bounds.yMin + h, maxY = state.Bounds.yMax - h;
        p.x = minX > maxX ? state.Bounds.center.x : Mathf.Clamp(p.x, minX, maxX);
        p.y = minY > maxY ? state.Bounds.center.y : Mathf.Clamp(p.y, minY, maxY);
        state.Position = p;
    }

    void KeepInView()
    {
        if (!state.FollowTarget.HasValue) return;
        Vector2 t = state.FollowTarget.Value;
        float h = state.OrthoSize, w = h * cam.aspect;
        float ix = w * FollowEdgeInset, iy = h * FollowEdgeInset;
        var p = state.Position;
        p.x = Mathf.Clamp(p.x, t.x - (w - ix), t.x + (w - ix));
        p.y = Mathf.Clamp(p.y, t.y - (h - iy), t.y + (h - iy));
        state.Position = p;
    }

    void HandlePan(float dt)
    {
        Vector3 screen = PointerScreen();
        if (PanDown()) { dragPanning = true; dragAnchorWorld = cam.ScreenToWorldPoint(screen); }
        if (PanUp()) dragPanning = false;
        if (dragPanning) state.Position += dragAnchorWorld - cam.ScreenToWorldPoint(screen);

        Vector2 kb = KeyboardPan();
        if (kb.sqrMagnitude > 0f)
            state.Position += (Vector3)kb * (cfg.keyboardPanSpeed * (state.OrthoSize / 8f) * dt);
    }

    void TickRecenter(float dt)
    {
        if (PanDown() || KeyboardPan().sqrMagnitude > 0f) { recentering = false; return; }
        Vector3 to = state.FollowTarget.HasValue
            ? new Vector3(state.FollowTarget.Value.x, state.FollowTarget.Value.y, state.Position.z)
            : new Vector3(state.Bounds.center.x, state.Bounds.center.y, state.Position.z);
        if (cfg.recenterDuration <= 0f) { state.Position = to; recentering = false; return; }
        recenterT += dt / cfg.recenterDuration;
        if (recenterT >= 1f) { state.Position = to; recentering = false; return; }
        float t = 1f - Mathf.Pow(1f - recenterT, 3f);
        state.Position = Vector3.LerpUnclamped(recenterFrom, to, t);
    }

    Vector3 PointerScreen()
    {
        var m = Mouse.current;
        if (m == null) return Vector3.zero;
        var p = m.position.ReadValue();
        return new Vector3(p.x, p.y, 0f);
    }

    bool PanDown() { var m = Mouse.current; return m != null && m.rightButton.wasPressedThisFrame; }
    bool PanUp()   { var m = Mouse.current; return m != null && m.rightButton.wasReleasedThisFrame; }

    Vector2 KeyboardPan()
    {
        Vector2 v = Vector2.zero;
        if (InputState.Typing) return v;
        var kb = Keyboard.current;
        if (kb == null) return v;
        if (kb.leftArrowKey.isPressed) v.x -= 1f;
        if (kb.rightArrowKey.isPressed) v.x += 1f;
        if (kb.downArrowKey.isPressed) v.y -= 1f;
        if (kb.upArrowKey.isPressed) v.y += 1f;
        return v;
    }

    bool SpacePressed()
    {
        if (InputState.Typing) return false;
        var kb = Keyboard.current;
        return kb != null && kb.spaceKey.wasPressedThisFrame;
    }
}
```

- [ ] **Step 2: Delete the old file**

```bash
rm "Assets/_Scripts/Camera/CameraRig.cs" "Assets/_Scripts/Camera/CameraRig.cs.meta"
```

### Task 1.3: Create `CameraView` (visual)

**Files:**
- Create: `Assets/_Scripts/Camera/CameraView.cs`

- [ ] **Step 1: Create the file:**

```csharp
using UnityEngine;

// Visual: the SOLE writer of the camera transform. Mirrors CameraState (computed by CameraSystem) onto the
// Camera, called by Game right after CameraSystem.Tick. Reads data; writes only the camera.
public sealed class CameraView
{
    public void Apply(Camera cam, CameraState s)
    {
        cam.transform.position = s.Position;
        cam.orthographicSize = s.OrthoSize;
    }
}
```

### Task 1.4: Wire `Game` + `WorldView` to the camera trio

**Files:**
- Modify: `Assets/_Scripts/Game.cs`
- Modify: `Assets/_Scripts/World/WorldView.cs`

- [ ] **Step 1: `Game.cs` — replace the `rig` field** (`CameraRig rig;`):
```csharp
    CameraState cameraState;
    CameraSystem cameraSystem;
    CameraView cameraView;
```

- [ ] **Step 2: `Game.cs` — replace the rig construction in `Awake`.**

Old:
```csharp
        rig = new CameraRig(Cam, new CameraRig.Config
        {
            minCellsVisible = minCellsVisible,
            startCellsVisible = startCellsVisible,
            keyboardPanSpeed = keyboardPanSpeed,
            recenterDuration = recenterDuration,
        }, cellWorld);
```
New:
```csharp
        cameraState = new CameraState();
        cameraSystem = new CameraSystem(Cam, new CameraSystem.Config
        {
            minCellsVisible = minCellsVisible,
            startCellsVisible = startCellsVisible,
            keyboardPanSpeed = keyboardPanSpeed,
            recenterDuration = recenterDuration,
        }, cellWorld, cameraState);
        cameraView = new CameraView();
        cameraView.Apply(Cam, cameraState);
```

- [ ] **Step 3: `Game.cs` — pass `cameraState` to `WorldView`** (the last constructor argument).

Old:
```csharp
        }, cellWorld, transform, summerSheet, waterSheet, rig);
```
New:
```csharp
        }, cellWorld, transform, summerSheet, waterSheet, cameraState);
```

- [ ] **Step 4: `Game.cs` — update `Update()`.**

Old:
```csharp
        if (rig == null) return;   // e.g. after an edit-during-play domain reload; a fresh Play fixes it
        var lp = PlayerController.LocalInstance;
        rig.FollowTarget = lp != null ? (Vector2?)(Vector2)lp.transform.position : null;
        rig.Tick(Time.unscaledDeltaTime);
        view.Follow(lp != null ? lp.CurrentCell() : Vector2Int.zero);
```
New:
```csharp
        if (cameraSystem == null) return;   // e.g. after an edit-during-play domain reload; a fresh Play fixes it
        var lp = PlayerController.LocalInstance;
        cameraState.FollowTarget = lp != null ? (Vector2?)(Vector2)lp.transform.position : null;
        cameraSystem.Tick(Time.unscaledDeltaTime);
        cameraView.Apply(Cam, cameraState);
        view.Follow(lp != null ? lp.CurrentCell() : Vector2Int.zero);
```
(`PlayerController.LocalInstance` stays for now — it becomes `PlayerMovement.LocalInstance` in Phase 2.)

- [ ] **Step 5: `WorldView.cs` — swap the `CameraRig` dependency for `CameraState`.**

Change the field:
```csharp
    readonly CameraRig rig;
```
to:
```csharp
    readonly CameraState cameraState;
```
Change the constructor parameter `CameraRig rig` to `CameraState cameraState` and the assignment `this.rig = rig;` to `this.cameraState = cameraState;`.
In `RebuildMesh`, change:
```csharp
        if (rig != null) rig.Bounds = new Rect((meshCenter.x - cfg.viewRadius) * cellWorld,
                                               (meshCenter.y - cfg.viewRadius) * cellWorld, size, size);
```
to:
```csharp
        if (cameraState != null) cameraState.Bounds = new Rect((meshCenter.x - cfg.viewRadius) * cellWorld,
                                                               (meshCenter.y - cfg.viewRadius) * cellWorld, size, size);
```

### Task 1.5: Verify + commit Phase 1

- [ ] **Step 1: Verify** — `refresh_unity scope=all` → editor_state ready → `read_console` (0 errors) → Play → screenshot. Confirm: right-drag pan, arrow-key pan, mouse-wheel zoom-to-cursor, spacebar recenter, and bounds clamp (no black past the loaded window) all behave as before. Stop.

- [ ] **Step 2: Commit**
```bash
git add Assets/_Scripts/Camera/CameraState.cs Assets/_Scripts/Camera/CameraState.cs.meta Assets/_Scripts/Camera/CameraSystem.cs Assets/_Scripts/Camera/CameraSystem.cs.meta Assets/_Scripts/Camera/CameraView.cs Assets/_Scripts/Camera/CameraView.cs.meta Assets/_Scripts/Game.cs Assets/_Scripts/World/WorldView.cs
git rm --cached "Assets/_Scripts/Camera/CameraRig.cs" "Assets/_Scripts/Camera/CameraRig.cs.meta" 2>/dev/null; true
git commit -m "Camera: split CameraRig into CameraState/CameraSystem/CameraView"
```

---

## Phase 2 — Player split

**Outcome:** `PlayerController` → `PlayerMotion` (data) + `PlayerInput` (logic helper) + `PlayerMovement` (NetworkBehaviour logic) + `PlayerView` (visual). The Player prefab keeps its `PlayerMovement` component (renamed in place, GUID preserved) and gains a `PlayerView` component.

### Task 2.1: Create `PlayerMotion` (data)

**Files:**
- Create: `Assets/_Scripts/Player/PlayerMotion.cs`

- [ ] **Step 1: Create the file:**

```csharp
using System.Collections.Generic;
using UnityEngine;

// Data: server-side tile-movement state for one player. Logic (PlayerMovement) writes it; it is never
// replicated (clients see only the authoritative transform). Plain data — no behavior.
public sealed class PlayerMotion
{
    public Vector2Int cell;
    public bool moving;
    public Vector2Int toCell;
    public Vector2 fromPos, toPos;
    public float moveT, stepDuration;
    public bool hasTarget;
    public Vector2Int targetCell;
    public readonly List<Vector2Int> path = new();   // A* route (cells to step onto, in order)
    public int pathIndex;
}
```

### Task 2.2: Create `PlayerInput` (logic helper)

**Files:**
- Create: `Assets/_Scripts/Player/PlayerInput.cs`

- [ ] **Step 1: Create the file:**

```csharp
using UnityEngine;
using UnityEngine.InputSystem;

// Logic helper: reads the owning client's devices and returns movement intent. Holds only double-click
// timing state. Writes nothing itself — PlayerMovement applies the intent to the networked state.
public sealed class PlayerInput
{
    public struct Intent
    {
        public Vector2 dir;           // raw 8-way WASD direction (each axis -1/0/1); zero while typing
        public bool hasClickTarget;   // true on a double-left-click
        public Vector2 clickWorld;    // world point of the double-click
    }

    float lastClickTime = -1f;
    Vector2 lastClickPos;
    const float DoubleClickTime = 0.3f, DoubleClickPixels = 24f;

    public Intent Read(Camera cam)
    {
        var result = new Intent();
        if (InputState.Typing) return result;          // command line open: no movement, no clicks

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed) result.dir.y += 1f;
            if (kb.sKey.isPressed) result.dir.y -= 1f;
            if (kb.aKey.isPressed) result.dir.x -= 1f;
            if (kb.dKey.isPressed) result.dir.x += 1f;
        }

        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame)
        {
            float now = Time.unscaledTime;
            Vector2 sp = mouse.position.ReadValue();
            bool isDouble = now - lastClickTime <= DoubleClickTime && Vector2.Distance(sp, lastClickPos) <= DoubleClickPixels;
            lastClickTime = isDouble ? -1f : now;       // reset after a double so a 3rd click starts fresh
            lastClickPos = sp;
            if (isDouble && cam != null)
            {
                Vector3 wp = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, Mathf.Abs(cam.transform.position.z)));
                result.hasClickTarget = true;
                result.clickWorld = new Vector2(wp.x, wp.y);
            }
        }
        return result;
    }
}
```

### Task 2.3: Create `PlayerView` (visual)

**Files:**
- Create: `Assets/_Scripts/Player/PlayerView.cs`

- [ ] **Step 1: Create the file:**

```csharp
using UnityEngine;

// Visual: derives Idle/Walk + 4-way facing from the replicated transform and drives the Animator. Reads the
// transform (data); writes only the animator. Runs on every client. Never touches movement state.
public sealed class PlayerView : MonoBehaviour
{
    [SerializeField] Animator animator;

    Vector3 lastPos;
    Vector2 facing = new(1f, -1f);                  // default SE (down-right)
    static readonly int SpeedHash = Animator.StringToHash("Speed");
    static readonly int DirXHash = Animator.StringToHash("DirX");
    static readonly int DirYHash = Animator.StringToHash("DirY");

    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        lastPos = transform.position;
    }

    void OnEnable()
    {
        if (animator != null) { animator.SetFloat(DirXHash, facing.x); animator.SetFloat(DirYHash, facing.y); }
    }

    void LateUpdate()
    {
        Vector3 pos = transform.position;
        Vector2 delta = (Vector2)(pos - lastPos);
        lastPos = pos;

        float speed = delta.magnitude / Mathf.Max(Time.deltaTime, 1e-5f);
        if (animator != null) animator.SetFloat(SpeedHash, speed);

        if (delta.sqrMagnitude > 1e-6f)
        {
            Vector2 d = delta.normalized;
            if (Mathf.Abs(d.y) < 0.2f) d.y -= 0.2f;     // bias near-horizontal toward facing the camera (down)
            facing = d.normalized;
            if (animator != null)
            {
                animator.SetFloat(DirXHash, facing.x);
                animator.SetFloat(DirYHash, facing.y);
            }
        }
    }
}
```

### Task 2.4: Rename `PlayerController` → `PlayerMovement` (logic), strip visuals

**Files:**
- Rename: `Assets/_Scripts/Player/PlayerController.cs` → `Assets/_Scripts/Player/PlayerMovement.cs` (+ `.meta`, GUID preserved)
- Rewrite the renamed file's contents

- [ ] **Step 1: Write the new content to a new file** `Assets/_Scripts/Player/PlayerMovement.cs`:

```csharp
using Unity.Netcode;
using UnityEngine;

// Server-authoritative tile movement (LOGIC). The OWNER submits intent (a NetworkVariable dir + a click
// ServerRpc) via PlayerInput; the SERVER consumes intent, runs Pathfinder, and writes PlayerMotion + the
// authoritative transform (replicated by NetworkTransform). Walk/Idle + facing are derived separately by
// PlayerView from the replicated transform.
[RequireComponent(typeof(NetworkObject))]
public class PlayerMovement : NetworkBehaviour
{
    public static PlayerMovement LocalInstance { get; private set; }   // the owning client's own player

    [Header("Movement (server)")]
    [SerializeField] float moveSpeed = 4f;          // world units/sec while crossing a cell

    // Owner -> server intent: 8-way step direction, each axis in {-1,0,1}. Owner-writable.
    readonly NetworkVariable<Vector2> moveInput =
        new(Vector2.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    readonly PlayerMotion motion = new();   // server-side movement state (data)
    readonly PlayerInput input = new();      // owner input reader (logic helper)

    public override void OnNetworkSpawn()
    {
        if (IsOwner) LocalInstance = this;
        if (IsServer)
        {
            var gm = Game.Instance;
            if (gm != null)
            {
                int n = (int)OwnerClientId;
                motion.cell = new Vector2Int((n % 5) - 2, (n / 5) - 2);   // small spread around the origin
                transform.position = (Vector3)gm.CellCenter(motion.cell.x, motion.cell.y);
            }
        }
    }

    public override void OnNetworkDespawn()
    {
        if (LocalInstance == this) LocalInstance = null;
    }

    // The cell the player visually occupies right now (from the replicated transform).
    public Vector2Int CurrentCell()
    {
        var gm = Game.Instance;
        return gm != null ? gm.WorldToCell(transform.position) : Vector2Int.zero;
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsOwner) ReadOwnerInput();
        if (IsServer) ServerStep(Time.deltaTime);
    }

    // ---- Owner input -> networked intent ----
    void ReadOwnerInput()
    {
        var cam = Game.Instance != null ? Game.Instance.Cam : Camera.main;
        var intent = input.Read(cam);
        if (intent.dir != moveInput.Value) moveInput.Value = intent.dir;   // raw 8-way intent (not normalized)
        if (intent.hasClickTarget) SetTargetServerRpc(intent.clickWorld);
    }

    [ServerRpc]
    void SetTargetServerRpc(Vector2 worldPoint)
    {
        var gm = Game.Instance;
        if (gm == null) return;
        motion.targetCell = gm.WorldToCell(worldPoint);
        // Route from where we'll be standing (the in-progress step's destination, else the current cell).
        Vector2Int from = motion.moving ? motion.toCell : motion.cell;
        motion.path.Clear();
        motion.path.AddRange(Pathfinder.FindPath(from, motion.targetCell, gm.IsWalkable));
        motion.pathIndex = 0;
        motion.hasTarget = motion.path.Count > 0;
    }

    // ---- Server tile movement (authoritative) ----
    void ServerStep(float dt)
    {
        var gm = Game.Instance;
        if (gm == null) return;

        if (motion.moving)
        {
            motion.moveT += dt / motion.stepDuration;
            if (motion.moveT < 1f) { transform.position = Vector2.Lerp(motion.fromPos, motion.toPos, motion.moveT); return; }
            motion.cell = motion.toCell;                            // arrived: snap to the exact cell center
            transform.position = (Vector3)motion.toPos;
            motion.moving = false;
        }

        Vector2 mi = moveInput.Value;
        if (mi.sqrMagnitude > 0.01f)
        {
            motion.hasTarget = false; motion.path.Clear();          // WASD cancels click-to-move
            TryWalkStep(gm, new Vector2Int(StepSign(mi.x), StepSign(mi.y)));
            return;
        }

        if (motion.hasTarget && motion.pathIndex < motion.path.Count)
        {
            Vector2Int next = motion.path[motion.pathIndex++];
            if (!gm.IsWalkable(next.x, next.y)) { motion.hasTarget = false; motion.path.Clear(); return; }
            BeginStep(gm, motion.cell, next);
            if (motion.pathIndex >= motion.path.Count) motion.hasTarget = false;
        }
    }

    // WASD: step if walkable; a diagonal needs both orthogonal cells open, else slide along the clear axis.
    void TryWalkStep(Game gm, Vector2Int step)
    {
        if (step == Vector2Int.zero) return;
        bool diag = step.x != 0 && step.y != 0;
        bool sideX = gm.IsWalkable(motion.cell.x + step.x, motion.cell.y);
        bool sideY = gm.IsWalkable(motion.cell.x, motion.cell.y + step.y);

        if (gm.IsWalkable(motion.cell.x + step.x, motion.cell.y + step.y) && (!diag || (sideX && sideY)))
        {
            BeginStep(gm, motion.cell, motion.cell + step);
            return;
        }
        if (!diag) return;
        if (sideX) BeginStep(gm, motion.cell, new Vector2Int(motion.cell.x + step.x, motion.cell.y));
        else if (sideY) BeginStep(gm, motion.cell, new Vector2Int(motion.cell.x, motion.cell.y + step.y));
    }

    void BeginStep(Game gm, Vector2Int from, Vector2Int to)
    {
        motion.fromPos = gm.CellCenter(from.x, from.y);
        motion.toPos = gm.CellCenter(to.x, to.y);
        motion.toCell = to;
        motion.stepDuration = Mathf.Max(Vector2.Distance(motion.fromPos, motion.toPos) / Mathf.Max(moveSpeed, 0.01f), 1e-4f);
        motion.moveT = 0f;
        motion.moving = true;
    }

    static int StepSign(float v) => v > 0.001f ? 1 : (v < -0.001f ? -1 : 0);
}
```

- [ ] **Step 2: Preserve the GUID + remove the old file.**

```bash
cd "C:/Users/ryans/this again"
git mv Assets/_Scripts/Player/PlayerController.cs.meta Assets/_Scripts/Player/PlayerMovement.cs.meta
git rm Assets/_Scripts/Player/PlayerController.cs
```
(The new `PlayerMovement.cs` written in Step 1 now pairs with the preserved `.meta` GUID, so the Player prefab's component stays linked and `moveSpeed` persists. The old `animator` serialized ref is dropped — re-added via `PlayerView` in Task 2.6.)

### Task 2.5: Point `Game` at `PlayerMovement`

**Files:**
- Modify: `Assets/_Scripts/Game.cs`

- [ ] **Step 1:** In `Update()`, change `var lp = PlayerController.LocalInstance;` to:
```csharp
        var lp = PlayerMovement.LocalInstance;
```

### Task 2.6: Compile, then add `PlayerView` to the Player prefab

**Files:** Player prefab (path discovered at runtime)

- [ ] **Step 1: Compile-check** — `refresh_unity scope=all` → editor_state ready → `read_console` (expect 0 errors; the renamed component + new types compile). Do NOT Play yet — the prefab still lacks `PlayerView`, so animation wouldn't run.

- [ ] **Step 2: Find the Player prefab** — call `manage_asset` action=search filterType=Prefab (or inspect the scene `NetworkManager`'s NetworkPrefabs list / `find_gameobjects by_component=PlayerMovement`). Note the prefab path.

- [ ] **Step 3: Add the `PlayerView` component to the prefab** — via `manage_gameobject`/`manage_components` (add component `PlayerView` to the Player prefab root, the same GameObject as `PlayerMovement`). `PlayerView.Awake` auto-resolves the Animator via `GetComponentInChildren<Animator>()`, so no manual field assignment is required unless the Animator isn't found.

- [ ] **Step 4:** `read_console` (0 errors); confirm the prefab now lists both `PlayerMovement` and `PlayerView`.

### Task 2.7: Verify + commit Phase 2

- [ ] **Step 1: Verify** — Play. Confirm: WASD steps tile-by-tile (with diagonal corner-cut rule), double-left-click auto-moves with A* around water, and the character animates (walk/idle + 4-way facing). If two editors/clients are available, confirm a second client sees the first move. Screenshot. Stop.

- [ ] **Step 2: Commit**
```bash
cd "C:/Users/ryans/this again"
git add Assets/_Scripts/Player/PlayerMotion.cs Assets/_Scripts/Player/PlayerMotion.cs.meta Assets/_Scripts/Player/PlayerInput.cs Assets/_Scripts/Player/PlayerInput.cs.meta Assets/_Scripts/Player/PlayerView.cs Assets/_Scripts/Player/PlayerView.cs.meta Assets/_Scripts/Player/PlayerMovement.cs Assets/_Scripts/Player/PlayerMovement.cs.meta Assets/_Scripts/Game.cs
git rm --cached Assets/_Scripts/Player/PlayerController.cs 2>/dev/null; true
# Stage the Player prefab too (path from Task 2.6 Step 2), e.g.:
# git add "Assets/<path-to-Player.prefab>"
git commit -m "Player: split PlayerController into PlayerMotion/PlayerInput/PlayerMovement/PlayerView"
```

---

## Phase 3 — Net data split

**Outcome:** `RelayConnector`'s status/joinCode move into a `NetState` data object; `RelayTestHUD` reads `NetState`.

### Task 3.1: Create `NetState` (data)

**Files:**
- Create: `Assets/_Scripts/Net/NetState.cs`

- [ ] **Step 1: Create the file:**

```csharp
// Data: networking status surfaced to the HUD. Logic (RelayConnector) writes it; visual (RelayTestHUD) reads
// it. Plain data — no behavior.
public sealed class NetState
{
    public string status = "offline";
    public string joinCode = "";
}
```

### Task 3.2: `RelayConnector` writes `NetState`

**Files:**
- Modify: `Assets/_Scripts/Net/RelayConnector.cs`

- [ ] **Step 1: Replace the `JoinCode` property + `status`/`Status` property** (lines ~15-22):

Old:
```csharp
    public string JoinCode { get; private set; }

    string status = "offline";
    public string Status
    {
        get => status;
        private set { status = value; GameLog.Post(OutputType.System, value); }   // surface relay progress in the chat popup
    }
```
New:
```csharp
    public NetState State { get; } = new();   // logic writes this; RelayTestHUD reads it

    void SetStatus(string s) { State.status = s; GameLog.Post(OutputType.System, s); }   // surface progress in chat
```

- [ ] **Step 2: Update `HostAsync`** — replace its body's status/joincode writes:

Old:
```csharp
            Status = "hosting…";
            await InitAndSignIn();
            Allocation alloc = await RelayService.Instance.CreateAllocationAsync(maxConnections);
            JoinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
            transport.SetRelayServerData(new RelayServerData(alloc, "dtls"));
            NetworkManager.Singleton.StartHost();
            Status = $"host up · code {JoinCode}";
        }
        catch (System.Exception e) { Status = "host failed: " + e.Message; Debug.LogException(e); }
```
New:
```csharp
            SetStatus("hosting…");
            await InitAndSignIn();
            Allocation alloc = await RelayService.Instance.CreateAllocationAsync(maxConnections);
            State.joinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
            transport.SetRelayServerData(new RelayServerData(alloc, "dtls"));
            NetworkManager.Singleton.StartHost();
            SetStatus($"host up · code {State.joinCode}");
        }
        catch (System.Exception e) { SetStatus("host failed: " + e.Message); Debug.LogException(e); }
```

- [ ] **Step 3: Update `JoinAsync`** — replace its status writes:

Old:
```csharp
            Status = "joining…";
            await InitAndSignIn();
            JoinAllocation alloc = await RelayService.Instance.JoinAllocationAsync(joinCode);
            transport.SetRelayServerData(new RelayServerData(alloc, "dtls"));
            NetworkManager.Singleton.StartClient();
            Status = $"client → {joinCode}";
        }
        catch (System.Exception e) { Status = "join failed: " + e.Message; Debug.LogException(e); }
```
New:
```csharp
            SetStatus("joining…");
            await InitAndSignIn();
            JoinAllocation alloc = await RelayService.Instance.JoinAllocationAsync(joinCode);
            transport.SetRelayServerData(new RelayServerData(alloc, "dtls"));
            NetworkManager.Singleton.StartClient();
            SetStatus($"client → {joinCode}");
        }
        catch (System.Exception e) { SetStatus("join failed: " + e.Message); Debug.LogException(e); }
```

### Task 3.3: `RelayTestHUD` reads `NetState`

**Files:**
- Modify: `Assets/_Scripts/Net/RelayTestHUD.cs`

- [ ] **Step 1: Update `OnGUI`'s reads** (lines ~19-21):

Old:
```csharp
        if (net.Status != lastStatus) { lastStatus = net.Status; status.text = "Net: " + lastStatus; }
        if (net.JoinCode != lastCode) { lastCode = net.JoinCode; share.text = "Share code: " + lastCode; }
        bool hasCode = !string.IsNullOrEmpty(net.JoinCode);
```
New:
```csharp
        if (net.State.status != lastStatus) { lastStatus = net.State.status; status.text = "Net: " + lastStatus; }
        if (net.State.joinCode != lastCode) { lastCode = net.State.joinCode; share.text = "Share code: " + lastCode; }
        bool hasCode = !string.IsNullOrEmpty(net.State.joinCode);
```

### Task 3.4: Verify + commit Phase 3

- [ ] **Step 1: Verify** — `refresh_unity scope=all` → ready → `read_console` (0 errors) → Play → click **Host** in the Relay HUD; confirm the status text updates ("hosting…" → "host up · code …") and the share code appears (and the same line shows in the chat popup via `GameLog`). Stop.

- [ ] **Step 2: Commit**
```bash
git add Assets/_Scripts/Net/NetState.cs Assets/_Scripts/Net/NetState.cs.meta Assets/_Scripts/Net/RelayConnector.cs Assets/_Scripts/Net/RelayTestHUD.cs
git commit -m "Net: split RelayConnector status/joinCode into NetState"
```

---

## Phase 4 — Conformance pass

**Outcome:** Confirm the already-conformant systems match `ARCHITECTURE.md`; capstone verification.

### Task 4.1: Conformance review

- [ ] **Step 1:** Re-read `Assets/_Scripts/ARCHITECTURE.md` against `World/`, `Commands/`, `UI/`, `Core/`. Confirm each type's role matches (data/logic/visual). These were verified-conformant during design — expect no code changes. If any comment still implies a logic→visual or visual→data write, fix the comment only.

- [ ] **Step 2: Run the command unit tests** (confirms the Commands assembly is untouched):
`run_tests mode=EditMode assembly_names=["Minifantasy.Commands.Tests"]` → poll `get_test_job` → expect 9/9 passed.

- [ ] **Step 3: Full smoke test** — Play and exercise: terrain renders + water animates; minimap; camera pan/zoom/recenter; player WASD + double-click + pathfinding + animation; command console open/submit; tuner sliders live-update; Relay host. Screenshot. Stop.

- [ ] **Step 4: Commit** (only if any comment fixes were made)
```bash
git add Assets/_Scripts/ARCHITECTURE.md   # + any comment-fixed files
git commit -m "Confirm L/D/V conformance across remaining systems"
```

---

## Self-Review (completed by plan author)

**Spec coverage:**
- Contract `ARCHITECTURE.md` → Task 0.1. ✓
- Camera split (`CameraState`/`CameraSystem`/`CameraView`, sole camera writer) → Tasks 1.1-1.4. ✓
- Player split (`PlayerMotion`/`PlayerInput`/`PlayerMovement`/`PlayerView`, transform=data, view reads it) → Tasks 2.1-2.6. ✓
- Net split (`NetState`) → Tasks 3.1-3.3. ✓
- Conformant systems = doc-only → Phase 4. ✓
- Netcode mapping (server writes transform+NetworkVariable; client visual reads) → Task 2.4/2.3. ✓
- Naming convention (`*State`/`*System`/`*View`) → applied throughout. ✓
- Update ordering (logic→visual; PlayerView in LateUpdate; Game ticks system then view) → Tasks 1.4/2.3. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code; edits show old→new. The Player prefab path is intentionally discovered at runtime (Task 2.6 Step 2) — not a placeholder, an editor lookup. ✓

**Type consistency:** `CameraState.Position/OrthoSize/Bounds/FollowTarget` are written by `CameraSystem` and read by `CameraView`/`WorldView` consistently. `CameraSystem.Config` matches `Game`'s usage. `PlayerInput.Intent{dir,hasClickTarget,clickWorld}` matches `PlayerMovement.ReadOwnerInput`. `PlayerMotion` fields match every `motion.*` reference in `PlayerMovement`. `PlayerMovement.LocalInstance`/`CurrentCell()` match `Game.Update`. `NetState{status,joinCode}` matches `RelayConnector` writes + `RelayTestHUD` reads. ✓

## Out of scope
- Renaming already-conformant types (e.g. `World`→`TerrainSystem`) — doc carries the labels.
- ECS/struct-of-arrays pools; per-folder asmdefs.
- `LineEditor` extraction from `CommandConsole` (separate future task).
