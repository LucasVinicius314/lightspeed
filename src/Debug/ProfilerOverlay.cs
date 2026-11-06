using Godot;

namespace Lightspeed.Debug;

/// <summary>
/// On-screen performance readout, shown in the simple and full debug modes (F3, see DebugMode).
/// CPU time = script/process + physics + render-thread CPU work. GPU time is the measured
/// render time of the main viewport. Frame-time values are averaged over each refresh interval.
/// </summary>
public partial class ProfilerOverlay : CanvasLayer
{
    [Export] public double RefreshSeconds { get; set; } = 0.25;

    private Label _label = null!;
    private Rid _viewport;
    private double _elapsed;
    private int _samples;
    private double _processMs, _physicsMs, _renderCpuMs, _renderGpuMs;

    public override void _Ready()
    {
        Layer = 100;
        _label = new Label { Position = new Vector2(10, 10) };
        _label.AddThemeColorOverride("font_color", Colors.White);
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _label.AddThemeConstantOverride("outline_size", 6);
        AddChild(_label);

        _viewport = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);

        DebugMode.Changed += ApplyMode;
        ApplyMode(DebugMode.Current);
    }

    public override void _ExitTree() => DebugMode.Changed -= ApplyMode;

    private void ApplyMode(DebugLabelMode mode) => Visible = mode != DebugLabelMode.None;

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F3 })
            DebugMode.Cycle();
    }

    public override void _Process(double delta)
    {
        _processMs += Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
        _physicsMs += Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000;
        _renderCpuMs += RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport);
        _renderGpuMs += RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport);
        _samples++;
        _elapsed += delta;
        if (_elapsed < RefreshSeconds)
            return;

        double cpuMs = (_processMs + _physicsMs + _renderCpuMs) / _samples;
        double gpuMs = _renderGpuMs / _samples;
        double vram = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed);
        double texMem = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed);
        double bufMem = Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed);
        double ram = System.Environment.WorkingSet;
        double engineMem = Performance.GetMonitor(Performance.Monitor.MemoryStatic);
        double managedMem = System.GC.GetTotalMemory(false);

        _label.Text =
            $"Total   {Engine.GetFramesPerSecond(),6:F0} fps  {1000.0 / System.Math.Max(Engine.GetFramesPerSecond(), 1):F2} ms\n" +
            $"CPU     {Fps(cpuMs),6:F0} fps  {cpuMs:F2} ms  (proc {_processMs / _samples:F2} + phys {_physicsMs / _samples:F2} + render {_renderCpuMs / _samples:F2})\n" +
            $"GPU     {Fps(gpuMs),6:F0} fps  {gpuMs:F2} ms\n" +
            $"VRAM    {Mb(vram):F1} MB  (textures {Mb(texMem):F1}, buffers {Mb(bufMem):F1})\n" +
            $"RAM     {Mb(ram):F1} MB  (engine {Mb(engineMem):F1}, managed {Mb(managedMem):F1})";

        _elapsed = 0;
        _samples = 0;
        _processMs = _physicsMs = _renderCpuMs = _renderGpuMs = 0;
    }

    private static double Fps(double ms) => ms > 0 ? 1000.0 / ms : 0;
    private static double Mb(double bytes) => bytes / (1024.0 * 1024.0);
}
