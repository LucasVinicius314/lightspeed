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

    [Signal] public delegate void TickedEventHandler(long tick, double time);
    [Signal] public delegate void CChangedEventHandler(float c);

    public override void _EnterTree()
    {
        Instance = this;
        ProcessPhysicsPriority = -1000; // tick first, before anything moves or records

        float initial = InitialC;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--c="))
                initial = float.Parse(arg["--c=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--start-tick="))
                Tick = long.Parse(arg["--start-tick=".Length..], CultureInfo.InvariantCulture);
        }
        StepIndex = SpeedLadder.Nearest(initial);
    }

    public override void _PhysicsProcess(double delta)
    {
        Tick++;
        EmitSignal(SignalName.Ticked, Tick, Time);
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
