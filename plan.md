# Plan: Delayed-Light Visual Simulation in Godot

**Goal:** physics and gameplay run in the present. Only what the camera *sees* is delayed by the finite speed of light. A moving object is drawn where it was when the light now reaching the camera left it, so you see delays, bent spinning blades, objects that appear only when their light arrives, and so on.

## Status

| Milestone | State |
|---|---|
| M0 Project setup | Done |
| M1 Single object end to end | Done |
| M2 Many objects + moving player | Done (see notes) |
| Live speed-of-light selector (mouse wheel + HUD) | Done |
| M3 Debug view | Done |
| Debug text modes (F3: off / simple / full) and profiler | Done |
| M4 Robustness and polish | Done (see notes) |
| Demo machines: car, helicopter, standing fan | Done |
| Edge tessellation so shapes can warp | Done for primitive meshes |
| M5–M8, optional | Next: your call |

## Decisions

- **Stack:** Godot 4.7.2 (Forward+, Jolt Physics, D3D12 on Windows), **C#** (.NET, net8.0), a spatial shader. xUnit test project in `tests/`.
- **Clock:** fixed 60 Hz. `t = tick × (1/60)` (16.67 ms), advanced in `_PhysicsProcess`, never from wall-clock time. The project relies on Godot's default of 60 physics ticks per second (the editor strips it from `project.godot` because it is the default), and `SimClock.TickRate` is a constant 60.
- **Physics:** normal Godot physics, instantaneous. Nothing is delayed except the visuals.
- **Speed of light:** `c` defaults to 5 m/s so the effect is obvious, and the player steps it live through a ladder of 31 values from 1 cm/s up to the real 299,792,458 m/s.
- **No Doppler shift.**
- **Camera:** free-fly in the present, FOV 85°, FXAA on. The camera is not clamped below `c` by default (at low `c` the player could barely move).
- **Deferred:** player's own body, lighting and shadows, aberration and Lorentz contraction. (Normals are already rotated in the shader.)

## Controls and saved settings

| Input | Effect |
|---|---|
| Mouse wheel up / down | Step the speed of light faster / slower |
| WASD, Q/E (or Ctrl/Space), Shift | Fly, down/up, sprint |
| Mouse | Look (locked at start; Esc frees it, the next left click re-locks without stepping `c`) |
| F1 | Toggle the shader solve and the CPU reference solve |
| F3 | Cycle debug text: off, simple (profiler), full (profiler plus debug view). Saved to `user://debug_mode.cfg`; the first run starts in full |
| 1 / 2 / 3 | In full mode: ghosts, delay rings, delay labels and lines. Saved to `user://debug_view.cfg` |

**Command-line options** (after `--`): `--cpu`, `--c=<m/s>`, `--screenshot=<file.png>`, `--cam=x,y,z,yawDeg,pitchDeg`, `--at-tick=N`, `--stress=N`, `--start-tick=N`, `--max-edge=<m>`.

## Architecture

