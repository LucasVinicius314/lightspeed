using Godot;
using Lightspeed.Core;

namespace Lightspeed.Player;

/// <summary>
/// Free-fly camera. WASD to move, E/Space up, Q/Ctrl down, Shift to sprint, mouse to look.
/// The mouse is locked at start; Esc unlocks it and the next click locks it again. The player lives in the present: nothing here is delayed.
/// </summary>
public partial class FreeCamera : Camera3D
{
    [Export] public float Speed { get; set; } = 4f;
    [Export] public float SprintMultiplier { get; set; } = 3f;
    [Export] public float MouseSensitivity { get; set; } = 0.0018f;
    /// <summary>Keep the camera below c. Off by default: at low c the player would barely move.</summary>
    [Export] public bool LimitToC { get; set; }

    private float _yaw;
    private float _pitch;

    public override void _Ready()
    {
        _yaw = Rotation.Y;
        _pitch = Rotation.X;

        // Don't grab the mouse for headless runs or scripted screenshots.
        bool scripted = DisplayServer.GetName() == "headless";
        foreach (var arg in OS.GetCmdlineUserArgs())
            scripted |= arg.StartsWith("--screenshot");
        if (!scripted)
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _Input(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true } when Input.MouseMode != Input.MouseModeEnum.Captured:
                // The click that re-locks the mouse is swallowed so it doesn't also step c.
                Input.MouseMode = Input.MouseModeEnum.Captured;
                GetViewport().SetInputAsHandled();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                Input.MouseMode = Input.MouseModeEnum.Visible;
                break;
            case InputEventMouseMotion motion when Input.MouseMode == Input.MouseModeEnum.Captured:
                _yaw -= motion.Relative.X * MouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -1.5f, 1.5f);
                Rotation = new Vector3(_pitch, _yaw, 0);
                break;
        }
    }

    public override void _Process(double delta)
    {
        var dir = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) dir -= GlobalBasis.Z;
        if (Input.IsKeyPressed(Key.S)) dir += GlobalBasis.Z;
        if (Input.IsKeyPressed(Key.A)) dir -= GlobalBasis.X;
        if (Input.IsKeyPressed(Key.D)) dir += GlobalBasis.X;
        if (Input.IsKeyPressed(Key.E) || Input.IsKeyPressed(Key.Space)) dir += Vector3.Up;
        if (Input.IsKeyPressed(Key.Q) || Input.IsKeyPressed(Key.Ctrl)) dir += Vector3.Down;
        if (dir == Vector3.Zero)
            return;

        float speed = Speed * (Input.IsKeyPressed(Key.Shift) ? SprintMultiplier : 1f);
        if (LimitToC)
            speed = Mathf.Min(speed, 0.9f * SimClock.Instance.C);
        GlobalPosition += dir.Normalized() * speed * (float)delta;
    }
}
