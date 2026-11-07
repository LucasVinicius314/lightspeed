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
| Windows and Linux builds (`build.ps1` / `build.sh`) | Done |
| Multi-image rendering for parts at or above c ("Light Echoes") | Done (see notes) |
| Exact per-pixel rendering for fast primitives ("Exact rays", the default) | Done (see notes) |
| Settings menu (Tab) for every option, now tabbed with a fixed width and a Controls (keybinds) tab | Done |
| Camera modes: Fly, Drone (momentum, banking, CRT feed with on-screen display), Walk (character controller) | Done |
| Physics objects: throw balls, boxes and cylinders (left click) | Done |
| Time control: pause, single-step, time scale that scales the whole clock | Done |
| M5–M8, optional | Next: your call |

## Decisions

- **Stack:** Godot 4.7.2 (Forward+, Jolt Physics, D3D12 on Windows), **C#** (.NET, net8.0), a spatial shader. xUnit test project in `tests/`.
- **Clock:** fixed 60 Hz. `t = tick × (1/60)` (16.67 ms), advanced in `_PhysicsProcess`, never from wall-clock time. The project relies on Godot's default of 60 physics ticks per second (the editor strips it from `project.godot` because it is the default), and `SimClock.TickRate` is a constant 60.
- **Physics:** normal Godot physics, instantaneous. Nothing is delayed except the visuals.
- **Speed of light:** `c` defaults to 5 m/s so the effect is obvious, and the player steps it live through a ladder of 31 values from 1 cm/s up to the real 299,792,458 m/s.
- **No Doppler shift.**
- **Camera:** three modes in the present (V cycles them): Fly (free), Drone (FPV acro: attitude, thrust, gravity, drag, CRT feed) and Walk (a `CharacterBody3D` with gravity, jumping, collisions). FOV 85°, FXAA on. The camera is not clamped below `c` by default (at low `c` the player could barely move).
- **Time control:** `SimClock` owns pause, step and time scale. A scale of `s` runs `60 s` ticks per real second and scales the engine delta by `s`, so each tick is still exactly 1/60 s (`Engine.TimeScale` alone would only slow the physics bodies and leave the clock and the light history at full rate). Pause is the tree pause: camera, UI and visuals have ProcessMode Always, tick-driven nodes check `GetTree().Paused`. While paused the visuals hold the sub-tick age captured at the pause.
- **Deferred:** player's own body, lighting and shadows, aberration and Lorentz contraction. (Normals are already rotated in the shader.)

## Controls and saved settings

| Input | Effect |
|---|---|
| Mouse wheel up / down, Up / Down arrows, D-pad up / down | Step the speed of light faster / slower |
| WASD, Space / C, Shift | Fly: move, up / down, sprint. Walk: move, Space jumps, sprint |
| Drone (FPV) | W / S throttle up / down, instant (no key: zero throttle, like a Battlefield helicopter), A / D yaw, mouse Y pitch (forward dives; invertible), mouse X roll. Controller: left stick up throttle (released is zero, linear, about a fifth holds height) and left stick X yaw, right stick pitch and roll |
| V | Cycle camera mode: Fly, Drone, Walk |
| P, `.`, `[` and `]`, D-pad left / right | Pause / resume, step one tick while paused, slower / faster time (the D-pad only changes the time scale) |
| Left click, B, X | Throw the selected physics object, next object (ball, box, cylinder), remove thrown objects |
| H | Open the settings menu on the Controls tab, which lists every keybind (the table is `Keybinds.cs`) |
| Mouse | Look (locked at start; Esc frees it, the next left click re-locks without stepping `c`) |
| Tab | Open / close the settings menu (Esc also closes it). Opening it frees the mouse and stops the camera |
| F1 | Toggle the shader solve and the CPU reference solve |
| F3 | Cycle debug text: off, simple (profiler), full (profiler plus debug view). Saved to `user://debug_mode.cfg`; the first run starts in full |
| 1 / 2 / 3 | In full mode: ghosts, delay rings, delay labels and lines. Saved to `user://debug_view.cfg` |

