using Godot;
using Lightspeed.Core;
using Lightspeed.History;

namespace Lightspeed.Visuals;

public enum SolveMode
{
    Shader,
    /// <summary>Solves the object's centre on the CPU and moves the node. Sanity reference for the shader.</summary>
    Cpu,
}

/// <summary>
/// The visible proxy of a moving object. It shows where the object was when the light now reaching
/// the camera left it. Top-level, so the physics body's transform never affects it.
/// After <see cref="LightHistory.Despawn"/> it is moved to the scene root and frees itself once the
/// object's last light has passed.
/// </summary>
public partial class DelayedVisual : MeshInstance3D
{
    [Export] public LightHistory? History { get; set; }
    [Export] public Color Albedo { get; set; } = Colors.White;
    [Export] public SolveMode Mode { get; set; } = SolveMode.Shader;
    [Export] public bool LogDelay { get; set; }
    /// <summary>
    /// Longest edge allowed in the mesh, in metres. The solve is per vertex, so edges longer than the
    /// distortion you want to see stay straight. Smaller is smoother but costs vertices.
    /// Override with `-- --max-edge=<m>` (a huge value switches tessellation off).
    /// </summary>
    [Export] public float MaxEdgeLength { get; set; } = 0.2f;

    /// <summary>The object's history. Outlives the object's nodes.</summary>
    public HistoryTrack? Track { get; private set; }
    public string BodyName { get; private set; } = "";
    /// <summary>The true-position node. May be freed after despawn; check <c>GodotObject.IsInstanceValid</c>.</summary>
    public Node3D? Body { get; private set; }

    private ShaderMaterial _material = null!;

    public override void _Ready()
    {
        AddToGroup("delayed_visual");
        TopLevel = true;
        CastShadow = ShadowCastingSetting.Off; // shadows are M7; the shader reads the camera position
        CustomAabb = new Aabb(new Vector3(-500, -500, -500), new Vector3(1000, 1000, 1000));

        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--max-edge="))
                MaxEdgeLength = float.Parse(arg["--max-edge=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        if (Mesh != null)
            Mesh = MeshTessellation.Refine(Mesh, MaxEdgeLength);

        Track = History!.Track;
        Body = History.GetParent<Node3D>();
        BodyName = Body.Name;

        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/delayed_visual.gdshader") };
        _material.SetShaderParameter("object_index", Track?.Row ?? 0);
        _material.SetShaderParameter("albedo", Albedo);
        MaterialOverride = _material;

        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg == "--cpu")
                Mode = SolveMode.Cpu;
        ApplyMode();
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F1 })
        {
            Mode = Mode == SolveMode.Shader ? SolveMode.Cpu : SolveMode.Shader;
            ApplyMode();
        }
    }

    /// <summary>Leaves the despawning object's subtree so it can outlive it.</summary>
    public void Detach()
    {
        History = null; // the LightHistory node is about to be freed; Track keeps the data
        Reparent(GetTree().CurrentScene);
    }

    private void ApplyMode()
    {
        _material.SetShaderParameter("cpu_mode", Mode == SolveMode.Cpu);
        if (Track == null)
            Visible = false;
        else if (Mode == SolveMode.Shader)
        {
            GlobalTransform = Transform3D.Identity; // custom AABB is in this node's space
            Visible = true;
        }
    }

    public override void _Process(double delta)
    {
        if (Track == null)
            return;
        if (Track.IsExpired(SimClock.Instance.Tick))
        {
            QueueFree();
            return;
        }
        if (Mode != SolveMode.Cpu)
            return;

        var camera = GetViewport().GetCamera3D();
        if (camera == null)
            return;

        var camPos = camera.GlobalPosition;
        var pose = RetardedSolver.Solve(Track, SimClock.TickDelta, camPos.ToNumerics(), SimClock.Instance.C,
            System.Numerics.Vector3.Zero, HistoryTexture.AgeOf(Track));
        Visible = pose.Status == SolveStatus.Ok;
        if (!Visible)
            return;

        var pos = pose.Position.ToGodot();
        GlobalTransform = new Transform3D(new Basis(pose.Rotation.ToGodot()), pos);

        if (LogDelay && SimClock.Instance.Tick % SimClock.TickRate == 0)
            GD.Print($"delay={pose.Delay:F4}s  dist/c={camPos.DistanceTo(pos) / SimClock.Instance.C:F4}s");
    }
}
