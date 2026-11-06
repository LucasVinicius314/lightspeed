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
/// </summary>
public partial class DelayedVisual : MeshInstance3D
{
    [Export] public LightHistory? History { get; set; }
    [Export] public Color Albedo { get; set; } = Colors.White;
    [Export] public SolveMode Mode { get; set; } = SolveMode.Shader;
    [Export] public bool LogDelay { get; set; }

    private ShaderMaterial _material = null!;

    public override void _Ready()
    {
        TopLevel = true;
        CastShadow = ShadowCastingSetting.Off; // shadows are M7; the shader reads the camera position
        CustomAabb = new Aabb(new Vector3(-500, -500, -500), new Vector3(1000, 1000, 1000));

        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/delayed_visual.gdshader") };
        _material.SetShaderParameter("object_index", History!.ObjectIndex);
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

    private void ApplyMode()
    {
        _material.SetShaderParameter("cpu_mode", Mode == SolveMode.Cpu);
        if (Mode == SolveMode.Shader)
        {
            GlobalTransform = Transform3D.Identity; // custom AABB is in this node's space
            Visible = true;
        }
    }

    public override void _Process(double delta)
    {
        if (Mode != SolveMode.Cpu)
            return;

        var camera = GetViewport().GetCamera3D();
        if (camera == null)
            return;

        var camPos = camera.GlobalPosition;
        var pose = RetardedSolver.Solve(History!, SimClock.TickDelta, camPos.ToNumerics(), SimClock.Instance.C, System.Numerics.Vector3.Zero, HistoryTexture.Age);
        Visible = pose.Status == SolveStatus.Ok;
        if (!Visible)
            return;

        var pos = pose.Position.ToGodot();
        GlobalTransform = new Transform3D(new Basis(pose.Rotation.ToGodot()), pos);

        if (LogDelay && SimClock.Instance.Tick % SimClock.TickRate == 0)
            GD.Print($"delay={pose.Delay:F4}s  dist/c={camPos.DistanceTo(pos) / SimClock.Instance.C:F4}s");
    }
}