| Piece | Where | Role |
|---|---|---|
| `SimClock` (autoload) | `src/Core` | Owns tick and `t`. Emits `Ticked` each tick. Holds the current `C`, stepped through `SpeedLadder`; emits `CChanged`. |
| `SpeedLadder` | `src/Core` | Engine-free list of selectable values for c plus speed/distance formatting. |
| `LightHistory` | `src/History` | Component on each moving part. Records `(position, rotation)` each tick into its `HistoryTrack`. Spawn and despawn through it. |
| `HistoryTrack` | `src/History` | One part's ring buffer, lifetime (birth/death tick), teleport breaks and texture row. Engine-free, unit-tested. |
| `HistoryTexture` (autoload) | `src/History` | Packs all tracks into a Texture2DArray (one layer per 16 time slots, one row per object) plus a metadata texture. Uploads one layer per tick. Exposes `lc_history`, `lc_meta`, `lc_head`, `lc_count`, `lc_tick`, `lc_c`, `lc_age` as shader globals. `lc_age` is the time since the newest sample, so retarded time moves smoothly between ticks. |
| `RetardedSolver` | `src/History` | Engine-free C# reference solve (System.Numerics). Unit-tested. The shader mirrors it. |
| `delayed_visual.gdshader` | `shaders/` | `vertex()` does the bracket search and interpolation for the retarded transform. |
| `DelayedVisual`, `MeshTessellation`, `TessellationMath` | `src/Visuals` | The visible proxy mesh. Top-level, uses the shader, large `CustomAabb`. Tessellates a copy of primitive meshes so no edge exceeds `MaxEdgeLength` (default 0.2 m) and edges can warp. |
| `SpeedLimiter` | `src/Physics` | Keeps a RigidBody3D's fastest point below c (linear speed plus spin at its bounding radius). |
| `FreeCamera` | `src/Player` | Fly camera. Lives in the present. |
| `LightSpeedHud` | `src/Ui` | Bottom centre: c, its fraction of the real c, visible range, progress bar over the ladder, key hints and the current F3 mode. |
| `DebugMode` | `src/Debug` | Static F3 state (`None`, `Simple`, `Both`), persisted, with a `Changed` event. |
| `ProfilerOverlay` | `src/Debug` | Top-left readout in simple and full modes: fps, CPU and GPU frame time, VRAM, RAM. |
| `DebugView`, `LightRings` | `src/Debug` | Full mode only: true-position ghosts, delay rings around the camera, per-object delay labels and ghost-to-visual lines, plus a top-right panel (c, history fill, history update cost, per-object delay and distance). `LightRings` (engine-free) plans the ring delays. |
| `ScreenshotCapture` | `src/Debug` | `-- --screenshot=file.png` saves a frame and quits. For verification. |
| `Orbiter`, `Oscillator` | `src/Demo` | Deterministic kinematic test motions. |
| `CircleMover`, `Spinner` | `src/Demo` | Circular driving/flying path and a local-axis spinner. They build the car (spinning wheels), helicopter (main and tail rotors) and standing fan scenes. |
| `Spawner`, `Teleporter` | `src/Demo` | Spawn/despawn and teleport demos; `Spawner` also drives `--stress=N`. |

The physics body and the visual are separate, so the real simulation never knows the visuals are delayed.

**Parts.** A visual ignores its own node transform (the shader places vertices from the history), so every independently moving piece is a *part*: a `Node3D` with its own `LightHistory`, a ghost mesh and a `DelayedVisual`, offset through the part's transform. Compound machines nest parts (car: body, cabin, four wheels; helicopter: fuselage, boom, main rotor with two blade bars, tail rotor with a blade; fan: hub with two crossed blade bars on a static, non-delayed stand). Each part is solved separately, which is what makes rotation show the delay.

**History texture layout:** sample time is implicit (newest = now, each older sample is one tick earlier). All objects share one write head and count; each object's own birth/death tick lives in the metadata texture, with ticks stored modulo 2^20.

**Object lifetime:** spawn and despawn objects with `LightHistory` (`Despawn()`), never by freeing the body directly. Teleport with `BreakNext()`.

**Tick order** (physics priorities): `SimClock` −1000, movers 0, `LightHistory` 1000, `HistoryTexture` 2000.

## Core algorithm (per vertex)

1. World position of the vertex at candidate time: `x(t_s) = T(t_s) · local_vertex`.
2. `f(t_s) = |x_cam − x(t_s)| − c·(t_now − t_s)`.
3. `f` is monotonic when the object's speed is below `c`, so binary-search the history (about 8–14 steps) for the adjacent samples where `f` changes sign.
4. Interpolate position (lerp) and rotation (nlerp) between the two samples, then do one secant refinement.

**Edge cases**
- Light from the first sample hasn't arrived: hidden (the existence window).
- Closer than one tick of light travel: use the newest sample.
- Despawned object whose last light has passed: hidden. Until then it stays visible.
- Teleport: if the crossing lands on a history break, hidden (the gap between old and new place).
- History length: `MaxViewDistance / c` seconds at startup (100 m at 5 m/s is 20 s, 1200 samples; minimum 600, maximum 8192).

## Milestones

