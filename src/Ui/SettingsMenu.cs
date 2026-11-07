using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Lightspeed.Core;
using Lightspeed.Debug;
using Lightspeed.Demo;
using Lightspeed.Player;

namespace Lightspeed.Ui;

/// <summary>
/// Settings menu (Tab), one tab per section, plus the Controls tab (H) listing every keybind. Opening it frees
/// the mouse and stops the camera; every control reads and writes the same state the hotkeys use, so the two
/// stay in step. The panel has a fixed width: long values are clipped, never allowed to widen it.
/// Also owns F1 (shader / CPU solve).
/// </summary>
public partial class SettingsMenu : CanvasLayer
{
    public static bool IsOpen { get; private set; }

    private readonly List<Action> _refresh = new();
    private Control _panel = null!;
    private VBoxContainer _box = null!;
    private VBoxContainer _outer = null!;
    private TabContainer _tabs = null!;
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
        BuildControlsSection();
        BuildDebugSection();
        BuildDisplaySection();
        BuildFooter();

        _panel.Visible = false;
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--menu-tab=")) // for screenshots: which tab to show
            {
                string wanted = arg["--menu-tab=".Length..];
                for (int i = 0; i < _tabs.GetTabCount(); i++)
                    if (string.Equals(_tabs.GetChild(i).Name, wanted, StringComparison.OrdinalIgnoreCase))
                        _tabs.CurrentTab = i;
            }
            else if (arg == "--open-menu") // for screenshots
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
        else if (key.Keycode == Key.H)
        {
            // H opens the menu on the Controls tab, or closes it when that tab is already showing.
            if (IsOpen && _tabs.CurrentTab == ControlsTab())
                Toggle();
            else
            {
                if (!IsOpen)
                    Toggle();
                _tabs.CurrentTab = ControlsTab();
            }
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

    private int ControlsTab()
    {
        for (int i = 0; i < _tabs.GetTabCount(); i++)
            if (_tabs.GetChild(i).Name == "Controls")
                return i;
        return 0;
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
            v => SpeedLadder.FormatSpeed(SpeedLadder.Steps[(int)v].Value),
            "Also on the mouse wheel and the Up / Down arrow keys.");
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
        Toggle("Paused (P)",
            () => SimClock.Instance.Paused,
            v => SimClock.Instance.SetPaused(v),
            "Freezes ticks and physics. You can still fly around and watch the light settle.");
        Buttons(("Step one tick (.)", SimClock.Instance.StepTick));
        Slider("Time scale ([ and ])", Math.Log10(SimClock.MinTimeScale), Math.Log10(SimClock.MaxTimeScale), 0.01,
            () => Math.Log10(SimClock.Instance.TimeScale),
            v => SimClock.Instance.SetTimeScale((float)Math.Pow(10, v)),
            v => $"{Math.Pow(10, v):0.00}x",
            "Slows or speeds the whole simulation. The player keeps moving at normal speed. Not saved between runs.");

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

        SubSection("Throwing");
        Choice("Object (B)", Demo.Thrower.ShapeNames.ToArray(),
            () => Thrower()?.SelectedIndex ?? 0,
            i => Thrower()?.Select(i),
            "Left click throws the selected physics object along your view.");
        Slider("Throw speed", 2, 30, 1,
            () => Thrower()?.ThrowSpeed ?? 12,
            v => { if (Thrower() is { } t) t.ThrowSpeed = (float)v; },
            v => $"{v:0} m/s",
            "Capped at 0.6 c so thrown objects stay below the speed of light.");
        Buttons(("Remove thrown objects (X)", () => Thrower()?.Clear()));
    }

    private void BuildCameraSection()
    {
        Section("Camera");
        Choice("Mode (V)", new[] { "Fly", "Drone", "Walk" },
            () => (int)GameSettings.CameraMode,
            i => { GameSettings.CameraMode = (CameraMode)i; GameSettings.Commit(); },
            "Fly: free camera. Drone: FPV acro flight with a CRT video feed. Walk: gravity, jumping and collisions.");
        Toggle("Invert drone pitch",
            () => GameSettings.DroneInvertPitch,
            v => { GameSettings.DroneInvertPitch = v; GameSettings.Commit(); },
            "Off: mouse forward dives, like a flight stick. On: mouse forward climbs.");
        Toggle("Drone CRT effect",
            () => GameSettings.DroneCrt,
            v => { GameSettings.DroneCrt = v; GameSettings.Commit(); },
            "Scanlines, curvature, noise and an on-screen display while flying the drone.");
        Slider("Field of view", 40, 120, 1,
            () => GameSettings.Fov,
            v => { GameSettings.Fov = (float)v; GameSettings.Commit(); },
            v => $"{v:0}°");
        Slider("Mouse sensitivity", 0.0005, 0.006, 0.0001,
            () => GameSettings.MouseSensitivity,
            v => { GameSettings.MouseSensitivity = (float)v; GameSettings.Commit(); },
            v => $"{v * 1000:0.0}");
        Slider("Move speed", 1, 30, 0.5,
            () => GameSettings.FlySpeed,
            v => { GameSettings.FlySpeed = (float)v; GameSettings.Commit(); },
            v => $"{v:0.0} m/s",
            "Fly and walk speed. The FPV drone is thrust-driven and ignores it.");
        Toggle("Keep camera below c",
            () => GameSettings.LimitToC,
            v => { GameSettings.LimitToC = v; GameSettings.Commit(); },
            "Cap the camera at 0.9c, like everything else. At low c you would barely move.");
    }

