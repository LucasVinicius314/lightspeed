# Lightspeed

Godot 4.7.2 (C#, Forward+, Jolt) simulation where physics runs in the present but the *visuals* are delayed by the finite speed of light. See [plan.md](plan.md) for goals, architecture, milestones and status.

## Layout

- `src/Core` sim clock and main scene script. `src/History` recording, texture packing, retarded solver. `src/Visuals` delayed visuals. `src/Physics` speed limiter. `src/Player` camera. `src/Ui` HUD. `src/Debug` overlays and dev tools. `src/Demo` test motions. Namespaces mirror folders (`Lightspeed.History`, ...).
- `shaders/`, `scenes/` (`scenes/objects/` for reusable objects), `assets/`.
- `tests/Lightspeed.Tests` xUnit project. It *links* `src/History/RetardedSolver.cs` and `src/Core/SpeedLadder.cs` instead of referencing the Godot project, so those must stay free of Godot types (System.Numerics / BCL only).

## Commands

`<godot>` below is the Godot 4.7.2 mono *console* executable (its location is machine-specific; see the user's global notes).

- Build: `dotnet build Lightspeed.csproj`
- Tests: `dotnet test tests/Lightspeed.Tests`
- Import / load-error check: `<godot> --headless --path . --import`
- Headless smoke test: `<godot> --headless --path . --quit-after 1000` (headless runs frames fast; use a high count to see ticks)
- Visual check (needs a window): `<godot> --path . -- --screenshot=<file.png>`; add `--cpu` to use the CPU solve instead of the shader, `--c=<m/s>` to start at a given speed of light, `--cam=x,y,z,yawDeg,pitchDeg` to place the camera.
- In-game keys: F1 toggles shader/CPU solve, F3 toggles the profiler, left/right click steps c faster/slower, WASD/QE/Shift fly; the mouse is locked, Esc frees it and the next click re-locks.

## Rules and gotchas

- **Time:** only `SimClock` owns time (fixed 60 Hz tick). Never use wall-clock time or `delta` for simulation state.
- **Tick order** relies on `ProcessPhysicsPriority`: SimClock −1000, movers 0, LightHistory 1000, HistoryTexture 2000. Keep new movers at the default.
- **Solver and shader are twins.** `RetardedSolver.cs` and `shaders/delayed_visual.gdshader` implement the same algorithm; change both together and keep the unit tests passing.
- `SimClock.C` changes live (steps in `SpeedLadder`); read it each time, never cache it. Every object speed must stay below `SimClock.C`, or the retarded-time solve is not monotonic.
- The Godot csproj has no implicit usings (add `using System;`). `Compile Remove="tests/**"` keeps test files out of the game assembly.
- In `.tscn` files, an `[Export]` that references a node needs `node_paths=PackedStringArray("Prop")` on that node's header, or it loads as null.
- Shader globals (`lc_*`) are registered at runtime by `HistoryTexture`, so the editor may flag the shader until the game runs.
- Visuals are top-level, cast no shadows (until M7), and rely on a huge `CustomAabb`.
- Don't commit `.godot/`, `bin/`, `obj/` (already in `.gitignore`).