### M0: Project setup — done
Project, 60 Hz tick, `SimClock` autoload, basic scene.
**Done when:** tick counter and `t` print correctly and steadily. ✔

### M1: Single object end to end — done
Orbiting cube, `LightHistory`, texture upload, CPU solve then shader solve, ghost at the true position, `c` = 5 m/s.
**Done when:** the visible cube trails the ghost by exactly distance / c, and a distance-zero object shows no trail. ✔ (logged delay equals dist/c; solver unit tests; shader and CPU screenshots match)

### M2: Many objects, and a moving player — done
- Several objects with different motions: kinematic orbit and oscillators, plus real `RigidBody3D` balls (Jolt) bouncing on a collidable ground. Visual proxies are separate top-level nodes.
- Free-moving player camera in the present.
- One history row per object, indexed by object ID.
- Speed clamp below `c` for rigid bodies (`SpeedLimiter`); kinematic motions only warn at startup.
- Objects stay hidden until their first light arrives (existence window), and `CustomAabb` is huge so nothing is culled at screen edges.
- Smooth visuals between ticks via `lc_age` (the solver takes the time since the newest sample).

**Done when:** objects move correctly while the camera moves, with no popping at screen edges and no jitter.
**Verified:** solver tests (including continuity across a tick boundary), shader/CPU screenshots from several camera positions, rigid-body speed clamp measured (peak 4.65 vs limit 4.5 at c = 5).
**Not verified:** interactive camera feel and motion smoothness by eye (checked by screenshots only).

### Live speed-of-light selector — done
Mouse wheel steps `c` through `SpeedLadder` (1 cm/s … 1 m/s … speed of sound … 1 km/s … 0.1%, 1%, 10%, 25%, 50%, 90%, 99% of c … c). The HUD shows the value, its fraction of the real c, the visible range, a progress bar and the key hints. A new `c` applies immediately; history already recorded stays valid.

### M3: Debug view — done
- Toggleable ghosts (key 1).
- Delay rings (key 2): rings on the ground around the camera; an object standing on the ring labelled T is currently seen as it was T ago. Spacing adapts to c (about 4 m apart, at "nice" delays like 1 s, 100 ms, 20 ns).
- Per-object delay labels at the seen position, with a yellow line to the true position (key 3).
- Top-right panel with c, history fill, history update cost, and each object's delay and distance.
- F3 cycles the debug text off / simple / full, and the toggles are saved between runs.

**Done when:** M1 and M2 correctness can be checked visually at any time.
**Verified:** the readout delays equal distance / c (for example 15.1 m at 5 m/s shows 3.03 s); ring planner and formatting unit-tested. Delays use the CPU solver, the reference for the shader.
**Known quirk:** ghosts follow their own toggle and stay visible in the off and simple F3 modes; only the rings, labels and panel are hidden outside full mode.

### M4: Robustness and polish — done
- **Per-object tracks:** each part owns a `HistoryTrack`. Rows come from a free list and are reused.
- **Spawn:** an object is invisible until the light from its first sample reaches the camera.
- **Despawn:** `LightHistory.Despawn()` stops recording and hands the visual to the scene root. It stays visible until the light from the object's last moment has passed, then frees itself, and its row is reused once the last sample leaves the history window.
- **Teleport:** `LightHistory.BreakNext()` marks a history break. You see the old place until its last light has passed, then nothing (the gap), then the new place when its first light arrives. Two simultaneous images are not modelled.
- **History length derived from view distance** (see above). Lowering c later shrinks the visible range; the HUD shows it.
- **Partial upload:** a tick uploads one 16-slot layer instead of the whole texture. The metadata texture is rewritten only on spawn, despawn and expiry.
- **Precision:** sample time is implicit and ticks are stored modulo 2^20, so the GPU never holds a growing float time. Demo motion phases are reduced in double. `-- --start-tick=N` starts the clock later to test this.
- **Stress scene:** `-- --stress=N` drops N rigid balls at once. A default `Spawner` in the main scene spawns and despawns balls over time, and a `Teleporter` hops between four places.