    private void BuildControlsSection()
    {
        Section("Controls");
        foreach (var group in Keybinds.Groups)
        {
            SubSection(group.Title);
            var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 12);
            grid.AddThemeConstantOverride("v_separation", 5);
            foreach (var bind in group.Binds)
            {
                var keys = new PanelContainer { CustomMinimumSize = new Vector2(190, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
                keys.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Control, 5, 8, 3, new Color(1, 1, 1, 0.12f)));
                keys.AddChild(new Label { Text = bind.Keys, ClipText = true, HorizontalAlignment = HorizontalAlignment.Center });
                grid.AddChild(keys);
                grid.AddChild(new Label
                {
                    Text = bind.Action,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    CustomMinimumSize = new Vector2(60, 0),
                });
            }
            _box.AddChild(grid);
        }
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
        _outer.AddChild(new HSeparator());
        _box = _outer;
        Buttons(("Reset everything", ResetAll), ("Close (Tab)", () => Toggle()));
        RefreshAll();
    }

    private void ResetAll()
    {
        GameSettings.Reset();
        SimClock.Instance.SetStepIndex(SpeedLadder.Nearest(5f));
        SimClock.Instance.SetPaused(false);
        SimClock.Instance.SetTimeScale(1);
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

    private Thrower? Thrower() => GetTree().GetFirstNodeInGroup("thrower") as Thrower;

    private Spawner? Spawner() => GetTree().GetFirstNodeInGroup("spawner") as Spawner;

    // ------------------------------------------------------------------ layout helpers

    private const float PanelWidth = 480;

    private void BuildPanel()
    {
        var root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // Fixed width: the offsets set it, and ClipContents plus clipped labels keep content from pushing it wider.
        var panel = new PanelContainer { Theme = UiTheme.Create(), ClipContents = true };
        panel.SetAnchorsPreset(Control.LayoutPreset.LeftWide);
        panel.OffsetLeft = 12;
        panel.OffsetTop = 12;
        panel.OffsetRight = 12 + PanelWidth;
        panel.OffsetBottom = -12;
        panel.GrowHorizontal = Control.GrowDirection.End;
        panel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.PanelColor, 10, 14, 12, new Color(1, 1, 1, 0.08f)));
        root.AddChild(panel);
        _panel = panel;

        _outer = new VBoxContainer();
        _outer.AddThemeConstantOverride("separation", 6);
        panel.AddChild(_outer);

        var header = new HBoxContainer();
        var title = new Label { Text = "Settings" };
        title.AddThemeFontSizeOverride("font_size", 20);
        header.AddChild(title);
        var hint = new Label
        {
            Text = "Tab / Esc to close",
            HorizontalAlignment = HorizontalAlignment.Right,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipText = true,
        };
        hint.AddThemeColorOverride("font_color", UiTheme.Dim);
        header.AddChild(hint);
        _outer.AddChild(header);

        _tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.None };
        _tabs.AddThemeFontSizeOverride("font_size", 13);
        _outer.AddChild(_tabs);
    }

    /// <summary>Starts a new tab; later controls go into it.</summary>
    private void Section(string text)
    {
        var scroll = new ScrollContainer
        {
            Name = text,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _tabs.AddChild(scroll);
        _box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _box.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_box);
        _box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
    }

    /// <summary>A heading inside the current tab.</summary>
    private void SubSection(string text)
    {
        var label = new Label { Text = text.ToUpperInvariant() };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", UiTheme.Accent);
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
        var name = new Label { Text = text, CustomMinimumSize = new Vector2(150, 0), ClipText = true };
        var slider = new HSlider
        {
            MinValue = min, MaxValue = max, Step = step,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(80, 0),
            FocusMode = Control.FocusModeEnum.None,
        };
        // Fixed width, clipped: a long value (the speed of light) must never widen the menu.
        var value = new Label { CustomMinimumSize = new Vector2(125, 0), ClipText = true, HorizontalAlignment = HorizontalAlignment.Right };
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
        row.AddChild(new Label { Text = text, CustomMinimumSize = new Vector2(150, 0), ClipText = true });
        var option = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FocusMode = Control.FocusModeEnum.None,
            FitToLongestItem = false,
            ClipText = true,
            CustomMinimumSize = new Vector2(60, 0),
        };
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
