using Godot;
using Lightspeed.Core;
using Lightspeed.Visuals;

namespace Lightspeed.History;

/// <summary>
/// Records the parent Node3D's pose once per tick into a <see cref="HistoryTrack"/>.
/// Runs after all movement (high priority value) so the sample matches the tick's time.
/// To remove the object, call <see cref="Despawn"/> rather than freeing the parent, so its visual
/// stays visible until the light from its last moment has passed.
/// </summary>
public partial class LightHistory : Node
{
    private bool _breakNext;

    /// <summary>Null when no history row was available.</summary>
    public HistoryTrack? Track { get; private set; }

    public override void _Ready()
    {
        ProcessPhysicsPriority = 1000;
        Track = HistoryTexture.Instance.CreateTrack();
    }

    /// <summary>
    /// Call after teleporting the parent (before this tick's recording): the next sample is not
    /// interpolated from the previous one.
    /// </summary>
    public void BreakNext() => _breakNext = true;

    public override void _PhysicsProcess(double delta)
    {
        if (Track == null || Track.IsFinished)
            return;

        var xf = GetParent<Node3D>().GlobalTransform;
        Track.Record(
            SimClock.Instance.Tick,
            new Sample(xf.Origin.ToNumerics(), xf.Basis.GetRotationQuaternion().ToNumerics()),
            _breakNext);
        _breakNext = false;
    }

    /// <summary>
    /// Removes the object: stops recording, hands its delayed visual(s) over to the scene root so they
    /// persist until the last light has passed, then frees the parent.
    /// </summary>
    public void Despawn()
    {
        Track?.Retire();
        var body = GetParent();
        foreach (var child in body.GetChildren())
            if (child is DelayedVisual visual && visual.History == this)
                visual.Detach();
        body.QueueFree();
    }

    public override void _ExitTree() => Track?.Retire();
}
