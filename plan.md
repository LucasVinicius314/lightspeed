# Plan: Delayed-Light Visual Simulation in Godot

**Goal:** physics and gameplay run in the present. Only what the camera *sees* is delayed by the finite speed of light.

## Status

| Milestone | State |
|---|---|
| M0 Project setup | Done |
| M1 Single object end to end | Done |
| Profiler overlay (F3) | Done |
| Live speed-of-light selector (mouse clicks + HUD) | Done |
| M2 Many objects + moving player | Done (see notes) |
| M3 Debug view | Next |
| M4 Robustness and polish | Planned |
| M5–M8, optional | Later |

## Decisions

- **Stack:** Godot 4.7.2 (Forward+, Jolt), **C#** (.NET, net8.0), spatial shader. Test project in `tests/` (xUnit).
- **Clock:** fixed 60 Hz. `t = tick × (1/60)` (16.67 ms), advanced in `_PhysicsProcess`, never from wall-clock time. `physics/common/physics_ticks_per_second = 60`.
- **Physics:** normal Godot physics, instantaneous. Nothing is delayed except the visuals.
- **No Doppler shift.**
- **Deferred:** player's own body, lighting and shadows, aberration and Lorentz contraction. (Normals are already rotated in the shader.)

## Architecture

| Piece | Where | Role |
|---|---|---|
| `SimClock` (autoload) | `src/Core` | Owns tick and `t`. Emits `Ticked` each tick. Holds the current `C` (default 5 m/s), stepped through `SpeedLadder`; emits `CChanged`. |
| `LightHistory` | `src/History` | Component on each moving object. Records `(position, rotation)` each tick into a ring buffer. |
| `HistoryTexture` (autoload) | `src/History` | Packs all ring buffers into one float texture, one row per object, two texels per sample. Uploads each tick. Exposes `lc_history`, `lc_head`, `lc_count`, `lc_c`, `lc_age` as shader globals. `lc_age` is the time since the newest sample, so retarded time moves smoothly between ticks. |
| `RetardedSolver` | `src/History` | Engine-free C# reference solve (System.Numerics). Unit-tested. The shader mirrors it. |
| `delayed_visual.gdshader` | `shaders/` | `vertex()` does the bracket search and interpolation for the retarded transform. |
| `DelayedVisual` | `src/Visuals` | The visible proxy mesh. Top-level, uses the shader, large `CustomAabb`. F1 toggles shader/CPU solve. |
| `ProfilerOverlay` | `src/Debug` | F3 readout: fps, CPU/GPU frame time, VRAM. |
| `ScreenshotCapture` | `src/Debug` | `-- --screenshot=file.png` saves a frame and quits. For verification. |
| `Orbiter`, `Oscillator` | `src/Demo` | Deterministic kinematic test motions. |
| `SpeedLimiter` | `src/Physics` | Keeps a RigidBody3D's fastest point below c (linear speed + spin at bounding radius). |
| `FreeCamera` | `src/Player` | WASD/QE/Shift fly camera, mouse locked by default (Esc frees, click re-locks). Lives in the present. |
| `SpeedLadder` | `src/Core` | Engine-free list of selectable values for c (1 cm/s up to the real speed of light) plus formatting. |
| `LightSpeedHud` | `src/Ui` | Shows c, its fraction of the real c, visible range, and a progress bar. Left click = faster, right click = slower. |
| `DebugView` | `src/Debug` | M3: ghosts, light cone, readouts, toggles. |

The physics body and the visual are separate, so the real simulation never knows the visuals are delayed.

**History texture layout:** sample time is implicit (newest = now, each older sample is one tick earlier). All objects share one write head and count. Per-object existence windows arrive in M2.

**Tick order** (physics priorities): `SimClock` −1000, movers 0, `LightHistory` 1000, `HistoryTexture` 2000.

## Core algorithm (per vertex)

1. World position of the vertex at candidate time: `x(t_s) = T(t_s) · local_vertex`.
2. `f(t_s) = |x_cam − x(t_s)| − c·(t_now − t_s)`.
3. `f` is monotonic when the object's speed is below `c`, so binary-search the ring buffer (about 8–14 steps) for the adjacent samples where `f` changes sign.
4. Interpolate position (lerp) and rotation (nlerp) between the two samples, then do one secant refinement.

**Edge cases**
- Light from the oldest sample hasn't arrived: hide the object (this is the existence window).
- Closer than one tick of light travel: use the newest sample.
- History length: `max_view_distance / c` seconds. Example: 200 m at 20 m/s = 10 s = 600 samples. Currently a fixed 600.

