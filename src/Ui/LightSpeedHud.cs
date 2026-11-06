using Godot;
using Lightspeed.Core;
using Lightspeed.Debug;
using Lightspeed.History;

namespace Lightspeed.Ui;

/// <summary>
/// Bottom-centre readout of the speed of light with a progress bar over the whole ladder.
/// Scrolling the mouse wheel up steps c faster, down steps it slower.
/// </summary>
public partial class LightSpeedHud : CanvasLayer
{
    private Label _title = null!;
    private Label _value = null!;
    private Label _detail = null!;
    private Label _keys = null!;
    private Control _bar = null!;

    public override void _Ready()
    {
        Layer = 90;

        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        box.OffsetBottom = -16;
        box.GrowVertical = Control.GrowDirection.Begin;
        box.AddThemeConstantOverride("separation", 4);
        root.AddChild(box);

        _title = MakeLabel(box, 14);
        _title.Text = "SPEED OF LIGHT";
        _value = MakeLabel(box, 30);
        _detail = MakeLabel(box, 14);

        _bar = new Control
        {
            CustomMinimumSize = new Vector2(560, 18),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _bar.Draw += DrawBar;
        box.AddChild(_bar);

        _keys = MakeLabel(box, 14);
        DebugMode.Changed += _ => RefreshKeys();
        RefreshKeys();

        SimClock.Instance.CChanged += _ => Refresh();
        Refresh();
    }

    private void RefreshKeys() =>
        _keys.Text = $"[Scroll up] faster     [Scroll down] slower     [Esc] free mouse     [F3] debug: {DebugMode.Describe(DebugMode.Current)}";

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
        if (@event is not InputEventMouseButton { Pressed: true } click)
            return;
        if (click.ButtonIndex == MouseButton.WheelUp)
            SimClock.Instance.StepFaster();
        else if (click.ButtonIndex == MouseButton.WheelDown)
            SimClock.Instance.StepSlower();
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
        var track = new Rect2(0, 5, size.X, size.Y - 10);

        _bar.DrawRect(track, new Color(0, 0, 0, 0.6f));
        _bar.DrawRect(new Rect2(track.Position, new Vector2(track.Size.X * progress, track.Size.Y)), new Color(1f, 0.6f, 0.2f));
        for (int i = 0; i < count; i++)
        {
            float x = size.X * i / (count - 1);
            _bar.DrawLine(new Vector2(x, track.Position.Y), new Vector2(x, track.End.Y), new Color(1, 1, 1, 0.25f));
        }
        _bar.DrawRect(new Rect2(size.X * progress - 2, 0, 4, size.Y), Colors.White);

        var font = ThemeDB.FallbackFont;
        _bar.DrawString(font, new Vector2(0, size.Y + 14), "1 cm/s", fontSize: 12);
        _bar.DrawString(font, new Vector2(size.X - 14, size.Y + 14), "c", fontSize: 12);
    }
}
