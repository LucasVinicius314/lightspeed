using System;
using Godot;
using Lightspeed.Core;
using Lightspeed.Player;

namespace Lightspeed.Ui;

/// <summary>
/// What the drone's camera feed looks like: an on-screen display (horizon, crosshair, altitude, speed, flight
/// time, REC) drawn under a full-screen CRT shader. Visible only in drone mode with
/// <see cref="GameSettings.DroneCrt"/> on. The HUD sits below the shader too, so it gets the same video look;
/// the settings menu stays above it, clean.
/// </summary>
public partial class DroneView : CanvasLayer
{
    private const int CrtLayer = 95;

    private Control _osd = null!;
    private CanvasLayer _crtLayer = null!;
    private Label _left = null!;
    private Label _right = null!;
    private Label _rec = null!;
    private FreeCamera? _camera;

    public override void _Ready()
    {
        Layer = 91; // above the HUD (90) so the display is not hidden behind its panel, below the CRT

        _osd = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _osd.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _osd.Draw += DrawHorizon;
        AddChild(_osd);

        // Anchors and offsets are set by hand: a preset would read the parent's size before layout has run.
        _rec = MakeLabel(HorizontalAlignment.Right, anchorX: 1, anchorY: 0, offsetX: -56, offsetY: 56);
        _left = MakeLabel(HorizontalAlignment.Left, anchorX: 0, anchorY: 0.5f, offsetX: 56, offsetY: 0);
        _right = MakeLabel(HorizontalAlignment.Right, anchorX: 1, anchorY: 0.5f, offsetX: -56, offsetY: 0);

        _crtLayer = new CanvasLayer { Layer = CrtLayer };
        var rect = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        rect.Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/crt.gdshader") };
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _crtLayer.AddChild(rect);
        AddChild(_crtLayer);

        GameSettings.Changed += UpdateVisible;
        UpdateVisible();
    }

    public override void _ExitTree() => GameSettings.Changed -= UpdateVisible;

    private Label MakeLabel(HorizontalAlignment align, float anchorX, float anchorY, float offsetX, float offsetY)
    {
        var label = new Label { HorizontalAlignment = align, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 20);
        label.AddThemeColorOverride("font_color", new Color(0.75f, 1f, 0.8f));
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0.1f, 0.05f, 0.9f));
        label.AddThemeConstantOverride("outline_size", 6);
        _osd.AddChild(label);
        label.AnchorLeft = label.AnchorRight = anchorX;
        label.AnchorTop = label.AnchorBottom = anchorY;
        label.OffsetLeft = label.OffsetRight = offsetX;
        label.OffsetTop = label.OffsetBottom = offsetY;
        label.GrowHorizontal = anchorX > 0.5f ? Control.GrowDirection.Begin : Control.GrowDirection.End;
        label.GrowVertical = anchorY > 0.25f ? Control.GrowDirection.Both : Control.GrowDirection.End;
        return label;
    }

    private void UpdateVisible()
    {
        bool on = GameSettings.CameraMode == CameraMode.Drone && GameSettings.DroneCrt && !GameSettings.ThirdPerson; // the feed is the drone's own view
        _osd.Visible = on;
        _crtLayer.Visible = on;
        SetProcess(on);
    }

    public override void _Process(double delta)
    {
        _camera ??= GetViewport().GetCamera3D() as FreeCamera;
        if (_camera == null)
            return;

        var clock = SimClock.Instance;
        float speed = _camera.Velocity.Length();
        var flight = TimeSpan.FromSeconds(clock.Time);

        bool blink = Time.GetTicksMsec() / 600 % 2 == 0; // visual only
        _rec.Text = (blink ? "● " : "  ") + "REC";
        _left.Text = $"THR {_camera.Throttle * 100,5:0}%\nALT {_camera.GlobalPosition.Y,6:0.0} m\nSPD {speed,6:0.0} m/s\n    {speed / clock.C * 100,6:0.###}% c";
        float heading = Mathf.PosMod(Mathf.RadToDeg(Mathf.Atan2(-_camera.GlobalBasis.Z.X, _camera.GlobalBasis.Z.Z)), 360f);
        _right.Text = $"T+ {(int)flight.TotalMinutes:00}:{flight.Seconds:00}.{flight.Milliseconds / 100}\n" +
                      $"c  {SpeedLadder.FormatSpeed(clock.C)}\nHDG {heading,4:0}°" +
                      (clock.Paused ? "\nPAUSED" : "");
        _osd.QueueRedraw();
    }

    /// <summary>Crosshair and an artificial horizon that tilts with the drone's bank and shifts with its pitch.</summary>
    private void DrawHorizon()
    {
        if (_camera == null)
            return;
        var size = _osd.Size;
        var centre = size / 2;
        var green = new Color(0.75f, 1f, 0.8f, 0.9f);

        // Read the horizon off the camera's full orientation, so it stays right through flips and rolls.
        var basis = _camera.GlobalBasis;
        var worldUp = basis.Inverse() * Vector3.Up;
        float pitch = Mathf.Asin(Mathf.Clamp(-basis.Z.Y, -1f, 1f));
        var screenUp = new Vector2(worldUp.X, -worldUp.Y);
        screenUp = screenUp.LengthSquared() < 1e-6f ? Vector2.Up : screenUp.Normalized();
        float focal = size.Y / 2 / Mathf.Tan(Mathf.DegToRad(_camera.Fov) / 2); // Fov is vertical, in pixels of focal length
        var up = screenUp; // screen direction of "up"
        var along = new Vector2(-up.Y, up.X);
        var horizon = centre - up * (focal * Mathf.Tan(pitch)); // looking up pushes the horizon down the screen

        float half = size.X * 0.14f;
        const float gap = 46f;
        _osd.DrawLine(horizon - along * half, horizon - along * gap, green, 2f);
        _osd.DrawLine(horizon + along * gap, horizon + along * half, green, 2f);

        _osd.DrawLine(centre + new Vector2(-14, 0), centre + new Vector2(-5, 0), green, 2f);
        _osd.DrawLine(centre + new Vector2(5, 0), centre + new Vector2(14, 0), green, 2f);
        _osd.DrawLine(centre + new Vector2(0, -14), centre + new Vector2(0, -5), green, 2f);
        _osd.DrawLine(centre + new Vector2(0, 5), centre + new Vector2(0, 14), green, 2f);
    }
}
