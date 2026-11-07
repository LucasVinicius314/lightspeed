using System.Collections.Generic;
using System.Globalization;
using Godot;
using Lightspeed.Core;
using Lightspeed.History;
using Lightspeed.Net;
using Lightspeed.Visuals;

namespace Lightspeed.Demo;

/// <summary>
/// Spawns and despawns copies of a scene on sim time, to exercise object lifetimes: a new object appears
/// only once its light reaches you, and a despawned one stays visible until its last light has passed.
/// Run with `-- --stress=N` to instead drop N objects at once and keep them (stress test).
/// </summary>
public partial class Spawner : Node3D
{
    [Export] public PackedScene? Scene { get; set; }
    /// <summary>Sim seconds between spawns (0 = no timed spawns).</summary>
    [Export] public double Interval { get; set; } = 2;
    /// <summary>Sim seconds an object lives before it is despawned (0 = forever).</summary>
    [Export] public double Lifetime { get; set; } = 8;
    [Export] public float HalfExtent { get; set; } = 15;
    [Export] public float Height { get; set; } = 6;

    private readonly RandomNumberGenerator _rng = new() { Seed = 1 };
    private readonly List<(LightHistory History, double DieAt)> _alive = new();
    private double _nextSpawn;
    private bool _timedSpawning = true;

    /// <summary>Spawn a new object every <see cref="Interval"/> sim seconds.</summary>
    [Export]
    public bool TimedSpawning
    {
        get => _timedSpawning;
        set
        {
            _timedSpawning = value;
            if (value && IsInsideTree())
                _nextSpawn = SimClock.Instance.Time + Interval;
        }
    }

    public override void _Ready()
    {
        AddToGroup("spawner");
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--stress="))
                continue;
            int count = int.Parse(arg["--stress=".Length..], CultureInfo.InvariantCulture);
            _timedSpawning = false;
            Lifetime = 0;
            // A grid of columns so bodies start apart instead of overlapping.
            for (int i = 0; i < count; i++)
                Spawn(new Vector3(i % 10 * 2.2f - 10f, 2 + i / 100 * 2.2f, i / 10 % 10 * 2.2f - 10f));
        }
        _nextSpawn = SimClock.Instance.Time + Interval;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (NetSession.IsClient)
            return; // in a session the host spawns, and clients see its balls
        double now = SimClock.Instance.Time;

        while (_timedSpawning && Interval > 0 && now >= _nextSpawn)
        {
            Spawn(new Vector3(
                _rng.RandfRange(-HalfExtent, HalfExtent), Height, _rng.RandfRange(-HalfExtent, HalfExtent)));
            _nextSpawn += Interval;
        }

        for (int i = _alive.Count - 1; i >= 0; i--)
        {
            if (Lifetime > 0 && now >= _alive[i].DieAt)
            {
                _alive[i].History.Despawn();
                _alive.RemoveAt(i);
            }
        }
    }

    /// <summary>Drops <paramref name="count"/> objects at random places right now.</summary>
    public void SpawnBurst(int count)
    {
        if (NetSession.IsClient)
            return;
        for (int i = 0; i < count; i++)
            Spawn(new Vector3(
                _rng.RandfRange(-HalfExtent, HalfExtent), Height + _rng.RandfRange(0, 6), _rng.RandfRange(-HalfExtent, HalfExtent)));
    }

    /// <summary>Despawns everything this spawner made, so each stays visible until its last light has passed.</summary>
    public void DespawnAll()
    {
        foreach (var (history, _) in _alive)
            if (GodotObject.IsInstanceValid(history))
                history.Despawn();
        _alive.Clear();
    }

    private void Spawn(Vector3 position)
    {
        if (Scene == null)
            return;

        var instance = Scene.Instantiate<Node3D>();
        instance.Position = position;
        var history = instance.GetNode<LightHistory>("LightHistory");
        var color = Color.FromHsv(_rng.Randf(), 0.6f, 1f);
        instance.GetNode<DelayedVisual>("DelayedVisual").Albedo = color;
        AddChild(instance);
        _alive.Add((history, SimClock.Instance.Time + Lifetime));
        if (instance is RigidBody3D body)
            NetSession.Instance.OnBodySpawned(body, color);
    }
}
