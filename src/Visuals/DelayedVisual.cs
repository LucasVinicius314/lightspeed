using System.Collections.Generic;
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
    [Export] public bool LogDelay { get; set; }

    /// <summary>
    /// Shader solve, or the CPU reference (see <see cref="GameSettings.CpuSolver"/>). Mesh edge length and the
    /// Light Echoes options also come from <see cref="GameSettings"/>.
    /// </summary>
    public SolveMode Mode => GameSettings.CpuSolver ? SolveMode.Cpu : SolveMode.Shader;

    /// <summary>The object's history. Outlives the object's nodes.</summary>
    public HistoryTrack? Track { get; private set; }
    public string BodyName { get; private set; } = "";
    /// <summary>The true-position node. May be freed after despawn; check <c>GodotObject.IsInstanceValid</c>.</summary>
    public Node3D? Body { get; private set; }

    /// <summary>How many images of this part are drawn right now (1 unless it moves near or above c).</summary>
    public int ImageCount { get; private set; } = 1;

    /// <summary>True while this part is drawn per pixel (Exact rays) instead of per vertex.</summary>
    public bool IsRayRendered { get; private set; }

    // Names used every frame, converted once: a C# string becomes a new StringName on every call otherwise.
    private static readonly StringName LinBound = "lin_bound", AngBound = "ang_bound", IvCount = "iv_count",
        IvSphere = "iv_sphere", IvK = "iv_k", RectParam = "rect", SpeedBound = "speed_bound", Suppress = "suppress";

    private float _lastLinear = -1f, _lastAngular = -1f;
    private ShaderMaterial _material = null!;
    private readonly List<MeshInstance3D> _extraImages = new();
    private float _radius;
    private float _speedBound;
    private Mesh? _sourceMesh;
    private float _tessellatedEdge;
    private Shape? _shape;
    private MeshInstance3D? _rayProxy;
    private ShaderMaterial? _rayMaterial;
    private bool _suppressed;
    private readonly Vector4[] _intervalSpheres = new Vector4[RetardedRay.MaxIntervals];
    private readonly Vector2[] _intervalRanges = new Vector2[RetardedRay.MaxIntervals];

    public override void _Ready()
    {
        AddToGroup("delayed_visual");
        ProcessMode = ProcessModeEnum.Always; // keeps solving against the moving camera while the simulation is paused
        TopLevel = true;
        CastShadow = ShadowCastingSetting.Off; // shadows are M7; the shader reads the camera position
        CustomAabb = new Aabb(new Vector3(-500, -500, -500), new Vector3(1000, 1000, 1000));

        _sourceMesh = Mesh;
        if (_sourceMesh != null)
        {
            _tessellatedEdge = GameSettings.MaxEdgeLength;
            Mesh = MeshTessellation.Refine(_sourceMesh, _tessellatedEdge);

            _shape = ShapeOf(_sourceMesh);
            var box = _sourceMesh.GetAabb();
            for (int i = 0; i < 8; i++) // farthest corner from the origin bounds every vertex
                _radius = Mathf.Max(_radius, box.GetEndpoint(i).Length());
        }

        Track = History!.Track;
        Body = History.GetParent<Node3D>();
        BodyName = Body.Name;

        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/delayed_visual.gdshader") };
        _material.SetShaderParameter("object_index", Track?.Row ?? 0);
        _material.SetShaderParameter("albedo", Albedo);
        MaterialOverride = _material;

        ApplyMode();
        GameSettings.Changed += OnSettingsChanged;
    }

    public override void _ExitTree() => GameSettings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged()
    {
        ApplyMode();
        if (_sourceMesh == null || Mathf.IsEqualApprox(_tessellatedEdge, GameSettings.MaxEdgeLength))
            return;

        // The edge limit changed: rebuild the mesh from the original and hand it to every image.
        _tessellatedEdge = GameSettings.MaxEdgeLength;
        Mesh = MeshTessellation.Refine(_sourceMesh, _tessellatedEdge);
        foreach (var extra in _extraImages)
            extra.Mesh = Mesh;
    }

    /// <summary>Leaves the despawning object's subtree so it can outlive it.</summary>
    public void Detach()
    {
        History = null; // the LightHistory node is about to be freed; Track keeps the data
        Reparent(GetTree().CurrentScene);
    }

    /// <summary>
    /// Decides how many images this part needs. Slower than light there is one; at or above c a vertex's
    /// light can arrive from several moments, so the mesh is drawn once per image (the shader's image_index).
    /// </summary>
    private void UpdateImages()
    {
        float c = SimClock.Instance.C;
        // With Light Echoes off the bound stays 0, which keeps every part on the single-image path.
        var fast = GameSettings.FastParts;
        float bound = Mode == SolveMode.Cpu || fast == FastPartRendering.SingleImage
            ? 0f
            : Track!.PeakVertexSpeed(_radius) * 1.05f;
        if (Mathf.Abs(bound - _speedBound) > 0.02f * Mathf.Max(bound, 0.1f))
        {
            _speedBound = bound;
            _material.SetShaderParameter(SpeedBound, bound);
            foreach (var extra in _extraImages)
                ((ShaderMaterial)extra.MaterialOverride).SetShaderParameter(SpeedBound, bound);
        }

        // Roughly one extra image per 1.25 c of speed, since a circling point passes its own light
        // about twice per turn and turns (speed / c) / pi times while the light crosses the object.
        IsRayRendered = _shape != null && Mode == SolveMode.Shader
            && (fast == FastPartRendering.RaysAlways || (fast == FastPartRendering.Rays && bound >= c));
        ImageCount = bound < c || IsRayRendered
            ? 1
            : Mathf.Clamp(1 + Mathf.CeilToInt(0.8f * bound / c), 1, GameSettings.MaxImages);

        // While the per-pixel renderer draws this part, the vertex draws step aside.
        if (_suppressed != IsRayRendered)
        {
            _suppressed = IsRayRendered;
            _material.SetShaderParameter(Suppress, IsRayRendered);
        }

        while (_extraImages.Count < ImageCount - 1)
            _extraImages.Add(MakeExtraImage(_extraImages.Count + 1));
        for (int i = 0; i < _extraImages.Count; i++)
            _extraImages[i].Visible = i < ImageCount - 1;
    }

    /// <summary>Keeps the per-pixel renderer's quad and its per-frame uniforms in step with the part.</summary>
    private void UpdateRayProxy()
    {
        if (!IsRayRendered)
        {
            if (_rayProxy != null)
                _rayProxy.Visible = false;
            return;
        }

        if (_rayProxy == null)
            CreateRayProxy();
        _rayProxy!.Visible = true;

        var track = Track!;
        float linear = track.PeakLinearSpeed * 1.05f + 0.01f;
        float angular = track.PeakAngularSpeed * 1.05f + 0.001f;
        if (linear != _lastLinear || angular != _lastAngular)
        {
            _lastLinear = linear;
            _lastAngular = angular;
            _rayMaterial!.SetShaderParameter(LinBound, linear);
            _rayMaterial.SetShaderParameter(AngBound, angular);
        }
        UploadIntervals(track, linear);
    }

    /// <summary>
    /// Works out on the CPU the few stretches of history in which light from this part can reach the camera,
    /// with a sphere around where the part is during each, so a pixel only marches where its ray can hit.
    /// </summary>
    private void UploadIntervals(HistoryTrack track, float linearBound)
    {
        int count = 0;
        var camera = GetViewport().GetCamera3D();
        if (camera != null)
        {
            var intervals = RetardedRay.Intervals(track, SimClock.TickDelta, camera.GlobalPosition.ToNumerics(),
                SimClock.Instance.C, HistoryTexture.AgeOf(track), _radius, linearBound);

            // The solver counts samples within this track; the texture counts them within the shared window.
            double offset = (HistoryTexture.Instance.SampleCount - 1)
                            - (SimClock.Instance.Tick - track.LastTick) - (track.Count - 1);
            count = Mathf.Min(intervals.Count, RetardedRay.MaxIntervals);
            for (int i = 0; i < count; i++)
            {
                var iv = intervals[i];
                var centre = iv.Center.ToGodot();
                _intervalSpheres[i] = new Vector4(centre.X, centre.Y, centre.Z, iv.Radius);
                _intervalRanges[i] = new Vector2((float)(iv.KNewest + offset), (float)(iv.KOldest + offset));
            }
        }
        _rayMaterial!.SetShaderParameter(IvCount, count);
        _rayMaterial.SetShaderParameter(IvSphere, _intervalSpheres);
        _rayMaterial.SetShaderParameter(IvK, _intervalRanges);
        var rect = ScreenRect(camera, count);
        _rayMaterial.SetShaderParameter(RectParam, rect);
        _rayProxy!.Visible = rect.X < rect.Z && rect.Y < rect.W; // an empty rectangle: the part is off screen
    }

    /// <summary>
    /// Screen rectangle (NDC min xy, max xy) that covers every interval sphere, so the quad only shades pixels
    /// where a ray can hit the part. The whole screen when the camera is inside or near a sphere.
    /// </summary>
    private Vector4 ScreenRect(Camera3D? camera, int count)
    {
        var full = new Vector4(-1, -1, 1, 1);
        if (camera == null)
            return full;
        if (count == 0)
            return Vector4.Zero;

        var view = camera.GlobalTransform.AffineInverse();
        var proj = camera.GetCameraProjection();
        float sx = proj.X.X, sy = proj.Y.Y; // NDC per unit of x/depth and y/depth
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < count; i++)
        {
            var sph = _intervalSpheres[i];
            var v = view * new Vector3(sph.X, sph.Y, sph.Z);
            float near = -v.Z - sph.W; // depth of the sphere's nearest point
            if (near <= camera.Near * 1.5f)
                return full; // the camera is inside or at the sphere: no useful bound
            // A point d from the centre (|d| <= radius) moves its NDC by at most radius (scale + |centre NDC|) / nearest
            // depth: sideways by d, and by the depth change times the view angle (conservative).
            float cx = sx * v.X / -v.Z, cy = -sy * v.Y / -v.Z; // clip-space Y points down in the renderer
            float hx = sph.W * (sx + Mathf.Abs(cx)) / near, hy = sph.W * (sy + Mathf.Abs(cy)) / near;
            min = min.Min(new Vector2(cx - hx, cy - hy));
            max = max.Max(new Vector2(cx + hx, cy + hy));
        }

        const float pad = 0.02f;
        min -= new Vector2(pad, pad);
        max += new Vector2(pad, pad);
        if (max.X <= -1f || min.X >= 1f || max.Y <= -1f || min.Y >= 1f)
            return Vector4.Zero; // entirely outside the screen
        return new Vector4(Mathf.Max(min.X, -1f), Mathf.Max(min.Y, -1f), Mathf.Min(max.X, 1f), Mathf.Min(max.Y, 1f));
    }

    private void CreateRayProxy()
    {
        var shape = _shape!.Value;
        _rayMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/delayed_ray.gdshader") };
        _rayMaterial.SetShaderParameter("object_index", Track?.Row ?? 0);
        _rayMaterial.SetShaderParameter("albedo", Albedo);
        _rayMaterial.SetShaderParameter("shape_kind", (int)shape.Kind);
        _rayMaterial.SetShaderParameter("shape_size", shape.Size.ToGodot());
        _rayMaterial.SetShaderParameter("obj_radius", _radius);

        _rayProxy = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(2, 2) },
            MaterialOverride = _rayMaterial,
            CastShadow = ShadowCastingSetting.Off,
            CustomAabb = CustomAabb,
            IgnoreOcclusionCulling = true,
        };
        AddChild(_rayProxy);
    }

    /// <summary>The exact solid for primitives the per-pixel renderer understands; null for any other mesh.</summary>
    private static Shape? ShapeOf(Mesh mesh) => mesh switch
    {
        BoxMesh box => Shape.Box(new System.Numerics.Vector3(box.Size.X, box.Size.Y, box.Size.Z)),
        SphereMesh sphere when Mathf.IsEqualApprox(sphere.Height, 2f * sphere.Radius) => Shape.Sphere(sphere.Radius),
        CylinderMesh cyl when Mathf.IsEqualApprox(cyl.TopRadius, cyl.BottomRadius) => Shape.Cylinder(cyl.TopRadius, cyl.Height),
        _ => null,
    };

    private MeshInstance3D MakeExtraImage(int index)
    {
        var material = (ShaderMaterial)_material.Duplicate();
        material.SetShaderParameter("image_index", index);
        var extra = new MeshInstance3D
        {
            Mesh = Mesh,
            MaterialOverride = material,
            CastShadow = ShadowCastingSetting.Off,
            CustomAabb = CustomAabb,
        };
        AddChild(extra);
        return extra;
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
        UpdateImages();
        UpdateRayProxy();
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
