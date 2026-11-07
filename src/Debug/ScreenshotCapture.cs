using Godot;
using Lightspeed.Core;

namespace Lightspeed.Debug;

/// <summary>
/// Dev tool for verification: run with `-- --screenshot=<file.png>` to save a frame at
/// <see cref="AtTick"/> and quit. Optional `--cam=x,y,z,yawDeg,pitchDeg` places the camera first and `--at-tick=N` picks the tick.
/// </summary>
public partial class ScreenshotCapture : Node
{
    public string Path { get; set; } = "";
    /// <summary>Ticks after the clock's starting tick.</summary>
    public long AtTick { get; set; } = 400;
    public float[]? Cam { get; set; }

    public static void AttachIfRequested(Node parent)
    {
        string? path = null;
        float[]? cam = null;
        long atTick = 400;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--screenshot="))
                path = arg["--screenshot=".Length..];
            else if (arg.StartsWith("--at-tick="))
                atTick = long.Parse(arg["--at-tick=".Length..], System.Globalization.CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--cam="))
                cam = System.Array.ConvertAll(arg["--cam=".Length..].Split(','),
                    v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
        }
        if (path != null)
            parent.AddChild(new ScreenshotCapture { Path = path, Cam = cam, AtTick = atTick });
    }

    private long _startTick;

    public override void _Ready()
    {
        _startTick = SimClock.Instance.Tick;
        SimClock.Instance.Ticked += OnTicked;
        var camera = GetViewport().GetCamera3D();
        if (Cam is { Length: 5 } && camera != null)
        {
            if (camera is Player.FreeCamera player)
            {
                player.Place(new Vector3(Cam[0], Cam[1], Cam[2]), Cam[3], Cam[4]);
                return;
            }
            camera.GlobalPosition = new Vector3(Cam[0], Cam[1], Cam[2]);
            camera.RotationDegrees = new Vector3(Cam[4], Cam[3], 0);
        }
    }

    private async void OnTicked(long tick, double time)
    {
        if (tick - _startTick != AtTick)
            return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var error = GetViewport().GetTexture().GetImage().SavePng(Path);
        GD.Print($"screenshot {Path}: {error}");
        GetTree().Quit();
    }
}
