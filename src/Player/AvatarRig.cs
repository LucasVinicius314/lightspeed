using System.Collections.Generic;
using Godot;
using Lightspeed.Core;
using Lightspeed.History;
using Lightspeed.Visuals;

namespace Lightspeed.Player;

/// <summary>
/// The visible model of a player: a humanoid (head, visor, body and legs) for Fly and Walk, or an FPV quadcopter with
/// four spinning propellers for Drone. It is made of delayed parts like every other moving thing (a
/// <see cref="LightHistory"/> and a <see cref="DelayedVisual"/> each), so what you see of a player, yourself included, is
/// where they were when the light now reaching you left them. Poses come from the owner (the network for another player,
/// the camera for the local one); the model is rebuilt when the mode changes.
/// </summary>
public partial class AvatarRig : Node3D
{
    private const float DroneScale = 1.6f; // a real 5 inch quad is 0.25 m across: too small to read from a few metres
    private const float PropSpin = 50f;    // rad/s; faster than a tick could show without strobing

    private const float HeadHideFactor = 3f; // the head is hidden within this many times MinViewDistance of the camera

    private static readonly Color Dark = new(0.10f, 0.10f, 0.12f);

    /// <summary>The player's colour.</summary>
    public Color Tint { get; }

    /// <summary>Parts closer than this to the camera are not drawn, so the player never sees the inside of their own model.</summary>
    public float MinViewDistance { get; init; }

    /// <summary>Make a transparent ghost at the true position for each part (the debug view); the local avatar has none.</summary>
    public bool WithGhosts { get; init; } = true;

    private sealed class ModelPart
    {
        public required Node3D Node;
        public required Transform3D Local;  // relative to the model's frame
        public float SpinSign;              // 0 for a fixed part; +1 or -1 for a propeller (about local Y)
        public bool OnHead;                 // humanoid only: placed relative to the head (full camera rotation), not the facing
    }

    private readonly List<ModelPart> _parts = new();
    private CameraMode _model = (CameraMode)(-1);

    public AvatarRig(string name, Color tint)
    {
        Name = name;
        Tint = tint;
    }

    /// <summary>Sets where the player's camera is and which model to show, and moves the parts there. Call it once per tick.</summary>
    protected void Place(Vector3 cameraPosition, Quaternion cameraRotation, CameraMode mode)
    {
        if (mode != _model)
            Rebuild(mode);

        Transform3D frame;
        if (mode == CameraMode.Drone)
        {
            // The camera is tilted up on the drone; the model's frame is the drone's own attitude, with its camera at the front.
            var attitude = new Basis(cameraRotation) * new Basis(Vector3.Right, -FreeCamera.CameraTilt);
            frame = new Transform3D(attitude, cameraPosition - attitude * new Vector3(0, 0.012f, -0.085f) * DroneScale);
        }
        else
        {
            // Upright, turned the way the player faces. The head follows the camera's full rotation.
            float yaw = new Basis(cameraRotation).GetEuler().Y;
            frame = new Transform3D(Basis.FromEuler(new Vector3(0, yaw, 0)), cameraPosition);
        }

        var head = new Transform3D(new Basis(cameraRotation), cameraPosition); // with pitch and roll, unlike the body
        double spin = (SimClock.Instance.WorldTime * PropSpin) % Mathf.Tau;
        foreach (var part in _parts)
        {
            var local = part.Local;
            if (part.SpinSign != 0f)
                local *= new Transform3D(new Basis(Vector3.Up, (float)spin * part.SpinSign), Vector3.Zero);
            part.Node.GlobalTransform = (part.OnHead ? head : frame) * local;
        }
    }

    private void Rebuild(CameraMode mode)
    {
        Clear();
        _model = mode;
        if (mode == CameraMode.Drone)
            BuildDrone();
        else
            BuildHumanoid();
    }

    // ---- models ----

