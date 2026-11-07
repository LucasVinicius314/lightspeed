using System.Linq;
using Godot;
using Lightspeed.Core;
using Lightspeed.Debug;
using Lightspeed.History;

namespace Lightspeed.Ui;

/// <summary>
/// Bottom-centre readout of the speed of light with a progress bar over the whole ladder, plus a status chip
/// at the top (camera mode, pause, time scale, selected throwable).
/// Scrolling the mouse wheel or pressing Up steps c faster; wheel down or Down steps it slower.
/// </summary>
public partial class LightSpeedHud : CanvasLayer
{
    private Label _title = null!;
    private Label _value = null!;
    private Label _detail = null!;
    private Label _keys = null!;
    private Label _status = null!;
    private Control _bar = null!;
    private string _lastStatus = "";

    public override void _Ready()
    {
        Layer = 90;

        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // Bottom block: a centred glass panel that only grows as wide as its content.
        var bottom = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bottom.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        bottom.OffsetBottom = -14;
        bottom.GrowVertical = Control.GrowDirection.Begin;
        root.AddChild(bottom);
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.03f, 0.04f, 0.07f, 0.62f), 10, 22, 10));
        bottom.AddChild(panel);
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 3);
        panel.AddChild(box);

        _title = MakeLabel(box, 12);
        _title.Text = "SPEED OF LIGHT";
        _title.AddThemeColorOverride("font_color", UiTheme.Accent);
        _value = MakeLabel(box, 30);
        _detail = MakeLabel(box, 14);
        _detail.AddThemeColorOverride("font_color", UiTheme.Dim);

        _bar = new Control
        {
            CustomMinimumSize = new Vector2(560, 36),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _bar.Draw += DrawBar;
        box.AddChild(_bar);

        _keys = MakeLabel(box, 12);
        _keys.AddThemeColorOverride("font_color", UiTheme.Dim);
        DebugMode.Changed += _ => RefreshKeys();
        RefreshKeys();

        // Top chip: what mode the player is in and whether time is running.
        var top = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        top.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        top.OffsetTop = 12;
        root.AddChild(top);
        var chip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        chip.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(0.03f, 0.04f, 0.07f, 0.62f), 14, 16, 5));
        top.AddChild(chip);
        _status = MakeLabel(chip, 14);

        SimClock.Instance.CChanged += _ => Refresh();
        Refresh();
    }

    private void RefreshKeys() =>
        _keys.Text = $"Scroll / Up / Down  light speed     V  mode     P  pause     Tab  settings     H  keys     F3  debug: {DebugMode.Describe(DebugMode.Current)}";

    public override void _Process(double delta)
    {
        var clock = SimClock.Instance;
        string mode = GameSettings.CameraMode.ToString().ToUpperInvariant();
        string time = clock.Paused ? $"PAUSED  tick {clock.Tick}  (. steps)"
            : Mathf.IsEqualApprox(clock.TimeScale, 1f) ? "" : $"time x{clock.TimeScale:0.##}";
        string throwable = GetTree().GetFirstNodeInGroup("thrower") is Demo.Thrower thrower
            ? $"click: throw {thrower.SelectedName.ToLowerInvariant()}" : "";
        string text = string.Join("     ", new[] { mode, time, throwable }.Where(part => part.Length > 0));
        if (text == _lastStatus)
            return;
        _lastStatus = text;
        _status.Text = text;
    }

    private static Label MakeLabel(Control parent, int size)
    {
        var label = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 8);
        parent.AddChild(label);
        return label;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (SettingsMenu.IsOpen)
            return;
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Up }:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadUp }:
                SimClock.Instance.StepFaster();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Down }:
            case InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.DpadDown }:
                SimClock.Instance.StepSlower();
                break;
        }
    }

    private void Refresh()
    {
        var clock = SimClock.Instance;
        var step = SpeedLadder.Steps[clock.StepIndex];
        _value.Text = SpeedLadder.FormatSpeed(clock.C) + (step.Name != null ? $"  ({step.Name})" : "");

        double history = HistoryTexture.Capacity * SimClock.TickDelta;
        _detail.Text = $"{SpeedLadder.FormatFractionOfC(clock.C)}   |   " +
                       $"step {clock.StepIndex + 1}/{SpeedLadder.Steps.Count}   |   " +
                       $"sees up to {SpeedLadder.FormatDistance(clock.C * history)} ({history:0} s of history)";
        _bar.QueueRedraw();
    }

    private void DrawBar()
    {
        int count = SpeedLadder.Steps.Count;
        float progress = (float)SimClock.Instance.StepIndex / (count - 1);
        var size = _bar.Size;
        const float h = 18f; // bar height; the labels sit below it
        var track = new Rect2(0, 5, size.X, h - 10);

        _bar.DrawRect(track, new Color(0, 0, 0, 0.6f));
        _bar.DrawRect(new Rect2(track.Position, new Vector2(track.Size.X * progress, track.Size.Y)), new Color(1f, 0.6f, 0.2f));
        for (int i = 0; i < count; i++)
        {
            float x = size.X * i / (count - 1);
            _bar.DrawLine(new Vector2(x, track.Position.Y), new Vector2(x, track.End.Y), new Color(1, 1, 1, 0.25f));
        }
        _bar.DrawRect(new Rect2(size.X * progress - 2, 0, 4, h), Colors.White);

        var font = ThemeDB.FallbackFont;
        _bar.DrawString(font, new Vector2(0, h + 14), "1 cm/s", fontSize: 12);
        _bar.DrawString(font, new Vector2(size.X - 14, h + 14), "c", fontSize: 12);
    }
}