**Settings menu** (Tab) has one tab per section, in a fixed-width panel (long values are clipped, never widen it): *Light* (speed of light, fast parts: One image / Light Echoes / Exact rays, max echoes per part, mesh edge length, CPU reference solve), *Simulation* (pause, step, time scale, timed ball spawning, spawn interval, ball lifetime, spawn 25 balls, remove spawned balls, and the thrown object, throw speed and remove thrown objects), *Camera* (mode, invert drone pitch, drone CRT effect, field of view, mouse sensitivity, move speed, keep camera below c), *Controls* (every keybind), *Debug* (debug text mode, ghosts, rings, labels), *Display* (FXAA, VSync, fullscreen), and *Reset everything*. Hover a control for a tooltip. Most settings are saved to `user://settings.cfg`; the debug toggles keep their own files; the speed of light, time scale, pause and camera mode are not saved. Scripted runs (`--screenshot`, `--no-settings`) ignore the saved file.

**Command-line options** (after `--`): `--cpu`, `--c=<m/s>`, `--screenshot=<file.png>`, `--cam=x,y,z,yawDeg,pitchDeg`, `--at-tick=N`, `--stress=N`, `--start-tick=N`, `--max-edge=<m>`, `--no-echoes`, `--echoes`, `--rays`, `--open-menu`, `--menu-tab=<name>`, `--no-settings`, `--mode=fly|drone|walk`, `--throw=N` (throw N objects at start), `--pause-at-tick=N` (freeze the clock at tick N).

## Architecture

