using System;
using System.Runtime.InteropServices;
using Godot;
using Lightspeed.History;

namespace Lightspeed.Visuals;

/// <summary>
/// Draws every Exact-rays part with a single MultiMesh draw. Each part that is ray-rendered this frame asks for a
/// row (<see cref="NextRow"/>), fills it with its shape, intervals and screen rectangle, and once all parts have
/// run, this node uploads the rows as one float texture and sets how many instances to draw. The layout of a row
/// is documented in shaders/delayed_ray.gdshader. One draw call and one upload replace a material, a draw and
/// about six uniform updates per part.
/// </summary>
public partial class RayBatch : Node
{
    /// <summary>RGBA32F texels per part row (the shader reads 29 of them).</summary>
    public const int Texels = 32;

    public const int FloatsPerRow = Texels * 4;
    private const int InitialRows = 64;

    private static RayBatch? _instance;

    /// <summary>Camera of this frame, set by <see cref="BeginFrame"/>.</summary>
    public Camera3D? Camera { get; private set; }
    public System.Numerics.Vector3 CameraPosition { get; private set; }
    public Transform3D View { get; private set; }
    public Projection Projection { get; private set; }

    private MultiMesh _multi = null!;
    private MultiMeshInstance3D _drawer = null!;
    private ShaderMaterial _material = null!;
    private Image? _image;
    private ImageTexture? _texture;
    private byte[] _bytes = Array.Empty<byte>();
    private int _capacity;
    private int _used;
    private ulong _frame = ulong.MaxValue;
    private bool _grew = true;

    /// <summary>The batch, created on first use and kept for the life of the game.</summary>
    public static RayBatch Ensure(SceneTree tree)
    {
        if (_instance != null && IsInstanceValid(_instance))
            return _instance;
        _instance = new RayBatch { Name = "RayBatch" };
        tree.Root.CallDeferred(Node.MethodName.AddChild, _instance);
        return _instance;
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        ProcessPriority = 1000; // after every part has submitted its row this frame

        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/delayed_ray.gdshader") };
        _multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            InstanceCount = HistoryTexture.MaxObjects, // one instance per possible part; only the used ones are drawn
            VisibleInstanceCount = 0,
        };
        _drawer = new MultiMeshInstance3D
        {
            Multimesh = _multi,
            MaterialOverride = _material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(-500, -500, -500), new Vector3(1000, 1000, 1000)),
            IgnoreOcclusionCulling = true,
        };
        AddChild(_drawer);
        Grow(InitialRows);
    }

    /// <summary>Reads the camera once per frame, however many parts ask.</summary>
    public bool BeginFrame(Viewport viewport)
    {
        if (!IsNodeReady())
            return false; // added deferred: parts that run before it is in the tree skip this frame
        ulong frame = Engine.GetProcessFrames();
        if (frame != _frame)
        {
            _frame = frame;
            Camera = viewport.GetCamera3D();
            if (Camera != null)
            {
                View = Camera.GlobalTransform.AffineInverse();
                Projection = Camera.GetCameraProjection();
                CameraPosition = Camera.GlobalPosition.ToNumerics();
            }
        }
        return Camera != null;
    }

    /// <summary>The next free row for this frame, to be filled completely; empty when the batch is full.</summary>
    public Span<float> NextRow()
    {
        if (_used >= HistoryTexture.MaxObjects)
            return default;
        if (_used >= _capacity)
            Grow(Math.Min(_capacity * 2, HistoryTexture.MaxObjects));
        var row = MemoryMarshal.Cast<byte, float>(_bytes.AsSpan(_used * FloatsPerRow * sizeof(float), FloatsPerRow * sizeof(float)));
        _used++;
        return row;
    }

    private void Grow(int rows)
    {
        var bytes = new byte[rows * FloatsPerRow * sizeof(float)];
        Buffer.BlockCopy(_bytes, 0, bytes, 0, Math.Min(_bytes.Length, bytes.Length)); // keep the rows already written
        _bytes = bytes;
        _capacity = rows;
        _grew = true;
    }

    public override void _Process(double delta)
    {
        if (_used > 0)
        {
            // One reused image: a new one every frame is native memory that only a full GC would free.
            if (_grew || _image == null || _texture == null)
            {
                _image = Image.CreateEmpty(Texels, _capacity, false, Image.Format.Rgbaf);
                _image.SetData(Texels, _capacity, false, Image.Format.Rgbaf, _bytes);
                _texture = ImageTexture.CreateFromImage(_image);
                _material.SetShaderParameter("ray_data", _texture);
                _grew = false;
            }
            else
            {
                _image.SetData(Texels, _capacity, false, Image.Format.Rgbaf, _bytes);
                _texture.Update(_image);
            }
        }
        _multi.VisibleInstanceCount = _used;
        _used = 0;
    }
}
