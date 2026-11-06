using System;
using System.Collections.Generic;
using Godot;
using Lightspeed.Core;
using Lightspeed.Debug;
using Lightspeed.Demo;
using Lightspeed.Player;

namespace Lightspeed.Ui;

/// <summary>
/// Settings menu (Tab). Opening it frees the mouse and stops the camera; every control reads and writes the
/// same state the hotkeys use, so the two stay in step. Also owns F1 (shader / CPU solve).
/// </summary>
public partial class SettingsMenu : CanvasLayer
{
    public static bool IsOpen { get; private set; }

    private readonly List<Action> _refresh = new();
    private Control _panel = null!;
    private VBoxContainer _box = null!;
    private double _sinceRefresh;
    private bool _scripted;

    public override void _Ready()
    {
        Layer = 120;
        _scripted = DisplayServer.GetName() == "headless";
        foreach (var arg in OS.GetCmdlineUserArgs())
            _scripted |= arg.StartsWith("--screenshot");

        BuildPanel();
        BuildLightSection();
        BuildSimulationSection();
        BuildCameraSection();
        BuildDebugSection();
        BuildDisplaySection();
        BuildFooter();

        _panel.Visible = false;
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg == "--open-menu") // for screenshots
            {
                IsOpen = true;
                _panel.Visible = true;
                RefreshAll();
            }
        GameSettings.Changed += OnSettingsChanged;
        ApplyDisplay(startup: true);
        ApplyCamera();
    }

    public override void _ExitTree()
    {
        GameSettings.Changed -= OnSettingsChanged;
        IsOpen = false;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (key.Keycode == Key.Tab || (key.Keycode == Key.Escape && IsOpen))
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F1 })
        {
            GameSettings.CpuSolver = !GameSettings.CpuSolver;
            GameSettings.Commit();
        }
    }

    public override void _Process(double delta)
    {
        if (!IsOpen)
            return;
        _sinceRefresh += delta;
        if (_sinceRefresh >= 0.25)
            RefreshAll();
    }

    private void Toggle()
    {
        IsOpen = !IsOpen;
        _panel.Visible = IsOpen;
        if (IsOpen)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            RefreshAll();
        }
        else if (!_scripted)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    private void RefreshAll()
    {
        _sinceRefresh = 0;
        foreach (var refresh in _refresh)
            refresh();
    }

    // ------------------------------------------------------------------ sections

    private void BuildLightSection()
    {
        Section("Light");
        Slider("Speed of light", 0, SpeedLadder.Steps.Count - 1, 1,
            () => SimClock.Instance.StepIndex,
            v => SimClock.Instance.SetStepIndex((int)v),
            v =>
            {
                var step = SpeedLadder.Steps[(int)v];
                return SpeedLadder.FormatSpeed(step.Value) + (step.Name != null ? $" ({step.Name})" : "");
            },
            "Also on the mouse wheel.");
        Choice("Fast parts",
            new[] { "One image (tears)", "Light Echoes", "Exact rays" },
            () => (int)GameSettings.FastParts,
            i => { GameSettings.FastParts = (FastPartRendering)i; GameSettings.Commit(); },
            "How a part that outruns its own light is drawn. One image: the old single answer, which tears. " +
            "Light Echoes: the mesh is drawn once per image, with each vertex linked to its image of the same rank " +
            "(breaks down when close to the part). Exact rays: every pixel traces its own light ray back to the " +
            "moving shape (boxes, spheres and cylinders; other meshes use Light Echoes).");
        Slider("Max echoes per part", 2, 16, 1,
            () => GameSettings.MaxImages,
            v => { GameSettings.MaxImages = (int)v; GameSettings.Commit(); },
            v => ((int)v).ToString(),
            "Light Echoes only: upper limit on images drawn per part. Each is one more draw of the mesh.");
        Slider("Mesh edge length", -2, 0, 0.05,
            () => Math.Log10(GameSettings.MaxEdgeLength),
            v => { GameSettings.MaxEdgeLength = (float)Math.Pow(10, v); GameSettings.Commit(); },
            v => SpeedLadder.FormatDistance(Math.Pow(10, v)),
            "Longest edge allowed in a delayed mesh. Smaller lets shapes warp more smoothly but costs vertices.");
        Toggle("CPU reference solve (F1)",
            () => GameSettings.CpuSolver,
            v => { GameSettings.CpuSolver = v; GameSettings.Commit(); },
            "Solve each object's centre on the CPU: one image, no per-vertex warping. For checking the shader.");
    }

    private void BuildSimulationSection()
    {
        Section("Simulation");
        Slider("Time scale", 0.05, 2, 0.05,
            () => Engine.TimeScale,
            v => Engine.TimeScale = v,
            v => $"{v:0.00}x",
            "Slows or speeds the whole simulation. Not saved between runs.");

        Toggle("Spawn balls over time",
            () => Spawner()?.TimedSpawning ?? false,
            v => { if (Spawner() is { } s) s.TimedSpawning = v; },
            "New balls appear only once their light reaches you, and vanish after their last light passes.");
        Slider("Spawn every", 0.5, 10, 0.5,
            () => Spawner()?.Interval ?? 2,
            v => { if (Spawner() is { } s) s.Interval = v; },
            v => $"{v:0.0} s");
        Slider("Ball lifetime", 1, 30, 1,
            () => Spawner()?.Lifetime ?? 8,
            v => { if (Spawner() is { } s) s.Lifetime = v; },
            v => $"{v:0} s");
        Buttons(("Spawn 25 balls", () => Spawner()?.SpawnBurst(25)),
                ("Remove spawned balls", () => Spawner()?.DespawnAll()));
    }

    private void BuildCameraSection()
    {
        Section("Camera");
        Slider("Field of view", 40, 120, 1,
            () => GameSettings.Fov,
            v => { GameSettings.Fov = (float)v; GameSettings.Commit(); },
            v => $"{v:0}°");
        Slider("Mouse sensitivity", 0.0005, 0.006, 0.0001,
            () => GameSettings.MouseSensitivity,
            v => { GameSettings.MouseSensitivity = (float)v; GameSettings.Commit(); },
            v => $"{v * 1000:0.0}");
        Slider("Fly speed", 1, 30, 0.5,
            () => GameSettings.FlySpeed,
            v => { GameSettings.FlySpeed = (float)v; GameSettings.Commit(); },
            v => $"{v:0.0} m/s");
        Toggle("Keep camera below c",
            () => GameSettings.LimitToC,
            v => { GameSettings.LimitToC = v; GameSettings.Commit(); },
            "Cap the camera at 0.9c, like everything else. At low c you would barely move.");
    }

    private void BuildDebugSection()
    {
        Section("Debug");
        Choice("Debug text (F3)", new[] { "Off", "Simple", "Full" },
            () => (int)DebugMode.Current,
            i => DebugMode.Set((DebugLabelMode)i));
        Toggle("True-position ghosts (1)",
            () => DebugView.Instance?.ShowGhosts ?? false,
            v => { if (DebugView.Instance is { } d) d.ShowGhosts = v; });
        Toggle("Delay rings (2)",
            () => DebugView.Instance?.ShowRings ?? false,
            v => { if (DebugView.Instance is { } d) d.ShowRings = v; },
            "Rings on the ground around the camera. Something on the ring labelled T is seen as it was T ago. Full debug text only.");
        Toggle("Delay labels and lines (3)",
            () => DebugView.Instance?.ShowLabels ?? false,
            v => { if (DebugView.Instance is { } d) d.ShowLabels = v; },
            "Full debug text only.");
    }

    private void BuildDisplaySection()
    {
        Section("Display");
        Toggle("Anti-aliasing (FXAA)", () => GameSettings.Fxaa, v => { GameSettings.Fxaa = v; GameSettings.Commit(); });
        Toggle("VSync", () => GameSettings.Vsync, v => { GameSettings.Vsync = v; GameSettings.Commit(); });
        Toggle("Fullscreen", () => GameSettings.Fullscreen, v => { GameSettings.Fullscreen = v; GameSettings.Commit(); });
    }

    private void BuildFooter()
    {
        _box.AddChild(new HSeparator());
        Buttons(("Reset everything", ResetAll), ("Close (Tab)", Toggle));
        RefreshAll();
    }

    private void ResetAll()
    {
        GameSettings.Reset();
        SimClock.Instance.SetStepIndex(SpeedLadder.Nearest(5f));
        Engine.TimeScale = 1;
        DebugMode.Set(DebugLabelMode.Both);
        if (DebugView.Instance is { } d)
        {
            d.ShowGhosts = true;
            d.ShowRings = true;
            d.ShowLabels = true;
        }
        if (Spawner() is { } s)
        {
            s.TimedSpawning = true;
            s.Interval = 2;
            s.Lifetime = 8;
        }
        RefreshAll();
    }

    // ------------------------------------------------------------------ applying settings

    private void OnSettingsChanged()
    {
        ApplyDisplay(startup: false);
        ApplyCamera();
        RefreshAll();
    }

    private void ApplyCamera()
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null)
            return;
        camera.Fov = GameSettings.Fov;
        if (camera is FreeCamera free)
        {
            free.MouseSensitivity = GameSettings.MouseSensitivity;
            free.Speed = GameSettings.FlySpeed;
            free.LimitToC = GameSettings.LimitToC;
        }
    }

    private void ApplyDisplay(bool startup)
    {
        GetViewport().ScreenSpaceAA = GameSettings.Fxaa
            ? Viewport.ScreenSpaceAAEnum.Fxaa
            : Viewport.ScreenSpaceAAEnum.Disabled;

        var vsync = GameSettings.Vsync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled;
        if (DisplayServer.WindowGetVsyncMode() != vsync)
            DisplayServer.WindowSetVsyncMode(vsync);

        // Only touch the window mode when it differs, so a maximized window is left alone.
        bool isFullscreen = DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen;
        if (GameSettings.Fullscreen != isFullscreen && !_scripted)
            DisplayServer.WindowSetMode(GameSettings.Fullscreen
                ? DisplayServer.WindowMode.Fullscreen
                : DisplayServer.WindowMode.Windowed);
    }

    private Spawner? Spawner() => GetTree().GetFirstNodeInGroup("spawner") as Spawner;

    // ------------------------------------------------------------------ layout helpers

    private void BuildPanel()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        panel.OffsetLeft = 12;
        panel.OffsetTop = 12;
        panel.OffsetRight = 440;
        panel.OffsetBottom = -12;
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.08f, 0.9f),
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });
        root.AddChild(panel);
        _panel = panel;

        var outer = new VBoxContainer();
        panel.AddChild(outer);
        var title = new Label { Text = "Settings        Tab / Esc to close" };
        title.AddThemeFontSizeOverride("font_size", 18);
        outer.AddChild(title);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        outer.AddChild(scroll);
        _box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_box);
    }

    private void Section(string text)
    {
        var label = new Label { Text = text.ToUpperInvariant() };
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", new Color(1f, 0.65f, 0.3f));
        _box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
        _box.AddChild(label);
        _box.AddChild(new HSeparator());
    }

    private void Toggle(string text, Func<bool> get, Action<bool> set, string? tip = null)
    {
        var button = new CheckButton { Text = text, TooltipText = tip ?? "", FocusMode = Control.FocusModeEnum.None };
        button.Toggled += v => set(v);
        _box.AddChild(button);
        _refresh.Add(() => button.SetPressedNoSignal(get()));
    }

    private void Slider(string text, double min, double max, double step,
        Func<double> get, Action<double> set, Func<double, string> format, string? tip = null)
    {
        var row = new HBoxContainer { TooltipText = tip ?? "" };
        var name = new Label { Text = text, CustomMinimumSize = new Vector2(150, 0) };
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(120, 0),
            FocusMode = Control.FocusModeEnum.None,
        };
        var value = new Label { CustomMinimumSize = new Vector2(120, 0), HorizontalAlignment = HorizontalAlignment.Right };
        row.AddChild(name);
        row.AddChild(slider);
        row.AddChild(value);
        _box.AddChild(row);

        slider.ValueChanged += v =>
        {
            set(v);
            value.Text = format(v);
        };
        _refresh.Add(() =>
        {
            double current = get();
            slider.SetValueNoSignal(current);
            value.Text = format(slider.Value);
        });
    }

    private void Choice(string text, string[] items, Func<int> get, Action<int> set, string? tip = null)
    {
        var row = new HBoxContainer { TooltipText = tip ?? "" };
        row.AddChild(new Label { Text = text, CustomMinimumSize = new Vector2(150, 0) });
        var option = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        foreach (var item in items)
            option.AddItem(item);
        option.ItemSelected += i => set((int)i);
        row.AddChild(option);
        _box.AddChild(row);
        _refresh.Add(() => option.Select(get()));
    }

    private void Buttons(params (string Text, Action Action)[] buttons)
    {
        var row = new HBoxContainer();
        foreach (var (text, action) in buttons)
        {
            var button = new Button { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
            button.Pressed += action;
            row.AddChild(button);
        }
        _box.AddChild(row);
    }
}