| Piece | Where | Role |
|---|---|---|
| `SimClock` (autoload) | `src/Core` | Owns tick and `t`. Emits `Ticked` each tick. Holds the current `C`, stepped through `SpeedLadder`; emits `CChanged`. |
| `SpeedLadder` | `src/Core` | Engine-free list of selectable values for c plus speed/distance formatting. |
| `LightHistory` | `src/History` | Component on each moving part. Records `(position, rotation)` each tick into its `HistoryTrack`. Spawn and despawn through it. |
| `HistoryTrack` | `src/History` | One part's ring buffer, lifetime (birth/death tick), teleport breaks, texture row and its recent peak linear/angular speed (used to bound the image scan). Engine-free, unit-tested. |
| `HistoryTexture` (autoload) | `src/History` | Packs all tracks into a Texture2DArray (one layer per 16 time slots, one row per object) plus a metadata texture. Uploads one layer per tick. Exposes `lc_history`, `lc_meta`, `lc_head`, `lc_count`, `lc_tick`, `lc_c`, `lc_age` as shader globals. `lc_age` is the time since the newest sample, so retarded time moves smoothly between ticks. |
| `RetardedRay` | `src/History` | Engine-free per-pixel trace and exact distance functions for box, sphere and cylinder. Unit-tested. `delayed_ray.gdshader` mirrors it. |
| `RetardedSolver` | `src/History` | Engine-free C# reference solve (System.Numerics): `Solve` (one image, binary search) and `SolveImage` (the k-th of several images, speed-bounded scan). Unit-tested. The shader mirrors both. |
| `delayed_ray.gdshader`, `lc_common.gdshaderinc` | `shaders/` | Per-pixel renderer on a full-screen quad per fast part; the include holds the history lookup shared with the vertex shader. |
| `delayed_visual.gdshader` | `shaders/` | `vertex()` finds each vertex's retarded transform: binary search when the part is slower than c, `find_image` (speed-bounded backward scan) when it is not. Vertices with no such image are discarded in the fragment stage. |
| `DelayedVisual`, `MeshTessellation`, `TessellationMath` | `src/Visuals` | The visible proxy mesh. Top-level, uses the shader, large `CustomAabb`. Tessellates a copy of primitive meshes so no edge exceeds `MaxEdgeLength` (default 0.2 m) and edges can warp. Draws fast parts (at or above c) in the chosen mode: Light Echoes (extra mesh draws, up to `MaxImages`) or Exact rays (a full-screen quad shaded per pixel). |
| `SpeedLimiter` | `src/Physics` | Keeps a RigidBody3D's fastest point below c (linear speed plus spin at its bounding radius). |
| `FreeCamera` | `src/Player` | The player, in the present, with three modes: Fly, Drone (full 3D attitude, body rates from the sticks, thrust along its up axis at 5x weight, gravity, linear and quadratic drag, camera tilted up 25 degrees, floor at 0.15 m, no self-levelling) and Walk (`CharacterBody3D`, capsule, gravity, jump, pushes rigid bodies, camera interpolated between ticks). Uses wall-clock frame time, so it keeps moving when time is slowed or paused (Walk is tick-driven and stops with the world). Stops while the menu is open. |
| `GameSettings` | `src/Core` | Static, persisted settings (Light Echoes, max echoes, mesh edge length, CPU solve, FOV, mouse sensitivity, fly speed, camera limit, FXAA, VSync, fullscreen) with a `Changed` event. Consumers (`DelayedVisual`, `SettingsMenu`) react to it. |
| `SettingsMenu` | `src/Ui` | The Tab menu. Every control reads and writes the same state the hotkeys use and refreshes while open; also owns the F1 key. |
| `LightSpeedHud` | `src/Ui` | Bottom centre: c, its fraction of the real c, visible range, progress bar over the ladder, key hints and the current F3 mode, plus a top chip with the camera mode, pause / time scale and the selected throwable. Up / Down arrows also step c. |
| `DebugMode` | `src/Debug` | Static F3 state (`None`, `Simple`, `Both`), persisted, with a `Changed` event. |
| `ProfilerOverlay` | `src/Debug` | Top-left readout in simple and full modes: fps, CPU and GPU frame time, VRAM, RAM. |
| `DebugView`, `LightRings` | `src/Debug` | Full mode only: true-position ghosts, delay rings around the camera, per-object delay labels and ghost-to-visual lines, plus a top-right panel (c, history fill, history update cost, per-object delay and distance). `LightRings` (engine-free) plans the ring delays. |
| `ScreenshotCapture` | `src/Debug` | `-- --screenshot=file.png` saves a frame and quits. For verification. |
| `Orbiter`, `Oscillator` | `src/Demo` | Deterministic kinematic test motions. |
| `CircleMover`, `Spinner` | `src/Demo` | Circular driving/flying path and a local-axis spinner. They build the car (spinning wheels), helicopter (main and tail rotors) and standing fan scenes. |
| `Spawner`, `Teleporter` | `src/Demo` | Spawn/despawn and teleport demos; `Spawner` also drives `--stress=N`. |
| `Thrower` | `src/Demo` | Left click throws the selected rigid body (ball, box, cylinder scenes) along the view, capped at 0.6 c; despawns through `LightHistory`, oldest first past 60. |
| `DroneView`, `crt.gdshader` | `src/Ui`, `shaders/` | Drone mode only: horizon, crosshair, altitude, speed, flight time, REC drawn on layer 91 under a full-screen CRT shader on layer 95 (curvature, scanlines, phosphor mask, aberration, noise, interference band, vignette). The HUD sits under it too; the menu is above. |
| `Keybinds`, `UiTheme` | `src/Ui` | The table of every binding (drives the Controls tab) and the shared dark-glass theme. |

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
5. A part moving at or above `c` is not monotonic: `f` can cross zero several times, and each crossing is a separate *image* of the vertex seen at once. `DelayedVisual` bounds the part's speed from its recorded motion; at or above `c` it draws the mesh once per image (about `1 + 0.8 × speed/c`, capped), and the shader scans back from the newest sample for that draw's k-th crossing, skipping stretches where `|f| / (speed + c)` proves no crossing is possible, then refines it as above.

**Edge cases**
- Light from the first sample hasn't arrived: hidden (the existence window).
- Closer than one tick of light travel: use the newest sample.
- Despawned object whose last light has passed: hidden. Until then it stays visible.
- Teleport: if the crossing lands on a history break, hidden (the gap between old and new place).
- Image k of a vertex that does not exist: the vertex is parked at the newest pose and its triangles are discarded.
- History length: the player's setting (`GameSettings.HistorySeconds`, 5 to 120 s, default 30 s; 300 to 8192 samples), changeable live. The texture and every object's ring are re-packed by sample age, so the recent past survives a resize. About 2 MB per second of history (video plus system memory).

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
- **History length is a setting** (see above). Lowering c shrinks the visible range (c times the history); the HUD shows it.
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

