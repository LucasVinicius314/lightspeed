using Godot;
using Lightspeed.Core;
using Lightspeed.History;

namespace Lightspeed.Demo;

/// <summary>
/// Jumps between fixed positions every <see cref="Interval"/> sim seconds. Each jump marks a history
/// break so the visual is not interpolated across it: you see the old place until its last light has
/// passed, then nothing, then the new place once its first light arrives.
/// </summary>
public partial class Teleporter : Node3D
{
    [Export] public Vector3[] Positions { get; set; } = { Vector3.Zero };
    [Export] public double Interval { get; set; } = 3;

    private long _lastIndex = -1;

    public override void _PhysicsProcess(double delta)
    {
        long index = (long)(SimClock.Instance.Time / Interval) % Positions.Length;
        if (index == _lastIndex)
            return;

        Position = Positions[index];
        if (_lastIndex >= 0)
            GetNode<LightHistory>("LightHistory").BreakNext();
        _lastIndex = index;
    }
}
