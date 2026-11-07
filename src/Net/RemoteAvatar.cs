using Godot;
using Lightspeed.History;
using Lightspeed.Visuals;

namespace Lightspeed.Net;

/// <summary>
/// Another player, seen in a session. A remote player is just another moving thing in this world, so it is made
/// of delayed parts like everything else (a <see cref="LightHistory"/> and a <see cref="DelayedVisual"/> each): what
/// you see of them is where they were when the light now reaching you left them, on top of the network latency.
/// Their poses come from the network instead of a mover script; the avatar eases toward the newest one each tick.
/// The parts are a head, a visor that shows where they look, and a body.
/// </summary>
public partial class RemoteAvatar : Node3D
{
    public int PeerId { get; }
    public Color Tint { get; }

    private Node3D _head = null!;
    private Node3D _visor = null!;
    private Node3D _body = null!;
    private Vector3 _position;
    private Quaternion _rotation = Quaternion.Identity;
    private Vector3 _targetPosition;
    private Quaternion _targetRotation = Quaternion.Identity;
    private bool _hasTarget;

    public RemoteAvatar(int peerId, Color tint)
    {
        PeerId = peerId;
        Tint = tint;
        Name = $"Remote_{peerId}";
    }

    /// <summary>The latest pose received for this player.</summary>
    public Vector3 Position3D => _position;

    public void SetState(PoseState state)
    {
        _targetPosition = state.Position;
        _targetRotation = state.Rotation;
        if (_hasTarget)
            return;
        _hasTarget = true; // the first pose is taken as is, so the avatar does not fly in from the origin
        _position = state.Position;
        _rotation = state.Rotation;
    }

    public override void _Ready()
    {
        _head = Part("Head", new BoxMesh { Size = new Vector3(0.34f, 0.26f, 0.3f) }, Tint);
        _visor = Part("Visor", new BoxMesh { Size = new Vector3(0.26f, 0.08f, 0.06f) }, new Color(0.08f, 0.09f, 0.12f));
        _body = Part("Body", new BoxMesh { Size = new Vector3(0.42f, 0.85f, 0.26f) }, Tint.Darkened(0.25f));
        Apply();
    }

    public override void _PhysicsProcess(double delta)
    {
        // Runs before LightHistory records the tick (priority 1000), so the history gets the pose of this tick.
        if (!_hasTarget)
            return;
        _position = _position.Lerp(_targetPosition, 0.5f);
        _rotation = _rotation.Slerp(_targetRotation, 0.5f).Normalized();
        Apply();
    }

    private void Apply()
    {
        var headTransform = new Transform3D(new Basis(_rotation), _position);
        _head.GlobalTransform = headTransform;
        _visor.GlobalTransform = headTransform * new Transform3D(Basis.Identity, new Vector3(0, 0.02f, -0.17f));
        float yaw = new Basis(_rotation).GetEuler().Y;
        _body.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(0, yaw, 0)), _position + new Vector3(0, -0.6f, 0));
    }

    /// <summary>
    /// Removes the player. Each part is despawned through its history, so the avatar stays visible until the last
    /// light from it has passed, like any other object that disappears.
    /// </summary>
    public void Retire()
    {
        foreach (var part in new[] { _head, _visor, _body })
            part.GetNode<LightHistory>("LightHistory").Despawn();
        QueueFree();
    }

    /// <summary>One independently moving piece: a node, its history, a ghost at the true position and the delayed mesh.</summary>
    private Node3D Part(string name, Mesh mesh, Color albedo)
    {
        var part = new Node3D { Name = name };
        AddChild(part);
        var history = new LightHistory { Name = "LightHistory" };
        part.AddChild(history); // its _Ready creates the track; it must exist before the visual's _Ready
        var ghost = new MeshInstance3D
        {
            Name = "Ghost",
            Mesh = mesh,
            Scale = Vector3.One * 1.03f,
            MaterialOverride = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1, 1, 1, 0.25f),
            },
        };
        ghost.AddToGroup("ghost");
        part.AddChild(ghost);
        part.AddChild(new DelayedVisual { Name = "DelayedVisual", Mesh = mesh, History = history, Albedo = albedo });
        return part;
    }
}