### Multi-image rendering ("Light Echoes") — done
Why: a point moving faster than light is seen at several moments at once, and drawing only one of them turned a fast fan into a ragged or comb-like mess at low `c` (finer tessellation did not change that; verified at 0.008 m edges). Parts keep their real speed; the renderer now shows every image. The mode is called *Light Echoes*. It is one of three choices in the settings menu for how fast parts are drawn (see Exact rays below; "One image" is the old tearing behaviour).
- Each `HistoryTrack` keeps its recent peak linear and angular speed; `DelayedVisual` turns that into a vertex speed bound using the mesh's farthest-vertex radius.
- Parts below `c` stay on the single-image path (all the rigid balls, which `SpeedLimiter` holds at 0.9c). Parts at or above `c` get extra mesh draws (extra `MeshInstance3D` children with their own `image_index`).
- Images alternate between normal and reversed orientation, so the shader uses `cull_disabled` and relies on Godot flipping back-face normals.
**Verified:** unit tests compare `SolveImage` against brute-force roots of an analytic circling path (several images at the right places), a speed-bounded scan against a every-sample scan, teleport gaps (no image), and the peak-speed tracking (54 tests). Renders at c = 2, 1, 0.5 and 0.2 m/s: a clean swept spiral from the front at 0.5, separate fanned blades from an angle, and a smear at 0.2. The default scene and the 400-ball stress scene still use one draw per object (GPU 1.4 ms, was 0.9 ms before the extra shader work).
**Known limits (Light Echoes):** vertices are linked by "k-th most recent image", which is not a stable label, so the mode breaks down close to a part (one vertex can have a single far image while its neighbours have three, stretching triangles between them and cutting out the near images). At most `MaxImages` (8) images, so at very low `c` some images are missing; fold seams are discarded per triangle, which can leave slivers at some angles; the CPU solve mode and the debug panel show only image 0; the shader path is checked by screenshots, not by a numeric GPU-vs-CPU readback.