**Done when:** a stress scene runs stably.
**Verified:** 407 objects ran at the display's 160 fps (GPU 0.9 ms) with the history update costing 0.19 ms per tick (0.17 ms with 10 objects). Shader and CPU screenshots matched for the visible, gap and new-place teleport states, for despawn and spawn, and across a tick-counter wrap (start tick just below 2^20 × 5). A 40 s soak kept rows bounded (21 in use) while spawning and despawning. Unit tests cover tracks, despawn visibility and the teleport gap.
**Known limits:** the 1024-row cap and the fixed history window; the shader still collapses hidden objects rather than culling them; hundreds of `DelayedVisual` materials are not batched.

### Demo machines and edge tessellation — done
- **Car** drives a circle with four spinning wheels; **helicopter** flies a leaning, bobbing circle with a four-blade main rotor and a tail rotor; **standing fan** has a static stand and a spinning hub with crossed blades. Spin speeds are chosen so every vertex stays below c = 5 m/s (main rotor tip plus aircraft motion is about 4.4 m/s).
- **Tessellation:** the solve is per vertex, so a long edge between two vertices stayed straight and could not bend. `DelayedVisual` now subdivides a copy of any primitive mesh (box, cylinder, sphere, capsule, plane/quad, prism) so no edge is longer than `MaxEdgeLength`, capped at 64 segments per axis. The ghost keeps the original mesh.

**Verified:** at c = 4 m/s the fan blades are straight with tessellation off (`--max-edge=1000`) and visibly swept with it on.

### Later
- **M5:** player's own body. Probably unnecessary because the delay is near zero. Decide the visibility rule for body parts and mirrors.
- **M6:** normals (partly done: rotated in the shader).
- **M7:** lighting and shadows. Choose between present-time lighting, delayed light sources, or a custom approach.
- **M8:** aberration and Lorentz contraction, including the camera's relativistic frame transform.
- **Optional:** per-vertex history for deforming and skinned meshes (store bone transforms, not raw vertices).

## Risks and open points

- **Low c needs long history:** the buffer is sized for the starting c, so at lower c the visible range is `c × window` (at 1 m/s you see 20 m; at 1 cm/s nothing in the arena). The HUD shows this range. The buffer is not resized when c changes.
- **Superluminal motion:** the retarded-time solve is only monotonic when nothing moves at or above c. Rigid bodies are re-clamped live by `SpeedLimiter` and teleports use history breaks, but kinematic demos and the camera are not re-clamped when c is lowered, so lowering c below a demo's speed shows artifacts. The startup warning only checks the starting c.
- **Texture upload cost:** solved by chunked layers (one layer per tick). A compute-shader write would avoid even that if rows ever grow much beyond 1024.
- **Large triangles:** done for primitive meshes (see tessellation). Not done for imported/ArrayMesh meshes (they get a warning); those need offline subdivision or a per-fragment solve. Cylinder caps are fan triangles and are not subdivided across their radius.
- **Tick-rate jitter:** if physics runs several steps in one frame, the visual solve still uses the latest tick time, so the clock stays the single source of truth.
- **Shadow pass:** the shader reads the camera position, so visuals cast no shadows until M7.
- **Profiler noise:** Godot's physics-time monitor is a single-step sample and jumps around between runs; use the `history update` figure in the debug panel for the cost of our own per-tick work.

## Testing and verification

- **Unit tests** (49, `dotnet test tests/Lightspeed.Tests`): solver (stationary, receding, orbit vs bisection, hidden window, rotation, partial-tick age), `HistoryTrack` (ring wrap, gap filling, teleport flag, retire and expiry, despawn visibility, teleport gap), `SpeedLadder`, `LightRings`, `TessellationMath`.
- **Smoke test:** headless import and run for load errors (see `CLAUDE.md`).
- **Visual checks:** screenshots with `-- --screenshot=` in shader mode and with `-- --cpu`, compared at the same tick; the debug view readouts; before/after with `--max-edge=1000`.
- **Not yet done:** a numeric GPU-vs-CPU comparison (needs shader output readback); judging smoothness and camera feel by eye while playing.
