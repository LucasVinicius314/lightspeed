using Godot;
using Lightspeed.Core;
using Lightspeed.Player;

namespace Lightspeed.Net;

/// <summary>
/// Another player, seen in a session. A remote player is just another moving thing in this world, so it is made
/// of delayed parts like everything else (see <see cref="AvatarRig"/>): what you see of them is where they were when the
/// light now reaching you left them, on top of the network latency. Their poses come from the network instead of a
/// mover script; the avatar eases toward the newest one each tick. It looks like their current mode: a person
/// walking or flying, or an FPV drone.
/// </summary>
public partial class RemoteAvatar : AvatarRig
{
    public int PeerId { get; }

    private Vector3 _position;
    private Quaternion _rotation = Quaternion.Identity;
    private Vector3 _targetPosition;
    private Quaternion _targetRotation = Quaternion.Identity;
    private CameraMode _mode;
    private bool _hasTarget;

    public RemoteAvatar(int peerId, Color tint) : base($"Remote_{peerId}", tint)
    {
        PeerId = peerId;
    }

    /// <summary>The latest pose received for this player.</summary>
    public Vector3 Position3D => _position;

    public void SetState(PoseState state)
    {
        _targetPosition = state.Position;
        _targetRotation = state.Rotation;
        _mode = System.Enum.IsDefined(typeof(CameraMode), (int)state.Mode) ? (CameraMode)state.Mode : CameraMode.Fly;
        if (_hasTarget)
            return;
        _hasTarget = true; // the first pose is taken as is, so the avatar does not fly in from the origin
        _position = state.Position;
        _rotation = state.Rotation;
    }

    public override void _PhysicsProcess(double delta)
    {
        // Runs before LightHistory records the tick (priority 1000), so the history gets the pose of this tick.
        if (!_hasTarget)
            return;
        _position = _position.Lerp(_targetPosition, 0.5f);
        _rotation = _rotation.Slerp(_targetRotation, 0.5f).Normalized();
        Place(_position, _rotation, _mode);
    }
}
