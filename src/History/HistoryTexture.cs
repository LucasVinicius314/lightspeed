using System.Collections.Generic;
using Godot;
using Lightspeed.Core;

namespace Lightspeed.History;

/// <summary>
/// Packs every registered object's history into one float texture for the delayed-visual shader.
/// One row per object; two texels per sample: (position.xyz, 0) then rotation quaternion (xyzw).
/// Sample time is implicit: the newest is "now" and each older one is one tick earlier.
/// Exposes the shared write head and sample count as shader globals.
/// </summary>
public partial class HistoryTexture : Node
{
    /// <summary>Samples kept per object (10 s at 60 Hz). M4 derives this from max view distance / c.</summary>
    public const int Capacity = 600;
    public const int MaxObjects = 64;

    public static HistoryTexture Instance { get; private set; } = null!;

    private readonly List<LightHistory> _objects = new();
    private Image _image = null!;
    private ImageTexture _texture = null!;
    private int _head = -1;
    private int _count;

    public override void _EnterTree()
    {
        Instance = this;
        ProcessPhysicsPriority = 2000;

        _image = Image.CreateEmpty(Capacity * 2, MaxObjects, false, Image.Format.Rgbaf);
        _texture = ImageTexture.CreateFromImage(_image);

        RenderingServer.GlobalShaderParameterAdd("lc_history", RenderingServer.GlobalShaderParameterType.Sampler2D, _texture);
        RenderingServer.GlobalShaderParameterAdd("lc_head", RenderingServer.GlobalShaderParameterType.Int, 0);
        RenderingServer.GlobalShaderParameterAdd("lc_count", RenderingServer.GlobalShaderParameterType.Int, 0);
        RenderingServer.GlobalShaderParameterAdd("lc_age", RenderingServer.GlobalShaderParameterType.Float, 0f);
        RenderingServer.GlobalShaderParameterAdd("lc_c", RenderingServer.GlobalShaderParameterType.Float, SimClock.Instance.C);
    }

    /// <summary>Seconds since the newest sample, so the shader sees time between ticks.</summary>
    public static double Age => Engine.GetPhysicsInterpolationFraction() * SimClock.TickDelta;

    public override void _Process(double delta) =>
        RenderingServer.GlobalShaderParameterSet("lc_age", (float)Age);

    public int Register(LightHistory history)
    {
        _objects.Add(history);
        return _objects.Count - 1;
    }

    public override void _PhysicsProcess(double delta)
    {
        _head = (_head + 1) % Capacity;
        if (_count < Capacity)
            _count++;

        foreach (var obj in _objects)
        {
            var s = obj[obj.Count - 1];
            _image.SetPixel(_head * 2, obj.ObjectIndex, new Color(s.Position.X, s.Position.Y, s.Position.Z, 0));
            _image.SetPixel(_head * 2 + 1, obj.ObjectIndex, new Color(s.Rotation.X, s.Rotation.Y, s.Rotation.Z, s.Rotation.W));
        }
        _texture.Update(_image);

        RenderingServer.GlobalShaderParameterSet("lc_head", _head);
        RenderingServer.GlobalShaderParameterSet("lc_count", _count);
        RenderingServer.GlobalShaderParameterSet("lc_c", SimClock.Instance.C);
    }
}
