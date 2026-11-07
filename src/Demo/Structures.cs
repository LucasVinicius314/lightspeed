using System.Collections.Generic;
using Godot;
using Lightspeed.History;
using Lightspeed.Visuals;

namespace Lightspeed.Demo;

/// <summary>
/// The fixed structures of the big arena: towers around it and a parkour course to the east with steps, a beam, a moving
/// platform, a spinning bar, a lift and sweepers. Everything is built here, deterministically, so every player of a session
/// gets the same world without anything being sent.
/// Fixed pieces are plain meshes with static colliders (like the ground: nothing that never moves has anything to delay).
/// The moving ones are scripted movers like the demo objects: an <see cref="AnimatableBody3D"/> driven by world time with a
/// <see cref="LightHistory"/> and a <see cref="DelayedVisual"/>, so you see them through the light delay and still bump into
/// where they really are. Keep their peak speeds below the starting c (5 m/s); faster is allowed but then they need rays.
/// </summary>
public partial class Structures : Node3D
{
    private const float GroundTop = 0.1f;
    private static readonly Color PlatformColor = new(0.15f, 0.8f, 0.85f);
    private static readonly Color LiftColor = new(0.95f, 0.35f, 0.7f);
    private static readonly Color SpinnerColor = new(0.95f, 0.8f, 0.2f);

    private Material? _stone;

    public override void _Ready()
    {
        _stone = GD.Load<Material>("res://assets/materials/bricks075a/bricks.tres");
        BuildTowers();
        BuildParkour();
        BuildSweepers();
    }

    // ---- fixed pieces ----

    /// <summary>A solid block resting on the ground (or at any height): <paramref name="top"/> is the height of its upper face.</summary>
    private void Block(Vector3 centre, float width, float top, float depth, float bottom = GroundTop)
    {
        float height = top - bottom;
        Solid(new BoxMesh { Size = new Vector3(width, height, depth) }, new BoxShape3D { Size = new Vector3(width, height, depth) },
            new Vector3(centre.X, bottom + height / 2, centre.Z));
    }

    private void Slab(Vector3 centre, Vector3 size) =>
        Solid(new BoxMesh { Size = size }, new BoxShape3D { Size = size }, centre);

