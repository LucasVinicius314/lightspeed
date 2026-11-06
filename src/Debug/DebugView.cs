using System.Collections.Generic;
using System.Text;
using Godot;
using Lightspeed.Core;
using Lightspeed.History;
using Lightspeed.Visuals;

namespace Lightspeed.Debug;

/// <summary>
/// Lets you check the effect by eye. Keys: 1 ghosts (true present positions), 2 delay rings on the
/// ground around the camera, 3 per-object delay labels and ghost-to-visual lines.
/// The three toggles are remembered between runs (user://debug_view.cfg). Only drawn in the full
/// debug mode (F3, see DebugMode); the toggles are ignored otherwise.
/// Delays come from the CPU solver, which is the reference for the shader.
/// </summary>
public partial class DebugView : Node3D
{
    private const float RingY = 0.13f;
    private const int Segments = 96;

    private const string SettingsPath = "user://debug_view.cfg";

    private bool _ghosts = true;
    private bool _rings = true;
    private bool _labels = true;

    private readonly ImmediateMesh _mesh = new();
    private readonly List<(Vector3 A, Vector3 B, Color Color)> _lines = new();
    private readonly List<Label3D> _ringLabels = new();
    private readonly Dictionary<DelayedVisual, Label3D> _objectLabels = new();
    private readonly StringBuilder _text = new();
    private Label _panel = null!;
    private CanvasLayer _layer = null!;

    private static bool Active => DebugMode.Current == DebugLabelMode.Both;

