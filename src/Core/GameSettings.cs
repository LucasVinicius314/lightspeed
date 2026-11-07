using System;
using System.Globalization;
using System.Linq;
using Godot;

namespace Lightspeed.Core;

/// <summary>How a part that outruns its own light is drawn.</summary>
public enum FastPartRendering
{
    /// <summary>One image per vertex: the solver's single answer. Tears fast parts.</summary>
    SingleImage = 0,
    /// <summary>Light Echoes: the mesh is drawn once per image, each vertex linked to its image of the same rank.</summary>
    Echoes = 1,
    /// <summary>Exact rays: every pixel traces its own light ray back to the moving shape. Primitives only; others use Echoes.</summary>
    Rays = 2,
}

/// <summary>How the player moves. Cycled with V.</summary>
public enum CameraMode
{
    /// <summary>Free-fly camera: instant response, no collision.</summary>
    Fly = 0,
    /// <summary>FPV drone: momentum, banking into turns, vibration and a CRT-style video feed.</summary>
    Drone = 1,
    /// <summary>Character controller: gravity, jumping, collides with the world and pushes physics objects.</summary>
    Walk = 2,
}

/// <summary>
/// User-adjustable settings that survive between runs (user://settings.cfg). Change a value, then call
/// <see cref="Commit"/> to save it and tell listeners. Scripted runs (`--screenshot`, `--no-settings`)
/// ignore the saved file and never write it, so verification is repeatable.
/// The speed of light, time scale and the debug toggles are not here: they live with their owners.
/// </summary>
public static class GameSettings
{
    public const float DefaultFov = 85f;
    public const float DefaultMouseSensitivity = 0.0018f;
    public const float DefaultFlySpeed = 4f;
    public const float DefaultMaxEdgeLength = 0.2f;
    public const int DefaultMaxImages = 8;
    public const float DefaultHistorySeconds = 30f;

    private const string Path = "user://settings.cfg";
    private static readonly bool Persist;

    /// <summary>
    /// A part that outruns its own light is seen at several moments at once. This picks how those images are
    /// drawn; parts slower than light always use the single-image path.
    /// </summary>
    public static FastPartRendering FastParts { get; set; }

    /// <summary>Most echoes drawn per part in Light Echoes mode; each is one more draw of its mesh.</summary>
    public static int MaxImages { get; set; }

    /// <summary>Longest mesh edge in metres. Shorter lets shapes warp more smoothly but costs vertices.</summary>
    public static float MaxEdgeLength { get; set; }

    /// <summary>
    /// How many seconds of motion every object remembers. Light older than this is gone, so it caps how far away
    /// you can see an object (c times this). Longer costs memory; the history texture resizes to match.
    /// </summary>
    public static float HistorySeconds { get; set; }

    /// <summary>Use the CPU reference solve (one image, object centre) instead of the shader.</summary>
    public static bool CpuSolver { get; set; }

    public static float Fov { get; set; }
    public static float MouseSensitivity { get; set; }
    public static float FlySpeed { get; set; }
    public static bool LimitToC { get; set; }

    /// <summary>Current movement mode. Not saved: every run starts flying (`-- --mode=drone|walk` overrides).</summary>
    public static CameraMode CameraMode { get; set; }

    /// <summary>Show the CRT video-feed effect and on-screen display in drone mode.</summary>
    public static bool DroneCrt { get; set; }

    /// <summary>Drone pitch from the mouse. Off (the flight-sim norm): push forward to dive. On: push forward to climb.</summary>
    public static bool DroneInvertPitch { get; set; }
    public static bool Fxaa { get; set; }
    public static bool Vsync { get; set; }
    public static bool Fullscreen { get; set; }

    public static event Action? Changed;