### Exact rays — done
Why: Light Echoes link vertices across images by rank, which fails close to a part (found by following a circling car 1 m behind at c = 1 m/s: the far lap showed, the near car did not). The proper fix is to stop linking vertices.
- Each pixel traces its own light ray back from the camera: at time t_s the ray's point is c (t_now - t_s) away. The first time that point is inside the moving shape is the surface the pixel shows. Images, folds and occlusion between images fall out directly; nothing is matched between vertices.
- The march goes back through the history and skips stretches that provably cannot reach the shape: the distance function changes at most (c + the part's speed + spin at its radius) per second. A 5 mm surface tolerance ends slow approaches, and the two samples last read are cached so sub-sample steps do not fetch again.
- **Interval culling (the big saving):** a surface point within R of the part's origin can only be seen at a moment when the origin's distance to the camera is within R of c x delay (triangle inequality). So the CPU scans the origin's history (skipping safely using c + its speed) and hands the shader up to 16 short stretches of history, each with a sphere around where the part is during it (`RetardedRay.Intervals`). A pixel tests those few spheres and marches only inside the one its ray enters, so most pixels cost a handful of sphere tests. This replaced a single sphere around every place the part had ever been, which for a circling car covered most of the screen.
- Primitives only: boxes, spheres and cylinders have exact distance functions (`Shape` / `sdf`). Other meshes fall back to Light Echoes. Parts slower than light keep the single-image vertex path.
- Each fast part gets a full-screen quad shaded per pixel; its own mesh draw is suppressed. Normals come from the distance function's gradient, depth is written per pixel so parts occlude each other and the scene.
**Measured cost** (GPU ms, same scenes before / after interval culling and sample caching): overview at c = 1 m/s 2.63 / 0.19; close behind the car at c = 1 m/s 6.80 / 0.89; fan from the front at c = 0.5 m/s 5.14 / 0.72. Frames differ from the old ones in 0 to 0.2% of pixels (animation timing and silhouettes), not visibly.
**Verified:** the CPU twin is tested against brute-force marching and against the surface, and a property test checks that every ray hit over thousands of rays from three camera positions lies inside one of the intervals and its sphere (62 tests), and rays aimed at 866 known vertex-image points of a car-sized box near the camera never missed. Screenshots at c = 1 and 0.5 m/s: the near car and a clean swept fan from the front and an angle.
**A rendering bug worth remembering:** the first version showed single-pixel speckles. They disappeared with a constant depth, with no depth write, and when the material was made `blend_mix`. Cause: Godot's opaque depth pre-pass runs the fragment shader once for depth and again for colour and keeps only pixels whose depth matches exactly, and the march has hard thresholds, so a few pixels differ between the two runs. `blend_mix` + `depth_draw_always` keeps the shader out of the pre-pass and still writes depth. It also halved the GPU time.
**Known limits:** only primitive meshes; one full-screen quad per fast part, so the cost still grows with how many parts are at or above c, though most pixels now exit after a few sphere tests (about 1 ms or less in the scenes above; the 7 ms figure is gone); when more than 16 intervals are needed the last one is stretched to cover all older history, which is correct but slower; shapes are assumed centred on their origin; grazing rays on thin parts can lose a few pixels at silhouettes.

### Later
- **M5:** player's own body. Probably unnecessary because the delay is near zero. Decide the visibility rule for body parts and mirrors.
- **M6:** normals (partly done: rotated in the shader).
- **M7:** lighting and shadows. Choose between present-time lighting, delayed light sources, or a custom approach.
- **M8:** aberration and Lorentz contraction, including the camera's relativistic frame transform.
- **Optional:** per-vertex history for deforming and skinned meshes (store bone transforms, not raw vertices).

## Risks and open points

- **Low c needs long history:** the buffer is sized for the starting c, so at lower c the visible range is `c × window` (at 1 m/s you see 20 m; at 1 cm/s nothing in the arena). The HUD shows this range. The buffer is not resized when c changes.
- **Superluminal motion:** handled by multi-image rendering, within its limits (8 images, discarded fold seams). Rigid bodies are still re-clamped live by `SpeedLimiter`; kinematic demos and the camera keep their speed when c is lowered, so they are the parts that get multiple images. The startup warning only checks the starting c.
- **Texture upload cost:** solved by chunked layers (one layer per tick). A compute-shader write would avoid even that if rows ever grow much beyond 1024.
- **Large triangles:** done for primitive meshes (see tessellation). Not done for imported/ArrayMesh meshes (they get a warning); those need offline subdivision or a per-fragment solve. Cylinder caps are fan triangles and are not subdivided across their radius.
- **Tick-rate jitter:** if physics runs several steps in one frame, the visual solve still uses the latest tick time, so the clock stays the single source of truth.
- **Shadow pass:** the shader reads the camera position, so visuals cast no shadows until M7.
- **Profiler noise:** Godot's physics-time monitor is a single-step sample and jumps around between runs; use the `history update` figure in the debug panel for the cost of our own per-tick work.

## Testing and verification

- **Unit tests** (62, `dotnet test tests/Lightspeed.Tests`): solver (stationary, receding, orbit vs bisection, hidden window, rotation, partial-tick age), `HistoryTrack` (ring wrap, gap filling, teleport flag, retire and expiry, despawn visibility, teleport gap), `SpeedLadder`, `LightRings`, `TessellationMath`, multi-image solving and peak speeds.
- **Smoke test:** headless import and run for load errors (see `CLAUDE.md`).
- **Visual checks:** screenshots with `-- --screenshot=` in shader mode and with `-- --cpu`, compared at the same tick; the debug view readouts; before/after with `--max-edge=1000`.
- **Settings menu:** opening, rendering and loading a settings file are checked by screenshot and a headless run; the controls were not clicked or dragged by hand, and the saved-settings write path was not exercised end to end.
- **Not yet done:** a numeric GPU-vs-CPU comparison (needs shader output readback); judging smoothness and camera feel by eye while playing.
