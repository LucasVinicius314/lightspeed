using System.Collections.Generic;
using Godot;
using Lightspeed.Core;
using Lightspeed.History;
using Lightspeed.Ui;
using Lightspeed.Visuals;

namespace Lightspeed.Demo;

/// <summary>
/// Lets the player throw physics objects (left click). B picks the next shape, X removes everything thrown.
/// Objects are real Jolt rigid bodies with a <c>SpeedLimiter</c>; what you see of them is delayed like everything
/// else. They despawn through <see cref="LightHistory.Despawn"/> after <see cref="Lifetime"/> sim seconds, and the
/// oldest goes first when more than <see cref="MaxAlive"/> are out.
/// </summary>
public partial class Thrower : Node3D
{
    private static readonly string[] Names = { "Ball", "Box", "Cylinder" };

    /// <summary>One scene per entry of <see cref="Names"/>; each needs LightHistory and DelayedVisual children.</summary>
    [Export] public Godot.Collections.Array<PackedScene> Scenes { get; set; } = new();
    [Export] public float ThrowSpeed { get; set; } = 12f;
    [Export] public double Lifetime { get; set; } = 30;
    [Export] public int MaxAlive { get; set; } = 60;

    public int SelectedIndex { get; private set; }
    public string SelectedName => Names[SelectedIndex % Names.Length];
    public static IReadOnlyList<string> ShapeNames => Names;

    private readonly RandomNumberGenerator _rng = new() { Seed = 7 };
    private readonly List<(LightHistory History, double DieAt)> _alive = new();

    public override void _Ready()
    {
        AddToGroup("thrower");
        ProcessMode = ProcessModeEnum.Always; // keep taking throw input while paused; lifetimes wait for the clock
        foreach (var arg in OS.GetCmdlineUserArgs()) // verification: `-- --throw=N` throws N objects (cycling shapes) at start
            if (arg.StartsWith("--throw="))
            {
                int count = int.Parse(arg["--throw=".Length..], System.Globalization.CultureInfo.InvariantCulture);
                for (int i = 0; i < count; i++)
                {
                    Select(i);
                    CallDeferred(MethodName.Throw);
                }
            }
    }

    public void Select(int index) => SelectedIndex = Mathf.PosMod(index, Mathf.Max(Scenes.Count, 1));

    public override void _UnhandledInput(InputEvent @event)
    {
        if (SettingsMenu.IsOpen)
            return;
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when Input.MouseMode == Input.MouseModeEnum.Captured:
                Throw();
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.B }:
                Select(SelectedIndex + 1);
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.X }:
                Clear();
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (GetTree().Paused)
            return;
        double now = SimClock.Instance.Time;
        for (int i = _alive.Count - 1; i >= 0; i--)
        {
            if (!GodotObject.IsInstanceValid(_alive[i].History))
                _alive.RemoveAt(i);
            else if (now >= _alive[i].DieAt)
            {
                _alive[i].History.Despawn();
                _alive.RemoveAt(i);
            }
        }
    }

    public void Throw()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null || Scenes.Count == 0)
            return;

        var instance = Scenes[SelectedIndex % Scenes.Count].Instantiate<RigidBody3D>();
        var forward = -camera.GlobalBasis.Z;
        instance.Position = camera.GlobalPosition + forward * 1.2f;
        instance.Rotation = new Vector3(_rng.Randf() * Mathf.Tau, _rng.Randf() * Mathf.Tau, 0);

        // Stay well under c: at low c the limiter would clip a harder throw anyway.
        float speed = Mathf.Min(ThrowSpeed, 0.6f * SimClock.Instance.C);
        instance.LinearVelocity = forward * speed;
        instance.AngularVelocity = new Vector3(_rng.RandfRange(-3, 3), _rng.RandfRange(-3, 3), _rng.RandfRange(-3, 3));

        instance.GetNode<DelayedVisual>("DelayedVisual").Albedo = Color.FromHsv(_rng.Randf(), 0.65f, 1f);
        var history = instance.GetNode<LightHistory>("LightHistory");
        AddChild(instance);
        _alive.Add((history, SimClock.Instance.Time + Lifetime));

        if (_alive.Count > MaxAlive)
        {
            _alive[0].History.Despawn();
            _alive.RemoveAt(0);
        }
    }

    /// <summary>Despawns everything thrown; each stays visible until its last light has passed.</summary>
    public void Clear()
    {
        foreach (var (history, _) in _alive)
            if (GodotObject.IsInstanceValid(history))
                history.Despawn();
        _alive.Clear();
    }
}
