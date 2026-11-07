using System;
using System.Globalization;
using Godot;
using Lightspeed.Net;

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

    /// <summary>
    /// Added to <see cref="Time"/> to get <see cref="WorldTime"/>. Zero offline and for a host; a client sets it so
    /// that its scripted motion matches the host's. It never touches ticks or the light history.
    /// </summary>
    public double WorldOffset { get; set; }

    /// <summary>
    /// The time every scripted mover (orbiters, the car, the fan blades, ...) computes its position from. Peers in a
    /// session share it, so they all see the same objects in the same places. Ticks stay local to each machine.
    /// </summary>
    public double WorldTime => Time + WorldOffset;

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

    /// <summary>Raised when the speed of light, time scale or pause changed through the host-side path (a host broadcasts it).</summary>
    public event Action? SharedChanged;

    /// <summary>Raised when a single tick was stepped while paused (a host tells clients to step too).</summary>
    public event Action? Stepped;

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
        bool wasPaused = Paused;
        Paused = true;
        Freeze();
        if (!wasPaused && !NetSession.IsClient)
            SharedChanged?.Invoke(); // a scripted pause (--pause-at-tick) reaches clients like any other
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

    // The shared settings (speed of light, time scale, pause, stepping) belong to the host in a session. A
    // client's own input is sent to the host, which applies it and tells everyone; the Apply... methods below
    // are what actually changes the clock, on the host and, for host-sent changes, on clients.

    public void SetTimeScale(float scale)
    {
        if (NetSession.IsClient) { NetSession.Instance.RequestTimeScale(scale); return; }
        ApplyTimeScale(scale);
        SharedChanged?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        if (NetSession.IsClient) { NetSession.Instance.RequestPaused(paused); return; }
        ApplyPaused(paused);
        SharedChanged?.Invoke();
    }

    /// <summary>While paused, runs exactly one more tick (physics included) and freezes again.</summary>
    public void StepTick()
    {
        if (NetSession.IsClient) { NetSession.Instance.RequestStep(); return; }
        if (ApplyStep())
            Stepped?.Invoke();
    }

    public void StepFaster() => SetStepIndex(StepIndex + 1);
    public void StepSlower() => SetStepIndex(StepIndex - 1);

    public void SetStepIndex(int index)
    {
        index = Mathf.Clamp(index, 0, SpeedLadder.Steps.Count - 1);
        if (NetSession.IsClient) { NetSession.Instance.RequestStepIndex(index); return; }
        if (ApplyStepIndex(index))
            SharedChanged?.Invoke();
    }

    /// <summary>Client: take the host's shared settings.</summary>
    public void ApplyFromHost(int stepIndex, float timeScale, bool paused)
    {
        ApplyStepIndex(Mathf.Clamp(stepIndex, 0, SpeedLadder.Steps.Count - 1));
        ApplyTimeScale(timeScale);
        ApplyPaused(paused);
    }

    /// <summary>Client: the host stepped one tick.</summary>
    public void ApplyStepFromHost() => ApplyStep();

    private void ApplyTimeScale(float scale)
    {
        int ticksPerSecond = Mathf.Clamp(Mathf.RoundToInt(TickRate * scale), 1, (int)(TickRate * MaxTimeScale));
        TimeScale = (float)ticksPerSecond / TickRate;
        Engine.PhysicsTicksPerSecond = ticksPerSecond;
        Engine.TimeScale = TimeScale;
    }

    private void ApplyPaused(bool paused)
    {
        if (paused == Paused && _stepUntil < 0)
            return;
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

    private bool ApplyStep()
    {
        if (!Paused || _stepUntil >= 0)
            return false;
        _stepUntil = Tick + 1;
        GetTree().Paused = false;
        return true;
    }

    private bool ApplyStepIndex(int index)
    {
        if (index == StepIndex)
            return false;
        StepIndex = index;
        EmitSignal(SignalName.CChanged, C);
        return true;
    }

    private void Freeze()
    {
        FrozenAge = Engine.GetPhysicsInterpolationFraction() * TickDelta;
        GetTree().Paused = true;
    }
}