    private void BuildHumanoid()
    {
        // The head and visor follow the camera's full rotation; the body and legs only the way the player faces.
        // The camera sits inside the head, so it has a wider no-draw radius than the body below it (which you should see).
        Add("Head", new BoxMesh { Size = new Vector3(0.34f, 0.26f, 0.3f) }, Tint, Transform3D.Identity, HeadHideFactor).OnHead = true;
        Add("Visor", new BoxMesh { Size = new Vector3(0.26f, 0.08f, 0.06f) }, new Color(0.08f, 0.09f, 0.12f), At(0, 0.02f, -0.17f), HeadHideFactor).OnHead = true;
        Add("Body", new BoxMesh { Size = new Vector3(0.42f, 0.85f, 0.26f) }, Tint.Darkened(0.25f), At(0, -0.6f, 0));
        Add("LegL", new BoxMesh { Size = new Vector3(0.17f, 0.8f, 0.2f) }, Tint.Darkened(0.45f), At(-0.11f, -1.25f, 0));
        Add("LegR", new BoxMesh { Size = new Vector3(0.17f, 0.8f, 0.2f) }, Tint.Darkened(0.45f), At(0.11f, -1.25f, 0));
    }

    private void BuildDrone()
    {
        float s = DroneScale;
        // The drone's frame: X right, Y up, -Z forward (where the camera looks).
        Add("Frame", Box(0.07f, 0.008f, 0.11f, s), Dark, At(0, 0, 0, s));
        Add("Stack", Box(0.05f, 0.016f, 0.07f, s), Tint, At(0, 0.014f, 0.01f, s));
        Add("Battery", Box(0.04f, 0.022f, 0.095f, s), new Color(0.16f, 0.16f, 0.2f), At(0, 0.036f, 0.015f, s));
        Add("Camera", Box(0.026f, 0.026f, 0.024f, s), Colors.Black, At(0, 0.004f, -0.075f, s));
        Add("Lens", new CylinderMesh { TopRadius = 0.009f * s, BottomRadius = 0.009f * s, Height = 0.012f * s },
            new Color(0.1f, 0.2f, 0.45f), new Transform3D(new Basis(Vector3.Right, Mathf.Pi / 2), new Vector3(0, 0.004f, -0.088f) * s));
        Add("ArmA", Box(0.012f, 0.006f, 0.24f, s), Dark, new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 4), new Vector3(0, -0.002f, 0) * s));
        Add("ArmB", Box(0.012f, 0.006f, 0.24f, s), Dark, new Transform3D(new Basis(Vector3.Up, -Mathf.Pi / 4), new Vector3(0, -0.002f, 0) * s));

        int i = 0;
        foreach (float x in new[] { -0.085f, 0.085f })
        foreach (float z in new[] { -0.085f, 0.085f })
        {
            Add($"Motor{i}", new CylinderMesh { TopRadius = 0.014f * s, BottomRadius = 0.014f * s, Height = 0.02f * s },
                new Color(0.45f, 0.46f, 0.5f), At(x, 0.012f, z, s));
            // Neighbouring propellers turn opposite ways, like the real thing.
            Add($"Prop{i}", Box(0.13f, 0.002f, 0.012f, s), new Color(0.85f, 0.85f, 0.88f), At(x, 0.026f, z, s)).SpinSign = x * z > 0 ? 1f : -1f;
            i++;
        }
    }

    private static BoxMesh Box(float x, float y, float z, float scale) => new() { Size = new Vector3(x, y, z) * scale };

    private static Transform3D At(float x, float y, float z, float scale = 1f) => new(Basis.Identity, new Vector3(x, y, z) * scale);

    private ModelPart Add(string name, Mesh mesh, Color albedo, Transform3D local, float hideFactor = 1f)
    {
        var part = new ModelPart { Node = MakePart(name, mesh, albedo, MinViewDistance * hideFactor), Local = local };
        _parts.Add(part);
        return part;
    }

    /// <summary>One independently moving piece: a node, its history, a ghost at the true position (optional) and the delayed mesh.</summary>
    private Node3D MakePart(string name, Mesh mesh, Color albedo, float minViewDistance)
    {
        var part = new Node3D { Name = name };
        AddChild(part);
        var history = new LightHistory { Name = "LightHistory" };
        part.AddChild(history); // its _Ready creates the track; it must exist before the visual's _Ready
        if (WithGhosts)
        {
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
        }
        part.AddChild(new DelayedVisual
        {
            Name = "DelayedVisual", Mesh = mesh, History = history, Albedo = albedo, MinViewDistance = minViewDistance,
        });
        return part;
    }

    /// <summary>Despawns every part through its history, so it stays visible until its last light has passed.</summary>
    private void Clear()
    {
        foreach (var part in _parts)
        {
            part.Node.GetNode<LightHistory>("LightHistory").Despawn();
        }
        _parts.Clear();
    }

    /// <summary>Removes the player's model. Each part stays visible until the last light from it has passed.</summary>
    public void Retire()
    {
        Clear();
        QueueFree();
    }
}
