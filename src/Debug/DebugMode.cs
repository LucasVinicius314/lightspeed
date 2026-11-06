using System;
using Godot;

namespace Lightspeed.Debug;

public enum DebugLabelMode
{
    None,
    // Left performance readout only.
    Simple,
    // Left performance readout plus the right-hand debug view (rings, delay labels, ghosts).
    Both,
}

/// <summary>
/// Which debug text is on screen. F3 cycles none, simple, both; the choice is remembered between runs
/// (user://debug_mode.cfg).
/// </summary>
public static class DebugMode
{
    private const string SettingsPath = "user://debug_mode.cfg";

    private static DebugLabelMode _current = Load();

    public static DebugLabelMode Current => _current;

    public static event Action<DebugLabelMode>? Changed;

    public static void Cycle()
    {
        _current = (DebugLabelMode)(((int)_current + 1) % 3);
        var config = new ConfigFile();
        config.SetValue("debug", "mode", (int)_current);
        config.Save(SettingsPath);
        Changed?.Invoke(_current);
    }

    public static string Describe(DebugLabelMode mode) => mode switch
    {
        DebugLabelMode.None => "off",
        DebugLabelMode.Simple => "simple",
        _ => "full",
    };

    private static DebugLabelMode Load()
    {
        var config = new ConfigFile();
        if (config.Load(SettingsPath) != Error.Ok)
            return DebugLabelMode.Both; // first run: show everything, as before
        int value = (int)config.GetValue("debug", "mode", (int)DebugLabelMode.Both);
        return Enum.IsDefined(typeof(DebugLabelMode), value) ? (DebugLabelMode)value : DebugLabelMode.Both;
    }
}