    static GameSettings()
    {
        var args = OS.GetCmdlineUserArgs();
        Persist = !args.Any(a => a.StartsWith("--screenshot") || a == "--no-settings");
        ResetValues();
        if (Persist)
            Load();

        foreach (var arg in args)
        {
            if (arg == "--cpu")
                CpuSolver = true;
            else if (arg == "--no-echoes")
                FastParts = FastPartRendering.SingleImage;
            else if (arg == "--echoes")
                FastParts = FastPartRendering.Echoes;
            else if (arg == "--rays")
                FastParts = FastPartRendering.Rays;
            else if (arg.StartsWith("--mode=") && Enum.TryParse<CameraMode>(arg["--mode=".Length..], true, out var mode))
                CameraMode = mode;
            else if (arg.StartsWith("--history="))
                HistorySeconds = float.Parse(arg["--history=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--max-edge="))
                MaxEdgeLength = float.Parse(arg["--max-edge=".Length..], CultureInfo.InvariantCulture);
        }
    }

    public static void Reset()
    {
        ResetValues();
        Commit();
    }

    /// <summary>Saves and notifies listeners.</summary>
    public static void Commit()
    {
        if (Persist)
            Save();
        Changed?.Invoke();
    }

    private static void ResetValues()
    {
        FastParts = FastPartRendering.Rays;
        MaxImages = DefaultMaxImages;
        MaxEdgeLength = DefaultMaxEdgeLength;
        HistorySeconds = DefaultHistorySeconds;
        CpuSolver = false;
        Fov = DefaultFov;
        MouseSensitivity = DefaultMouseSensitivity;
        FlySpeed = DefaultFlySpeed;
        LimitToC = false;
        CameraMode = CameraMode.Fly;
        DroneCrt = true;
        DroneInvertPitch = false;
        Fxaa = true;
        Vsync = true;
        Fullscreen = false;
    }

    private static void Load()
    {
        var config = new ConfigFile();
        if (config.Load(Path) != Error.Ok)
            return;
        FastParts = (FastPartRendering)(int)config.GetValue("light", "fast_parts", (int)FastParts);
        MaxImages = (int)config.GetValue("light", "max_images", MaxImages);
        MaxEdgeLength = (float)(double)config.GetValue("light", "max_edge", MaxEdgeLength);
        HistorySeconds = (float)(double)config.GetValue("light", "history_seconds", (double)HistorySeconds);
        CpuSolver = (bool)config.GetValue("light", "cpu_solver", CpuSolver);
        Fov = (float)(double)config.GetValue("camera", "fov", Fov);
        MouseSensitivity = (float)(double)config.GetValue("camera", "sensitivity", MouseSensitivity);
        FlySpeed = (float)(double)config.GetValue("camera", "speed", FlySpeed);
        LimitToC = (bool)config.GetValue("camera", "limit_to_c", LimitToC);
        DroneCrt = (bool)config.GetValue("camera", "drone_crt", DroneCrt);
        DroneInvertPitch = (bool)config.GetValue("camera", "drone_invert_pitch", DroneInvertPitch);
        Fxaa = (bool)config.GetValue("display", "fxaa", Fxaa);
        Vsync = (bool)config.GetValue("display", "vsync", Vsync);
        Fullscreen = (bool)config.GetValue("display", "fullscreen", Fullscreen);
    }

    private static void Save()
    {
        var config = new ConfigFile();
        config.SetValue("light", "fast_parts", (int)FastParts);
        config.SetValue("light", "max_images", MaxImages);
        config.SetValue("light", "max_edge", (double)MaxEdgeLength);
        config.SetValue("light", "history_seconds", (double)HistorySeconds);
        config.SetValue("light", "cpu_solver", CpuSolver);
        config.SetValue("camera", "fov", (double)Fov);
        config.SetValue("camera", "sensitivity", (double)MouseSensitivity);
        config.SetValue("camera", "speed", (double)FlySpeed);
        config.SetValue("camera", "limit_to_c", LimitToC);
        config.SetValue("camera", "drone_crt", DroneCrt);
        config.SetValue("camera", "drone_invert_pitch", DroneInvertPitch);
        config.SetValue("display", "fxaa", Fxaa);
        config.SetValue("display", "vsync", Vsync);
        config.SetValue("display", "fullscreen", Fullscreen);
        config.Save(Path);
    }
}
