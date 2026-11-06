# Lightspeed

Godot 4.7.2 (C#, Forward+, Jolt) simulation where physics runs in the present but the *visuals* are delayed by the finite speed of light. See [plan.md](plan.md) for goals, architecture, controls, milestones and status.

## Layout

- `src/Core` sim clock, speed ladder, main scene script. `src/History` recording, texture packing, retarded solver. `src/Visuals` delayed visuals and mesh tessellation. `src/Physics` speed limiter. `src/Player` camera. `src/Ui` HUD and settings menu. `src/Debug` overlays, debug modes and dev tools. `src/Demo` test motions and spawners. Namespaces mirror folders (`Lightspeed.History`, ...).
- `shaders/`, `scenes/` (`scenes/objects/` for reusable objects: orbiting cube, oscillating sphere, bouncing ball, teleporting cube, car, helicopter, standing fan), `assets/`.
- `tests/Lightspeed.Tests` xUnit project. It *links* these engine-free files instead of referencing the Godot project, so they must stay free of Godot types (System.Numerics / BCL only): `src/History/RetardedSolver.cs`, `src/History/RetardedRay.cs`, `src/History/HistoryTrack.cs`, `src/Core/SpeedLadder.cs`, `src/Debug/LightRings.cs`, `src/Visuals/TessellationMath.cs`. A new engine-free file needs a `<Compile Include=... Link=...>` line in the test csproj.

## Commands

`<godot>` below is the Godot 4.7.2 mono *console* executable (its location is machine-specific; see the user's global notes).

- Build: `dotnet build Lightspeed.csproj`
- Tests: `dotnet test tests/Lightspeed.Tests`
- Import / load-error check: `<godot> --headless --path . --import`
- Headless smoke test: `<godot> --headless --path . --quit-after 1000` (the shader is not exercised headless; use a window for rendering checks)
- Visual check (needs a window): `<godot> --path . -- --screenshot=<file.png>`. Options after `--`:
  - `--cpu` CPU solve instead of the shader; `--c=<m/s>` starting speed of light; `--cam=x,y,z,yawDeg,pitchDeg` camera placement.
  - `--at-tick=N` tick to capture (counted from the starting tick); `--start-tick=N` start the clock late (precision / tick-wrap test).
  - `--stress=N` drop N rigid balls; `--open-menu` start with the settings menu open; `--no-echoes` / `--echoes` / `--rays` choose how fast parts are drawn; `--no-settings` ignore the saved settings file (screenshot runs already do); `--max-edge=<m>` mesh edge limit (a huge value turns tessellation off, handy for before/after shots).
- In-game keys: Tab settings menu (every option, see plan.md); F1 shader/CPU solve; F3 cycles debug text off / simple (profiler) / full (plus the debug view), saved to `user://debug_mode.cfg`; 1/2/3 toggle ghosts/rings/labels in full mode, saved to `user://debug_view.cfg`; mouse wheel up/down steps c; WASD/QE/Shift fly; the mouse is locked, Esc frees it and the next click re-locks.
- For a clean screenshot without debug clutter, write `user://debug_view.cfg` (under `%APPDATA%/Godot/app_userdata/Lightspeed/`) with `ghosts/rings/labels=false`, then delete it afterwards so the user's settings are untouched.

## Rules and gotchas

- **Time:** only `SimClock` owns time (fixed 60 Hz tick). Never use wall-clock time or `delta` for simulation state. Reduce motion phases in `double` (`t % Tau`) before narrowing to float.
- **Tick order** relies on `ProcessPhysicsPriority`: SimClock −1000, movers 0, LightHistory 1000, HistoryTexture 2000. Keep new movers at the default.
- **Solver and shader are twins.** `RetardedSolver.cs` and `shaders/delayed_visual.gdshader` implement the same algorithm; change both together and keep the unit tests passing. Texture layout constants (`Chunk`, `TickWrap`) are duplicated in the shader.
- **Debugging shaders:** `return` is not allowed in `fragment()`; paint debug colours (EMISSION) and read the PNG with PIL instead. Remove experiments before finishing.
- **Spawn/despawn** objects through `LightHistory` (`Despawn()`), not `QueueFree` on the body, so the visual outlives it; teleport with `BreakNext()`.
- **Parts:** a `DelayedVisual` ignores its own node transform, so every independently moving piece is a part (`Node3D` + `LightHistory` + ghost + `DelayedVisual`) offset through the part's transform. Compound machines (car, helicopter, fan) nest parts. Keep every part's peak speed (parent motion + spin x radius) below c.
- **Settings:** add a persisted option in `GameSettings` (default, load, save), a control in `SettingsMenu` (it refreshes from the getter, so hotkeys stay in sync), and have the consumer react to `GameSettings.Changed`. Scripted runs ignore the saved file. Controls use `FocusMode.None` so Space/WASD never trigger them.
- **Edges only warp if they have vertices:** `DelayedVisual` tessellates primitive meshes to `MaxEdgeLength`; non-primitive meshes are not tessellated (they log a warning).
- `SimClock.C` changes live (steps in `SpeedLadder`); read it each time, never cache it. Parts below c use the single-image binary search; parts at or above c are drawn as several images (`DelayedVisual.MaxImages`) via `find_image` in the shader and `RetardedSolver.SolveImage` on the CPU. Keep demos honest: a part's real speed is never reduced to hide this.
- **Fast-part rendering has three modes** (`GameSettings.FastParts`): one image (tears), Light Echoes (vertex copies, unreliable close to a part) and Exact rays (default; per pixel, primitives only, falls back to echoes otherwise). `RetardedRay.cs` and `delayed_ray.gdshader` are twins like the solver and the vertex shader; both include `lc_common.gdshaderinc`.
- **Ray intervals:** `RetardedRay.Intervals` runs on the CPU every frame per fast part and is uploaded as `iv_count` / `iv_sphere` / `iv_k` (16 slots). Its sample indices are track-relative; `DelayedVisual.UploadIntervals` shifts them to the shared texture window. The shader must only march inside them: anything outside is invisible by construction, and a test enforces that no ray hit falls outside.
- **Ray shader materials must stay `blend_mix`:** an opaque material that writes `DEPTH` goes through the depth pre-pass, which runs the march twice and drops pixels where the two runs differ by a hair (single-pixel speckles).
- **Multi-image details:** the shader is `cull_disabled` (reversed-orientation images), invalid vertices are discarded in the fragment stage through the `img_valid` varying, and `MAX_SCAN` in the shader equals `RetardedSolver.MaxScanIterations`.
- The Godot csproj has no implicit usings (add `using System;`). `Compile Remove="tests/**"` keeps test files out of the game assembly.
- In `.tscn` files, an `[Export]` that references a node needs `node_paths=PackedStringArray("Prop")` on that node's header, or it loads as null.
- If the Godot editor is open it may re-save `.tscn` and `project.godot` (adds uids, drops default values such as the 60 Hz tick); re-read a file before editing it.
- Shader globals (`lc_*`) are registered at runtime by `HistoryTexture`, so the editor may flag the shader until the game runs.
- Visuals are top-level, cast no shadows (until M7), and rely on a huge `CustomAabb`.
- Godot labels render a Windows CR+LF as a blank line: build multi-line text with `\n`, not `StringBuilder.AppendLine`.
- Profiler physics-time is a noisy single-step sample; trust the `history update` ms in the debug panel for our own cost.
- Don't commit `.godot/`, `bin/`, `obj/` (already in `.gitignore`).