    private void Column(Vector3 baseCentre, float radius, float height)
    {
        var shape = new CylinderShape3D { Radius = radius, Height = height };
        Solid(new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = height }, shape,
            new Vector3(baseCentre.X, GroundTop + height / 2, baseCentre.Z));
    }

    private void Solid(Mesh mesh, Shape3D shape, Vector3 position)
    {
        var body = new StaticBody3D { Position = position };
        body.AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = _stone });
        body.AddChild(new CollisionShape3D { Shape = shape });
        AddChild(body);
    }

    // ---- movers ----

    /// <summary>A scripted mover that is also a delayed part: collider, history, ghost and the delayed mesh.</summary>
    private T Mover<T>(T body, Vector3 size, Color colour) where T : AnimatableBody3D
    {
        body.SyncToPhysics = false; // with it on, assigning Position from _PhysicsProcess is ignored (see CLAUDE.md)
        var mesh = new BoxMesh { Size = size };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        var history = new LightHistory { Name = "LightHistory" };
        body.AddChild(history); // its _Ready creates the track, which must exist before the visual's _Ready
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
        body.AddChild(ghost);
        body.AddChild(new DelayedVisual { Name = "DelayedVisual", Mesh = mesh, History = history, Albedo = colour });
        AddChild(body);
        return body;
    }

    private void Slider(string name, Vector3 centre, Vector3 axis, float amplitude, float omega, Vector3 size, Color colour) =>
        Mover(new Oscillator { Name = name, Center = centre, Axis = axis, Amplitude = amplitude, AngularSpeed = omega }, size, colour);

    private void Spinner(string name, Vector3 centre, float omega, Vector3 size, Color colour)
    {
        var body = Mover(new SpinningBody
        {
            Name = name, AngularSpeed = omega, BoundingRadius = Mathf.Max(size.X, size.Z) / 2,
        }, size, colour);
        body.Position = centre;
    }

    // ---- the arena ----

    /// <summary>Towers on a ring around the arena, some with a slab on top to land on, and two skybridges between neighbours.</summary>
    private void BuildTowers()
    {
        float[] heights = { 45f, 28f, 60f, 35f, 50f, 24f, 40f, 32f };
        const float ring = 75f;
        for (int i = 0; i < heights.Length; i++)
        {
            float angle = i * Mathf.Tau / heights.Length + 0.3f;
            var at = new Vector3(Mathf.Cos(angle) * ring, 0, Mathf.Sin(angle) * ring);
            float top = heights[i];
            if (i % 2 == 0)
            {
                Block(at, 6f, top, 6f);
                Slab(new Vector3(at.X, top + 0.5f, at.Z), new Vector3(13f, 1f, 13f)); // a deck wider than the shaft
            }
            else
            {
                Column(at, 3.2f, top);
                Slab(new Vector3(at.X, top + 0.5f, at.Z), new Vector3(10f, 1f, 10f));
            }
        }

        // Skybridges between two pairs of neighbouring towers, at the height of the lower one.
        for (int i = 0; i < 2; i++)
        {
            int a = i * 4, b = a + 1;
            float angleA = a * Mathf.Tau / heights.Length + 0.3f, angleB = b * Mathf.Tau / heights.Length + 0.3f;
            var pa = new Vector3(Mathf.Cos(angleA) * ring, 0, Mathf.Sin(angleA) * ring);
            var pb = new Vector3(Mathf.Cos(angleB) * ring, 0, Mathf.Sin(angleB) * ring);
            float y = Mathf.Min(heights[a], heights[b]) + 0.5f;
            var middle = (pa + pb) / 2;
            var along = pb - pa;
            var bridge = new StaticBody3D { Position = new Vector3(middle.X, y, middle.Z), Rotation = new Vector3(0, -Mathf.Atan2(along.Z, along.X), 0) };
            var size = new Vector3(along.Length(), 0.6f, 3.2f);
            bridge.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = _stone });
            bridge.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            AddChild(bridge);
        }
    }

    /// <summary>
    /// A run from the start pad to a flag, in one direction (+X), sized for walking (jump about 1.2 m high and 2 m far):
    /// steps up, a narrow beam, a moving platform across a gap, a spinning bar, a lift and a high deck.
    /// </summary>
    private void BuildParkour()
    {
        const float x0 = 30f;
        Block(new Vector3(x0, 0, 0), 6f, 0.5f, 6f); // start pad

        // Seven steps, each 0.7 m higher, with a 1 m gap between them.
        float top = 0.5f;
        for (int i = 0; i < 7; i++)
        {
            top += 0.7f;
            Block(new Vector3(x0 + 6f + i * 3.2f, 0, 0), 2.2f, top, 2.2f);
        }
        float deckTop = top; // 5.4 m

        // A narrow beam to cross.
        Slab(new Vector3(x0 + 6f + 6 * 3.2f + 1.1f + 1.0f + 7f, deckTop - 0.2f, 0), new Vector3(14f, 0.4f, 0.9f));
        float beamEnd = x0 + 6f + 6 * 3.2f + 1.1f + 1.0f + 14f;

        // A platform that slides across the gap.
        float slideCentre = beamEnd + 5.8f;
        Slider("ParkourSlider", new Vector3(slideCentre, deckTop - 0.2f, 0), Vector3.Right, 3.5f, 0.7f, new Vector3(3f, 0.4f, 3f), PlatformColor);
        float landing = slideCentre + 3.5f + 1.5f + 1.0f; // 1 m of air from the far end of its swing
        Slab(new Vector3(landing + 2.5f, deckTop - 0.2f, 0), new Vector3(5f, 0.4f, 4f));

        // A bar spinning on a pillar.
        float hub = landing + 5f + 1f + 4f;
        Column(new Vector3(hub, 0, 0), 0.6f, deckTop - 0.4f - GroundTop);
        Spinner("ParkourSpinner", new Vector3(hub, deckTop - 0.2f, 0), 0.9f, new Vector3(8f, 0.4f, 1.6f), SpinnerColor);

        // A second deck, then a lift up to a high deck with the flag.
        float deck2 = hub + 4f + 1f + 2.5f;
        Slab(new Vector3(deck2, deckTop - 0.2f, 0), new Vector3(5f, 0.4f, 4f));
        float liftX = deck2 + 2.5f + 1f + 1.5f;
        Slider("ParkourLift", new Vector3(liftX, deckTop + 3.8f, 0), Vector3.Up, 4f, 0.5f, new Vector3(3f, 0.4f, 3f), LiftColor);
        float high = deckTop + 8f;
        float highX = liftX + 1.5f + 1f + 2.5f;
        Slab(new Vector3(highX, high - 0.2f, 0), new Vector3(5f, 0.4f, 4f));

        // The flag.
        Slab(new Vector3(highX + 1.5f, high + 1.6f, 0), new Vector3(0.1f, 3.2f, 0.1f));
        Slab(new Vector3(highX + 2f, high + 2.8f, 0), new Vector3(1f, 0.6f, 0.05f));
    }

    /// <summary>Two low bars sweeping the ground beside the start, to run past or jump over.</summary>
    private void BuildSweepers()
    {
        Spinner("SweeperA", new Vector3(30f, 0.9f, -14f), 1.0f, new Vector3(8f, 0.4f, 0.5f), SpinnerColor);
        Spinner("SweeperB", new Vector3(40f, 0.9f, -14f), -1.1f, new Vector3(8f, 0.4f, 0.5f), LiftColor);
    }
}
