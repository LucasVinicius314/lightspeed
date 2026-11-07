using System.Globalization;
using Godot;

namespace Lightspeed.Core;

/// <summary>
/// Single source of truth for simulation time. Advances once per physics tick,
/// never from wall-clock time. Also owns the (player-adjustable) speed of light.
/// </summary>
public partial class SimClock : Node
{
    public const int TickRate = 60;
    public const double TickDelta = 1.0 / TickRate;

    /// <summary>Starting speed of light in m/s; snapped to the nearest <see cref="SpeedLadder"/> step.
    /// Deliberately tiny so the effect is visible. Override with `-- --c=<m/s>`.</summary>
    [Export] public float InitialC { get; set; } = 5f;

    public static SimClock Instance { get; private set; } = null!;

    /// <summary>Starts at 0; `-- --start-tick=N` begins later to test long-running precision.</summary>
    public long Tick { get; private set; }
    public double Time => Tick * TickDelta;

    /// <summary>Index into <see cref="SpeedLadder.Steps"/>.</summary>
    public int StepIndex { get; private set; }
    public float C => SpeedLadder.Steps[StepIndex].Value;

    /// <summary>
    /// The player's time scale (1 = real time). A scale of s runs 60 s ticks per real second and scales the
    /// engine's physics delta by s, so every tick still advances exactly 1/60 s of simulation. (Scaling only
    /// the delta, as <see cref="Engine.TimeScale"/> alone does, would leave the clock and the light history
    /// ticking at full rate while the physics bodies crawl.)
    /// </summary>
    public float TimeScale { get; private set; } = 1f;

    /// <summary>
    /// True while the simulation is frozen: no ticks, no physics, no movers. The camera, UI and visuals keep
    /// working, so you can fly around a frozen moment. Implemented with the tree pause; the nodes that must keep
    /// running (camera, UI, visuals) have ProcessMode Always and the tick-driven ones check the tree state.
    /// </summary>
    public bool Paused { get; private set; }

    /// <summary>Seconds past the newest sample at the moment of the pause; the visuals hold this while frozen.</summary>
    public double FrozenAge { get; private set; }

    public const float MinTimeScale = 0.05f;
    public const float MaxTimeScale = 4f;

    private long _stepUntil = -1;
    private long _pauseAtTick = -1;
    private bool _pausePending;

    [Signal] public delegate void TickedEventHandler(long tick, double time);
    [Signal] public delegate void CChangedEventHandler(float c);

    public override void _EnterTree()
    {
        Instance = this;
        ProcessPhysicsPriority = -1000; // tick first, before anything moves or records
        ProcessMode = ProcessModeEnum.Always; // keeps handling keys and the pause itself while the tree is paused

        float initial = InitialC;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--c="))
                initial = float.Parse(arg["--c=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--start-tick="))
                Tick = long.Parse(arg["--start-tick=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--pause-at-tick=")) // verification: freeze once the clock reaches this tick
                _pauseAtTick = long.Parse(arg["--pause-at-tick=".Length..], CultureInfo.InvariantCulture);
        }
        StepIndex = SpeedLadder.Nearest(initial);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (GetTree().Paused)
            return;
        Tick++;
        EmitSignal(SignalName.Ticked, Tick, Time);

        // Freezing mid-tick would skip the nodes that run after this one, so it waits for the end of the frame.
        if (_pauseAtTick >= 0 && Tick >= _pauseAtTick)
        {
            _pauseAtTick = -1;
            _pausePending = true;
        }
        else if (_stepUntil >= 0 && Tick >= _stepUntil)
        {
            _stepUntil = -1;
            _pausePending = true;
        }
    }

    public override void _Process(double delta)
    {
        if (!_pausePending)
            return;
        _pausePending = false;
        Freeze();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Controller: D-pad left / right slow down / speed up time (up / down step c, see the HUD).
        if (@event is not InputEventJoypadButton { Pressed: true } pad)
            return;
        if (pad.ButtonIndex == JoyButton.DpadLeft)
            SetTimeScale(TimeScale / 1.25f);
        else if (pad.ButtonIndex == JoyButton.DpadRight)
            SetTimeScale(TimeScale * 1.25f);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key)
            return;
        switch (key.Keycode)
        {
            case Key.P when !key.Echo:
                SetPaused(!Paused);
                break;
            case Key.Period:
                StepTick();
                break;
            case Key.Bracketleft:
                SetTimeScale(TimeScale / 1.25f);
                break;
            case Key.Bracketright:
                SetTimeScale(TimeScale * 1.25f);
                break;
        }
    }

    public void SetTimeScale(float scale)
    {
        int ticksPerSecond = Mathf.Clamp(Mathf.RoundToInt(TickRate * scale), 1, (int)(TickRate * MaxTimeScale));
        TimeScale = (float)ticksPerSecond / TickRate;
        Engine.PhysicsTicksPerSecond = ticksPerSecond;
        Engine.TimeScale = TimeScale;
    }

    public void SetPaused(bool paused)
    {
        _stepUntil = -1;
        _pausePending = false;
        if (paused)
        {
            Paused = true;
            Freeze();
        }
        else
        {
            Paused = false;
            GetTree().Paused = false;
        }
    }

    /// <summary>While paused, runs exactly one more tick (physics included) and freezes again.</summary>
    public void StepTick()
    {
        if (!Paused || _stepUntil >= 0)
            return;
        _stepUntil = Tick + 1;
        GetTree().Paused = false;
    }

    private void Freeze()
    {
        FrozenAge = Engine.GetPhysicsInterpolationFraction() * TickDelta;
        GetTree().Paused = true;
    }

    public void StepFaster() => SetStepIndex(StepIndex + 1);
    public void StepSlower() => SetStepIndex(StepIndex - 1);

    public void SetStepIndex(int index)
    {
        index = Mathf.Clamp(index, 0, SpeedLadder.Steps.Count - 1);
        if (index == StepIndex)
            return;
        StepIndex = index;
        EmitSignal(SignalName.CChanged, C);
    }
}
