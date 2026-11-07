using Godot;
using Lightspeed.Core;
using Lightspeed.Ui;

namespace Lightspeed.Player;

/// <summary>
/// The player. Three modes (V cycles them, see <see cref="GameSettings.CameraMode"/>):
/// Fly (WASD, Space up, C down, instant), Drone (an FPV quadcopter in acro mode, shown through a CRT video
/// feed) and Walk (a CharacterBody3D with gravity, jumping and collisions).
/// The drone has a full 3D attitude, thrust along its own up axis, gravity and drag. W/S raise and lower the
/// throttle (instant: W climbs, S drops, no key holds the 20% base that keeps its height, like a Battlefield helicopter), A/D yaw, mouse Y pitches and mouse X rolls; on a
/// gamepad the left stick is throttle and yaw and the right stick pitch and roll. Stick input commands a
/// rotation rate, and nothing levels the drone for you.
/// The mouse is locked at start; Esc unlocks it and the next click locks it again. The player lives in the
/// present: nothing here is delayed. Movement uses wall-clock frame time, so the player keeps moving while
/// the simulation is slowed; only Walk is tied to physics ticks, so it stops with the world.
/// </summary>
public partial class FreeCamera : Camera3D
{
    [Export] public float Speed { get; set; } = 4f;
    [Export] public float SprintMultiplier { get; set; } = 3f;
    [Export] public float MouseSensitivity { get; set; } = 0.0018f;
    /// <summary>Keep the camera below c. Off by default: at low c the player would barely move.</summary>
    [Export] public bool LimitToC { get; set; }

    private const float EyeHeight = 1.65f;
    private const float BodyHeight = 1.8f;
    private const float BodyRadius = 0.35f;
    private const float WalkSprint = 1.8f;
    private const float JumpSpeed = 4.8f;
    private const float PushImpulse = 2f;
    private const float GroundTop = 0.1f;

    // FPV drone. Thrust is a multiple of gravity, so hover sits at 1 / ThrustToWeight of full throttle.
    private const float ThrustToWeight = 5f;
    private const float KeyThrottleUp = 0.55f;    // keyboard throttle while W is held (instant)
    private const float KeyThrottleDown = 0.0f;   // while S is held; with neither it is HoverThrottle (20%)
    private const float DroneDrag = 0.12f;        // linear, 1/s
    private const float DroneDragQuadratic = 0.010f; // per metre
    private const float CameraTilt = 0.436f;      // FPV cameras are tilted up (25 degrees) so forward flight looks level
    private const float DroneFloor = 0.15f;
    private const float KeyYawRate = 3.5f;        // rad/s with A or D held
    private const float PadRollPitchRate = 9f;    // rad/s at full right-stick deflection
    private const float PadYawRate = 4.5f;
    private const float MouseRateGain = 2f;       // mouse counts as this many times the look sensitivity
    private const float MaxRate = 30f;            // rad/s
    private const float RateResponse = 26f;       // 1/s, how quickly the rates follow the sticks
    private const float StickDeadzone = 0.08f;

    public CameraMode Mode { get; private set; } = CameraMode.Fly;

    /// <summary>Velocity in m/s, for the HUD.</summary>
    public Vector3 Velocity => Mode == CameraMode.Walk && _walker != null ? _walker.Velocity : _velocity;

    /// <summary>Drone throttle, 0 to 1.</summary>
    public float Throttle => _throttle;

    /// <summary>Throttle at which the drone hovers.</summary>
    public static float HoverThrottle => 1f / ThrustToWeight;

    /// <summary>True when standing on something (Walk only).</summary>
    public bool Grounded => Mode == CameraMode.Walk && _walker != null && _walker.IsOnFloor();

    private float _yaw;
    private float _pitch;
    private Vector3 _velocity;
    private Basis _attitude = Basis.Identity;   // drone orientation: X right, Y up, -Z forward
    private Vector3 _rates;                     // body rates in rad/s: pitch up, roll right, yaw right
    private float _throttle = 1f / ThrustToWeight;
    private Vector2 _mouseDelta;
    private double _lastFrame;

