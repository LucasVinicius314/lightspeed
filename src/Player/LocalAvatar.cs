using Godot;

namespace Lightspeed.Player;

/// <summary>
/// The player's own model, so you are not a floating camera: your body, or your drone, drawn through the same light
/// delay as everything else. It follows the player's eye pose (not the third-person camera). Parts closer than a few
/// centimetres to the camera are not drawn, so you never see the inside of your own head.
/// </summary>
public partial class LocalAvatar : AvatarRig
{
    /// <summary>How close to the camera a part of your own model may be drawn, in metres.</summary>
    public const float MinDistance = 0.15f;

    private readonly FreeCamera _camera;

    public LocalAvatar(FreeCamera camera) : base("LocalAvatar", new Color(1f, 0.6f, 0.2f))
    {
        _camera = camera;
    }

    public override void _PhysicsProcess(double delta)
    {
        // Before LightHistory records the tick (priority 1000), so the history gets this tick's pose. Frozen with the world.
        Place(_camera.PlayerPosition, _camera.PlayerRotation, _camera.Mode);
    }
}