## Milestones

### M0: Project setup — done
Project, 60 Hz tick, `SimClock` autoload, basic scene.
**Done when:** tick counter and `t` print correctly and steadily. ✔

### M1: Single object end to end — done
Orbiting cube, `LightHistory`, texture upload, CPU solve then shader solve, ghost at the true position, `c` = 5 m/s.
**Done when:** the visible cube trails the ghost by exactly distance / c, and a distance-zero object shows no trail. ✔ (logged delay equals dist/c; solver unit tests; shader and CPU screenshots match)

### M2: Many objects, and a moving player — done
- Several objects with different motions: kinematic orbit and oscillators, plus real `RigidBody3D` balls (Jolt) bouncing on a collidable ground. Visual proxies are separate top-level nodes.
- Free-moving player camera (WASD, QE, Shift, mouse locked by default (Esc frees, click re-locks)) in the present.
- One history row per object, indexed by object ID.
- Speed clamp below `c` for rigid bodies (`SpeedLimiter`); kinematic motions only warn at startup.
- Objects stay hidden until their first light arrives (existence window), and `CustomAabb` is huge so nothing is culled at screen edges.
- Smooth visuals between ticks via `lc_age` (the solver takes the time since the newest sample).

**Done when:** objects move correctly while the camera moves, with no popping at screen edges and no jitter.
**Verified:** solver tests (including continuity across a tick boundary), shader/CPU screenshots from several camera positions, rigid-body speed clamp measured (peak 4.65 vs limit 4.5 at c = 5).
**Not verified:** interactive camera feel and motion smoothness by eye (checked by screenshots only).
**Moved to M4:** per-object sample counts (all objects currently share one write head and count, which is fine while everything exists from tick 0).

### M3: Debug view
- Toggleable ghosts.
- Light-cone visualization (expanding shell or ring at `c·Δt` around the camera).
- Readout of `c`, delay per object, buffer fill.
- Hotkeys to change `c` live.

**Done when:** M1 and M2 correctness can be checked visually at any time.

### M4: Robustness and polish
- Objects spawning and despawning.
- History length derived from max view distance.
- Precision: float32 time grows, so store time relative to a base offset.
- Profile with many objects (target: hundreds of rigid objects at 60 fps) using the profiler overlay.

**Done when:** a stress scene runs stably.

### Later
- **M5:** player's own body. Probably unnecessary because the delay is near zero. Decide the visibility rule for body parts and mirrors.
- **M6:** normals (partly done: rotated in the shader).
- **M7:** lighting and shadows. Choose between present-time lighting, delayed light sources, or a custom approach.
- **M8:** aberration and Lorentz contraction, including the camera's relativistic frame transform.
- **Optional:** per-vertex history for deforming and skinned meshes (store bone transforms, not raw vertices).

## Risks and open points

- **Low c needs long history:** the buffer holds 10 s, so objects farther than `c × 10 s` are hidden (at 1 m/s you only see things within 10 m; at 1 cm/s nothing in the arena). The HUD shows this range. Fixing it needs a longer buffer, which makes the upload cost below worse, so do both in M4.
- **Superluminal motion at low c:** lowering c below an object's speed breaks the monotonic solve (artifacts). Rigid bodies are re-clamped live, kinematic demos are not.
- **Texture upload cost:** the whole history texture is uploaded every tick (about 1.2 MB) and appears to dominate physics-step time in the profiler. Mitigation: upload only the newest column, or use a compute shader.
- **Large triangles:** per-vertex solves distort big flat meshes. Mitigation: subdivide, or solve per fragment.
- **Tick-rate jitter:** if physics runs several steps in one frame, the visual solve still uses the latest tick time, so the clock stays the single source of truth.
- **Superluminal motion:** the clamp prevents it, but teleporting objects need a "history break" marker so they don't interpolate across the jump.
- **Shadow pass:** the shader reads the camera position, so visuals cast no shadows until M7.

## Testing and verification

- **Unit tests:** `dotnet test tests/Lightspeed.Tests` for the solver (stationary, receding, orbit vs bisection, hidden window, rotation).
- **Smoke test:** headless run for load errors (see `CLAUDE.md`).
- **Visual check:** screenshot with `-- --screenshot=`, `-- --cpu` to compare against the shader, and the M3 debug view.
- **Not yet done:** a numeric GPU-vs-CPU comparison (needs shader output readback).