    private CharacterBody3D? _walker;
    private Vector3 _walkPrev;
    private Vector3 _walkCur;
    private readonly float _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8);

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always; // the player keeps moving while the simulation is paused
        _yaw = Rotation.Y;
        _pitch = Rotation.X;
        _lastFrame = Time.GetTicksUsec() / 1e6;

        // Don't grab the mouse for headless runs or scripted screenshots.
        bool scripted = DisplayServer.GetName() == "headless";
        foreach (var arg in OS.GetCmdlineUserArgs())
            scripted |= arg.StartsWith("--screenshot");
        if (!scripted)
            Input.MouseMode = Input.MouseModeEnum.Captured;

        _walker = new CharacterBody3D { Name = "Walker" };
        _walker.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = BodyRadius, Height = BodyHeight },
            Position = new Vector3(0, BodyHeight / 2, 0),
        });
        GetParent().CallDeferred(Node.MethodName.AddChild, _walker);

        GameSettings.Changed += OnSettingsChanged;
        EnterMode(GameSettings.CameraMode);
    }

    public override void _ExitTree() => GameSettings.Changed -= OnSettingsChanged;

    /// <summary>Puts the player somewhere looking a given way (for scripted runs).</summary>
    public void Place(Vector3 position, float yawDegrees, float pitchDegrees)
    {
        Position = position;
        _yaw = Mathf.DegToRad(yawDegrees);
        _pitch = Mathf.DegToRad(pitchDegrees);
        EnterMode(Mode);
    }

    private void OnSettingsChanged()
    {
        if (GameSettings.CameraMode != Mode)
            EnterMode(GameSettings.CameraMode);
    }

    private void EnterMode(CameraMode mode)
    {
        if (Mode == CameraMode.Drone && mode != CameraMode.Drone)
        {
            // Take the camera's heading and pitch with you; the drone's roll is dropped.
            var euler = GlobalBasis.GetEuler();
            _yaw = euler.Y;
            _pitch = Mathf.Clamp(euler.X, -1.5f, 1.5f);
        }

        Mode = mode;
        _velocity = Vector3.Zero;
        if (mode == CameraMode.Walk && _walker != null)
        {
            var feet = new Vector3(Position.X, Mathf.Max(Position.Y - EyeHeight, GroundTop), Position.Z);
            _walker.Position = feet;
            _walker.Velocity = Vector3.Zero;
            _walkPrev = _walkCur = feet;
        }
        if (mode == CameraMode.Drone)
        {
            // Start level, facing the way you were looking, hovering.
            _attitude = Basis.FromEuler(new Vector3(0, _yaw, 0));
            _rates = Vector3.Zero;
            _throttle = HoverThrottle;
            _mouseDelta = Vector2.Zero;
            GlobalBasis = _attitude * new Basis(Vector3.Right, CameraTilt);
        }
        else
            Rotation = new Vector3(_pitch, _yaw, 0);
    }

    public override void _Input(InputEvent @event)
    {
        if (SettingsMenu.IsOpen)
            return;
        switch (@event)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } when Input.MouseMode != Input.MouseModeEnum.Captured:
                // The click that re-locks the mouse is swallowed so it doesn't also throw something.
                Input.MouseMode = Input.MouseModeEnum.Captured;
                GetViewport().SetInputAsHandled();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                Input.MouseMode = Input.MouseModeEnum.Visible;
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.V }:
                GameSettings.CameraMode = (CameraMode)(((int)GameSettings.CameraMode + 1) % 3);
                GameSettings.Commit();
                break;
            case InputEventMouseMotion motion when Input.MouseMode == Input.MouseModeEnum.Captured:
                if (Mode == CameraMode.Drone)
                {
                    _mouseDelta += motion.Relative; // the drone turns this into roll and pitch rates
                    break;
                }
                _yaw -= motion.Relative.X * MouseSensitivity;
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -1.5f, 1.5f);
                Rotation = new Vector3(_pitch, _yaw, 0);
                break;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Mode != CameraMode.Walk || _walker == null || !_walker.IsInsideTree() || GetTree().Paused)
            return;

        var input = ReadInput();
        float speed = Speed * (Input.IsKeyPressed(Key.Shift) ? WalkSprint : 1f);
        if (LimitToC)
            speed = Mathf.Min(speed, 0.9f * SimClock.Instance.C);

        var forward = new Vector3(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
        var right = new Vector3(Mathf.Cos(_yaw), 0, -Mathf.Sin(_yaw));
        var wish = (right * input.X + forward * -input.Z);
        if (wish.LengthSquared() > 1)
            wish = wish.Normalized();
        wish *= speed;

        float dt = (float)delta;
        var v = _walker.Velocity;
        bool onFloor = _walker.IsOnFloor();
        float accel = onFloor ? 60f : 14f;
        v.X = Mathf.MoveToward(v.X, wish.X, accel * dt);
        v.Z = Mathf.MoveToward(v.Z, wish.Z, accel * dt);
        if (onFloor)
            v.Y = input.Y > 0 ? JumpSpeed : -0.5f;
        else
            v.Y -= _gravity * dt;

        _walker.Velocity = v;
        _walkPrev = _walker.Position;
        _walker.MoveAndSlide();
        _walkCur = _walker.Position;

        for (int i = 0; i < _walker.GetSlideCollisionCount(); i++)
        {
            var hit = _walker.GetSlideCollision(i);
            if (hit.GetCollider() is RigidBody3D body)
                body.ApplyImpulse(-hit.GetNormal() * PushImpulse, hit.GetPosition() - body.GlobalPosition);
        }
    }

    public override void _Process(double delta)
    {
        double now = Time.GetTicksUsec() / 1e6;
        float dt = (float)Mathf.Min(now - _lastFrame, 0.1);
        _lastFrame = now;

        switch (Mode)
        {
            case CameraMode.Walk:
                ProcessWalk();
                break;
            case CameraMode.Drone:
                ProcessDrone(dt, now);
                break;
            default:
                ProcessFly(dt);
                break;
        }
    }

    /// <summary>X right, Y up (jump counts as +1), Z back; zero while the menu is open.</summary>
    private static Vector3 ReadInput()
    {
        if (SettingsMenu.IsOpen)
            return Vector3.Zero;
        var dir = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) dir.Z -= 1;
        if (Input.IsKeyPressed(Key.S)) dir.Z += 1;
        if (Input.IsKeyPressed(Key.A)) dir.X -= 1;
        if (Input.IsKeyPressed(Key.D)) dir.X += 1;
        if (Input.IsKeyPressed(Key.Space)) dir.Y += 1;
        if (Input.IsKeyPressed(Key.C)) dir.Y -= 1;
        return dir;
    }

    private float Cap(float speed) => LimitToC ? Mathf.Min(speed, 0.9f * SimClock.Instance.C) : speed;

    private void ProcessFly(float dt)
    {
        var input = ReadInput();
        var dir = GlobalBasis.X * input.X + GlobalBasis.Z * input.Z + Vector3.Up * input.Y;
        if (dir == Vector3.Zero)
        {
            _velocity = Vector3.Zero;
            return;
        }
        float speed = Cap(Speed * (Input.IsKeyPressed(Key.Shift) ? SprintMultiplier : 1f));
        _velocity = dir.Normalized() * speed;
        GlobalPosition += _velocity * dt;
    }

    private static float Stick(int device, JoyAxis axis)
    {
        float v = Input.GetJoyAxis(device, axis);
        if (Mathf.Abs(v) < StickDeadzone)
            return 0;
        v = Mathf.Sign(v) * (Mathf.Abs(v) - StickDeadzone) / (1f - StickDeadzone);
        return 0.65f * v * v * v + 0.35f * v; // a little expo: fine control near the centre, full rate at the edge
    }

    private void ProcessDrone(float dt, double now)
    {
        dt = Mathf.Max(dt, 1e-4f);
        bool menu = SettingsMenu.IsOpen;
        var mouse = menu ? Vector2.Zero : _mouseDelta;
        _mouseDelta = Vector2.Zero;

        float throttleKeys = 0, yawKeys = 0;
        if (!menu)
        {
            if (Input.IsKeyPressed(Key.W)) throttleKeys += 1;
            if (Input.IsKeyPressed(Key.S)) throttleKeys -= 1;
            if (Input.IsKeyPressed(Key.D)) yawKeys += 1;
            if (Input.IsKeyPressed(Key.A)) yawKeys -= 1;
        }

        // Gamepad: left stick up/down throttle (centre = hover), left X yaw, right Y pitch, right X roll.
        float padThrottle = 0, padYaw = 0, padPitch = 0, padRoll = 0;
        bool pad = !menu && Input.GetConnectedJoypads().Count > 0;
        if (pad)
        {
            int device = Input.GetConnectedJoypads()[0];
            padThrottle = -Stick(device, JoyAxis.LeftY);
            padYaw = Stick(device, JoyAxis.LeftX);
            padPitch = Stick(device, JoyAxis.RightY);
            padRoll = Stick(device, JoyAxis.RightX);
        }

        if (throttleKeys > 0)
            _throttle = KeyThrottleUp;
        else if (throttleKeys < 0)
            _throttle = KeyThrottleDown;
        else if (!pad)
            _throttle = HoverThrottle; // no key: the base throttle, which just holds height
        else
        {
            float hover = HoverThrottle;
            _throttle = padThrottle >= 0 ? hover + padThrottle * (1f - hover) : hover + padThrottle * hover;
        }

        // Stick deflection is a rotation rate. The mouse delta of a frame, divided by the frame time, is the
        // rate that turns the drone by exactly that much, so it feels like a direct rotation.
        float pitchSign = GameSettings.DroneInvertPitch ? -1f : 1f;
        float mouseGain = MouseSensitivity * MouseRateGain / dt;
        var command = new Vector3(
            // Mouse forward (Y negative) dives unless inverted; stick forward (Y negative) dives.
            (mouse.Y * mouseGain + padPitch * PadRollPitchRate) * pitchSign,
            mouse.X * mouseGain + padRoll * PadRollPitchRate,
            yawKeys * KeyYawRate + padYaw * PadYawRate);
        command = command.Clamp(new Vector3(-MaxRate, -MaxRate, -MaxRate), new Vector3(MaxRate, MaxRate, MaxRate));
        _rates = _rates.Lerp(command, 1f - Mathf.Exp(-RateResponse * dt));

        // Pitch is about the right axis, roll about the forward axis, yaw about the drone's own up axis.
        var turn = new Basis(Vector3.Up, -_rates.Z * dt)
                   * new Basis(Vector3.Right, _rates.X * dt)
                   * new Basis(Vector3.Back, -_rates.Y * dt);
        _attitude = (_attitude * turn).Orthonormalized();

        // Thrust along the drone's up axis against gravity and drag.
        var acceleration = _attitude.Y * (_throttle * ThrustToWeight * _gravity)
                           + Vector3.Down * _gravity
                           - _velocity * DroneDrag
                           - _velocity * (_velocity.Length() * DroneDragQuadratic);
        _velocity += acceleration * dt;
        if (LimitToC)
            _velocity = _velocity.LimitLength(0.9f * SimClock.Instance.C);

        var position = GlobalPosition + _velocity * dt;
        if (position.Y < DroneFloor)
        {
            position.Y = DroneFloor;
            _velocity.Y = Mathf.Max(_velocity.Y, 0f);
            float scrub = Mathf.Exp(-6f * dt); // skidding on the ground
            _velocity.X *= scrub;
            _velocity.Z *= scrub;
        }
        GlobalPosition = position;

        // The camera sits tilted up on the frame; motor vibration is visual only.
        float t = (float)now;
        float shake = Mathf.Clamp(_velocity.Length() / 20f, 0f, 1.5f) * 0.6f + 0.25f + _throttle * 0.4f;
        float vibration = (Mathf.Sin(t * 53f) + 0.7f * Mathf.Sin(t * 31.7f)) * 0.0012f * shake;
        GlobalBasis = _attitude * new Basis(Vector3.Right, CameraTilt + vibration) * new Basis(Vector3.Back, vibration * 0.7f);
    }

    private void ProcessWalk()
    {
        if (_walker == null || !_walker.IsInsideTree())
            return;
        float fraction = (float)Engine.GetPhysicsInterpolationFraction();
        GlobalPosition = _walkPrev.Lerp(_walkCur, fraction) + new Vector3(0, EyeHeight, 0);
        Rotation = new Vector3(_pitch, _yaw, 0);
    }
}