    public override void _Ready()
    {
        AddChild(new MeshInstance3D
        {
            Mesh = _mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(-500, -500, -500), new Vector3(1000, 1000, 1000)),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
            },
        });

        _layer = new CanvasLayer { Layer = 95 };
        var layer = _layer;
        AddChild(layer);
        _panel = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _panel.GrowHorizontal = Control.GrowDirection.Begin;
        _panel.OffsetRight = -12;
        _panel.OffsetTop = 10;
        _panel.AddThemeColorOverride("font_color", Colors.White);
        _panel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _panel.AddThemeConstantOverride("outline_size", 6);
        layer.AddChild(_panel);

        LoadSettings();
        ApplyGhosts();
        // Ghosts of objects spawned later must follow the toggle too.
        GetTree().NodeAdded += node =>
        {
            if (node is Node3D ghost && ghost.IsInGroup("ghost"))
                ghost.Visible = _ghosts;
        };
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!Active || @event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        switch (key.Keycode)
        {
            case Key.Key1: _ghosts = !_ghosts; ApplyGhosts(); break;
            case Key.Key2: _rings = !_rings; break;
            case Key.Key3: _labels = !_labels; break;
            default: return;
        }
        SaveSettings();
    }

    private void LoadSettings()
    {
        var config = new ConfigFile();
        if (config.Load(SettingsPath) != Error.Ok)
            return; // first run: keep the defaults
        _ghosts = (bool)config.GetValue("debug_view", "ghosts", _ghosts);
        _rings = (bool)config.GetValue("debug_view", "rings", _rings);
        _labels = (bool)config.GetValue("debug_view", "labels", _labels);
    }

    private void SaveSettings()
    {
        var config = new ConfigFile();
        config.SetValue("debug_view", "ghosts", _ghosts);
        config.SetValue("debug_view", "rings", _rings);
        config.SetValue("debug_view", "labels", _labels);
        config.Save(SettingsPath);
    }

    private void ApplyGhosts() => GetTree().CallGroup("ghost", Node3D.MethodName.SetVisible, _ghosts);

    private void HideOverlays()
    {
        _layer.Visible = false;
        _mesh.ClearSurfaces();
        foreach (var label in _ringLabels)
            label.Visible = false;
        foreach (var label in _objectLabels.Values)
            label.Visible = false;
    }

    public override void _Process(double delta)
    {
        if (!Active)
        {
            HideOverlays();
            return;
        }
        _layer.Visible = true;

        var camera = GetViewport().GetCamera3D();
        if (camera == null)
            return;

        var clock = SimClock.Instance;
        float c = clock.C;
        double historySeconds = HistoryTexture.Capacity * SimClock.TickDelta;
        var camPos = camera.GlobalPosition;
        _lines.Clear();
        _text.Clear();

        Line("DEBUG VIEW");
        Line($"[1] ghosts {OnOff(_ghosts)}   [2] rings {OnOff(_rings)}   [3] labels {OnOff(_labels)}");
        Line($"c = {SpeedLadder.FormatSpeed(c)}");
        int filled = HistoryTexture.Instance.SampleCount;
        Line($"history {filled}/{HistoryTexture.Capacity} samples  ({filled * SimClock.TickDelta:0.0}/{historySeconds:0} s)");

        Line($"history update {HistoryTexture.Instance.UpdateMs:0.00} ms/tick");
        UpdateRings(camera, c, historySeconds);
        UpdateObjects(camPos, c);

        _mesh.ClearSurfaces();
        if (_lines.Count > 0)
        {
            _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            foreach (var (a, b, color) in _lines)
            {
                _mesh.SurfaceSetColor(color);
                _mesh.SurfaceAddVertex(a);
                _mesh.SurfaceSetColor(color);
                _mesh.SurfaceAddVertex(b);
            }
            _mesh.SurfaceEnd();
        }
        _panel.Text = _text.ToString();
    }

    private void UpdateRings(Camera3D camera, float c, double historySeconds)
    {
        var delays = _rings ? LightRings.Delays(c, historySeconds) : System.Array.Empty<double>();
        var center = new Vector3(camera.GlobalPosition.X, RingY, camera.GlobalPosition.Z);

        // Labels sit on the ground straight ahead of the camera.
        var forward = -camera.GlobalBasis.Z;
        var ahead = new Vector3(forward.X, 0, forward.Z);
        ahead = ahead.LengthSquared() > 1e-4f ? ahead.Normalized() : Vector3.Forward;

        if (_rings)
            Line(delays.Count > 0
                ? $"rings every {LightRings.FormatDelay(delays[0])} ({SpeedLadder.FormatDistance(c * delays[0])})"
                : "rings: none within range");

        for (int i = 0; i < delays.Count; i++)
        {
            float radius = (float)(c * delays[i]);
            var color = Color.FromHsv(0.5f + 0.35f * i / System.Math.Max(delays.Count - 1, 1), 0.7f, 1f);
            for (int s = 0; s < Segments; s++)
            {
                float a0 = Mathf.Tau * s / Segments, a1 = Mathf.Tau * (s + 1) / Segments;
                _lines.Add((center + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * radius,
                            center + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * radius, color));
            }

            var label = RingLabel(i);
            label.Text = LightRings.FormatDelay(delays[i]);
            label.Modulate = color;
            label.GlobalPosition = center + ahead * radius + Vector3.Up * 0.2f;
            label.Visible = true;
        }
        for (int i = delays.Count; i < _ringLabels.Count; i++)
            _ringLabels[i].Visible = false;
    }

    private const int MaxListed = 8;
    private const int MaxLabeled = 40;

    private void UpdateObjects(Vector3 camPos, float c)
    {
        var camN = camPos.ToNumerics();
        var visuals = GetTree().GetNodesInGroup("delayed_visual");
        Line($"objects {visuals.Count}   rows in use {HistoryTexture.Instance.ActiveTracks}/{HistoryTexture.MaxObjects}");

        // Drop labels of visuals that have been freed.
        foreach (var dead in new List<DelayedVisual>(_objectLabels.Keys))
        {
            if (GodotObject.IsInstanceValid(dead))
                continue;
            _objectLabels[dead].QueueFree();
            _objectLabels.Remove(dead);
        }

        int listed = 0, labeled = 0;
        foreach (var node in visuals)
        {
            if (node is not DelayedVisual { Track: { Count: > 0 } track } visual)
                continue;

            var pose = RetardedSolver.Solve(track, SimClock.TickDelta, camN, c, System.Numerics.Vector3.Zero, HistoryTexture.AgeOf(track));
            bool show = pose.Status == SolveStatus.Ok;
            string state = track.IsFinished ? " (despawned)" : "";

            if (listed < MaxListed)
            {
                Line(show
                    ? $"{visual.BodyName,-14} {LightRings.FormatDelay(pose.Delay),9}   {SpeedLadder.FormatDistance(camPos.DistanceTo(pose.Position.ToGodot()))} away{state}"
                    : $"{visual.BodyName,-14} not visible{state}");
                listed++;
            }

            if (labeled >= MaxLabeled)
                continue;
            var label = ObjectLabel(visual);
            label.Visible = show && _labels;
            if (!show)
                continue;

            labeled++;
            var seen = pose.Position.ToGodot();
            label.Text = LightRings.FormatDelay(pose.Delay);
            label.GlobalPosition = seen + Vector3.Up * 0.9f;
            if (_labels && !track.IsFinished && GodotObject.IsInstanceValid(visual.Body))
                _lines.Add((seen, visual.Body!.GlobalPosition, new Color(1f, 0.9f, 0.2f)));
        }
        if (visuals.Count > listed)
            Line($"... and {visuals.Count - listed} more");
    }

    private Label3D RingLabel(int index)
    {
        while (_ringLabels.Count <= index)
        {
            var label = NewLabel(32);
            _ringLabels.Add(label);
        }
        return _ringLabels[index];
    }

    private Label3D ObjectLabel(DelayedVisual visual)
    {
        if (!_objectLabels.TryGetValue(visual, out var label))
            _objectLabels[visual] = label = NewLabel(28);
        return label;
    }

    private Label3D NewLabel(int fontSize)
    {
        var label = new Label3D
        {
            TopLevel = true,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            FixedSize = true,
            NoDepthTest = true,
            PixelSize = 0.0007f,
            FontSize = fontSize,
            OutlineSize = 8,
        };
        AddChild(label);
        return label;
    }

    // StringBuilder.AppendLine would add CR+LF on Windows, which Godot labels render as blank lines.
    private void Line(string text) => _text.Append(text).Append('\n');

    private static string OnOff(bool on) => on ? "on " : "off";
}
