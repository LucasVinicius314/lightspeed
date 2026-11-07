using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Godot;
using Lightspeed.Core;
using Lightspeed.Demo;
using Lightspeed.History;
using Lightspeed.Player;
using Lightspeed.Visuals;
using P2PSignaling;

namespace Lightspeed.Net;

/// <summary>
/// Hosting and joining a game (autoload). One player hosts, the others join by a room code through the
/// p2p-signaling-server; after that everything runs peer to peer over WebRTC (a star around the host).
///
/// The host is authoritative. It runs the physics and owns the shared settings (speed of light, time scale, pause);
/// clients follow it:
/// <list type="bullet">
/// <item>Every player sends their camera pose; the host relays everyone's pose and the pose of every physics body
/// to the clients 30 times a second. Other players are <see cref="RemoteAvatar"/>s built from delayed parts, and a
/// client's physics bodies are "puppets" (frozen, moved by the host's snapshots), so the light delay applies to
/// everything you see, networked or not.</item>
/// <item>Scripted movers are functions of <see cref="SimClock.WorldTime"/>; a client adopts the host's world time, so
/// they match without any replication. Ticks and the light history stay local to each machine.</item>
/// <item>Objects spawned by the host (balls, thrown objects) are announced as they appear and disappear. A client's
/// throw is sent to the host, which spawns the object.</item>
/// </list>
/// Network latency adds to the light delay: a remote player is seen where they were when the light left them, plus
/// however old their latest pose is.
/// </summary>
public partial class NetSession : Node
{
    /// <summary>This game's bucket on the signaling server.</summary>
    public const string BucketId = "lightspeed";

    /// <summary>Scenes a networked physics body can be, by index (matches the throwable shapes).</summary>
    public static readonly string[] KindScenes =
    {
        "res://scenes/objects/bouncing_ball.tscn",
        "res://scenes/objects/physics_box.tscn",
        "res://scenes/objects/physics_cylinder.tscn",
    };

    private const int SnapshotIntervalMs = 33;
    private const int BodiesPerMessage = 100;
    private const int FirstDynamicId = 1000;
    private const int ThrowCooldownMs = 100;
    private const int PingIntervalMs = 250;
    private const double TimeSlewPerSnapshot = 0.001;
    private const double TimeSnapSeconds = 0.5;
    /// <summary>How far behind the host's world time (in real seconds) puppets are drawn: about three snapshots, so a late or lost one is bridged.</summary>
    private const double InterpDelaySeconds = 0.1;
    private const int MaxSamples = 12;

    public enum Role { Offline, Connecting, Host, Client }

    public static NetSession Instance { get; private set; } = null!;
    public static bool IsClient => Instance is { State: Role.Client };
    public static bool IsHost => Instance is { State: Role.Host };

    public Role State { get; private set; }

    /// <summary>A short human-readable line for the menu: what is happening, or what went wrong.</summary>
    public string Status { get; private set; } = "Not connected.";

    /// <summary>The join code of the room we host, while it is open.</summary>
    public string? RoomCode => _lobby?.RoomCode;

    /// <summary>Everyone in the session by peer id (the host is 1), in the host's roster order.</summary>
    public IReadOnlyDictionary<int, string> Players => _players;

    /// <summary>
    /// A client's round-trip time to the host in milliseconds (smoothed), measured over the peer-to-peer link; -1 until
    /// the first answer, and always -1 for a host. This is the network part of what a remote player's delay adds to the
    /// light delay.
    /// </summary>
    public double PingMs { get; private set; } = -1;

    /// <summary>Raised when the state, status or roster changes.</summary>
    public event Action? Changed;

    private sealed class NetBody
    {
        public required int Id;
        public required RigidBody3D Node;
        public required int Kind; // index into KindScenes; -1 for a body that is part of the scene
        /// <summary>The host's recent poses of this body, oldest first, stamped with the host's world time.</summary>
        private readonly List<(double Time, Vector3 Position, Quaternion Rotation)> _samples = new();

        public bool HasTarget => _samples.Count > 0;

        public void AddSample(double time, Vector3 position, Quaternion rotation)
        {
            if (_samples.Count > 0 && time <= _samples[^1].Time)
            {
                if (_samples[^1].Time - time < 1.0)
                    return; // a snapshot that arrived out of order
                _samples.Clear(); // the world time jumped back: start over
            }
            _samples.Add((time, position, rotation));
            if (_samples.Count > MaxSamples)
                _samples.RemoveAt(0);
        }

        /// <summary>The pose at a world time: interpolated between the two samples around it, held at either end.</summary>
        public (Vector3 Position, Quaternion Rotation) PoseAt(double time)
        {
            var first = _samples[0];
            if (time <= first.Time)
                return (first.Position, first.Rotation);
            for (int i = 1; i < _samples.Count; i++)
            {
                var next = _samples[i];
                if (time > next.Time)
                    continue;
                var prev = _samples[i - 1];
                float a = (float)((time - prev.Time) / (next.Time - prev.Time));
                return (prev.Position.Lerp(next.Position, a), prev.Rotation.Slerp(next.Rotation, a).Normalized());
            }
            var last = _samples[^1];
            return (last.Position, last.Rotation);
        }
    }

    private P2PLobby? _lobby;
    private bool _wantHost;
    private bool _welcomed;
    private bool _worldSynced;
    private ulong _lastSendMsec;
    private ulong _lastPingMsec;
    private double _pingMin = double.MaxValue;
    private double _pingMax;
    private double _pingSum;
    private int _pingCount;
    private int _nextDynamicId = FirstDynamicId;
    private bool _spawnerWasTimed;
    private Node3D? _container;
    private readonly Dictionary<int, string> _players = new();
    private readonly Dictionary<int, RemoteAvatar> _avatars = new();
    private readonly Dictionary<int, PoseState> _clientPoses = new();
    private readonly Dictionary<int, NetBody> _bodies = new();
    private readonly Dictionary<int, ulong> _lastThrowMsec = new();

    // Scripted verification (see the command-line options in CLAUDE.md).
    private long _reportAtTick = -1;
    private long _quitAtTick = -1;
    private bool _reportWhenPaused;
    private double _pausedSeconds;
    private int _scriptedThrows;
    private int _scriptedStepIndex = -1;
    private float[]? _scriptedCamera;

    public override void _EnterTree()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always; // the session keeps running while the simulation is paused
        ProcessPhysicsPriority = -500;        // after the clock, before movers, so puppets are in place when the history records
    }

    public override void _Ready()
    {
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ServerDisconnected += OnServerDisconnected;

        bool autoHost = false;
        string? autoJoin = null;
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--net-host")
                autoHost = true;
            else if (arg.StartsWith("--net-join="))
                autoJoin = arg["--net-join=".Length..];
            else if (arg.StartsWith("--net-report-at="))
                _reportAtTick = long.Parse(arg["--net-report-at=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--net-quit-at="))
                _quitAtTick = long.Parse(arg["--net-quit-at=".Length..], CultureInfo.InvariantCulture);
            else if (arg == "--net-report-paused")
                _reportWhenPaused = true;
            else if (arg.StartsWith("--net-throw="))
                _scriptedThrows = int.Parse(arg["--net-throw=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--net-step-c="))
                _scriptedStepIndex = int.Parse(arg["--net-step-c=".Length..], CultureInfo.InvariantCulture);
            else if (arg.StartsWith("--net-cam="))
                _scriptedCamera = Array.ConvertAll(arg["--net-cam=".Length..].Split(','), v => float.Parse(v, CultureInfo.InvariantCulture));
        }
        if (_scriptedCamera is { Length: 5 } cam)
            Callable.From(() =>
            {
                if (GetViewport().GetCamera3D() is FreeCamera free)
                    free.Place(new Vector3(cam[0], cam[1], cam[2]), cam[3], cam[4]);
            }).CallDeferred();
        if (autoHost)
            Callable.From(Host).CallDeferred();
        else if (autoJoin != null)
            Callable.From(() => Join(autoJoin)).CallDeferred();
    }

    // ------------------------------------------------------------------ starting and stopping

    public void Host() => Begin(host: true, code: null);

    public void Join(string code) => Begin(host: false, code: code);

    private void Begin(bool host, string? code)
    {
        if (State != Role.Offline)
            return;
        string url = GameSettings.NetServerUrl.Trim();
        if (url.Length == 0)
        {
            SetStatus("Enter the server address first.");
            return;
        }
        code = code?.Trim().ToUpperInvariant();
        if (!host && string.IsNullOrEmpty(code))
        {
            SetStatus("Enter the room code first.");
            return;
        }

        _wantHost = host;
        _welcomed = false;
        _worldSynced = false;
        _lobby = new P2PLobby(url, BucketId, GameVersion()) { PlayerName = LocalName() };
        AddChild(_lobby);
        _lobby.CodeReady += c =>
        {
            GD.Print($"NET CODE {c}");
            SetStatus($"Hosting room {c}. Share the code.");
        };
        _lobby.PeerCreated += OnPeerCreated;
        _lobby.JoinPending += () => SetStatus("Waiting for the host to let you in ...");
        _lobby.Failed += OnLobbyFailed;
        _lobby.RoomClosed += reason =>
        {
            if (State != Role.Offline) // the server and the multiplayer layer can both report the same loss
                EndSession($"The room was closed ({reason}).");
        };
        _lobby.SignalingLost += OnSignalingLost;

        State = Role.Connecting;
        SetStatus(host ? "Creating a room ..." : $"Joining {code} ...");
        if (host)
            _lobby.CreateRoom();
        else
            _lobby.JoinRoom(code!);
    }

    /// <summary>Leave the session and go back to a game of your own.</summary>
    public void Leave()
    {
        if (State == Role.Offline)
            return;
        _lobby?.Leave();
        EndSession("Left the session.");
    }

    /// <summary>
    /// The multiplayer peer exists but no link is up yet. It is attached to Godot's multiplayer now, not later:
    /// a peer attached after it connected is never registered, and its packets are dropped.
    /// </summary>
    private void OnPeerCreated(MultiplayerPeer peer)
    {
        Multiplayer.MultiplayerPeer = peer;
        if (_wantHost)
            BecomeHost();
        else
            SetStatus("Linking to the host ...");
        // A client continues in OnConnectedToServer, once the multiplayer layer has seen the host.
    }

    private void OnLobbyFailed(string code, string message)
    {
        if (State == Role.Connecting)
            EndSession($"{message} ({code})");
        else
            SetStatus($"{message} ({code})");
    }

    private void OnSignalingLost(string reason)
    {
        if (State == Role.Host)
            SetStatus("Lost the signaling server: the room code no longer works and nobody new can join. " +
                      "Players already connected keep playing.");
    }

    private void OnConnectedToServer()
    {
        if (_wantHost || State != Role.Connecting)
            return;
        State = Role.Client;
        _players[Multiplayer.GetUniqueId()] = LocalName();
        RpcId(1, MethodName.RpcHello, LocalName());
        SetStatus("Connected. Waiting for the host's world ...");
    }

    private void OnServerDisconnected()
    {
        if (State != Role.Offline)
            EndSession("The host disconnected.");
    }

    private void OnPeerDisconnected(long id)
    {
        if (!IsHost)
            return;
        int peer = (int)id;
        _players.Remove(peer);
        _clientPoses.Remove(peer);
        RemoveAvatar(peer);
        BroadcastRoster();
        Changed?.Invoke();
    }

    private void BecomeHost()
    {
        State = Role.Host;
        _players.Clear();
        _players[1] = LocalName();
        CollectStaticBodies();
        AdoptDynamicBodies();
        SimClock.Instance.SharedChanged += BroadcastSettings;
        SimClock.Instance.Stepped += BroadcastStep;
        Changed?.Invoke();
    }

    private void EndSession(string status)
    {
        if (State == Role.Client)
            ReleasePuppets();
        if (State == Role.Host)
        {
            SimClock.Instance.SharedChanged -= BroadcastSettings;
            SimClock.Instance.Stepped -= BroadcastStep;
        }
        foreach (var avatar in _avatars.Values)
            avatar.Retire();
        _avatars.Clear();
        _players.Clear();
        _clientPoses.Clear();
        _bodies.Clear();
        _lastThrowMsec.Clear();
        _nextDynamicId = FirstDynamicId;
        if (Multiplayer.MultiplayerPeer != null)
            Multiplayer.MultiplayerPeer = null;
        SimClock.Instance.WorldOffset = 0;
        if (_lobby != null)
        {
            _lobby.QueueFree();
            _lobby = null;
        }
        _container = null;
        State = Role.Offline;
        PingMs = -1;
        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        Status = text;
        GD.Print($"NET {text}");
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ the frame loop

    public override void _Process(double delta)
    {
        if (_reportWhenPaused && State is Role.Host or Role.Client && SimClock.Instance.Paused)
        {
            _pausedSeconds += delta;
            if (_pausedSeconds >= 2.0)
            {
                _reportWhenPaused = false; // the world has been still long enough for puppets to settle
                PrintReport();
                GetTree().CreateTimer(0.5, processAlways: true).Timeout += () => GetTree().Quit();
            }
        }
        if (State is not (Role.Host or Role.Client))
            return;
        ulong now = Time.GetTicksMsec();
        if (now - _lastSendMsec < SnapshotIntervalMs)
            return;
        _lastSendMsec = now;
        if (State == Role.Host)
            SendSnapshot();
        else if (_welcomed)
        {
            SendMyPose();
            if (now - _lastPingMsec >= PingIntervalMs)
            {
                _lastPingMsec = now;
                RpcId(1, MethodName.RpcPing, (long)Time.GetTicksUsec());
            }
        }
    }

    // Round-trip time: the client stamps a ping, the host bounces it back unchanged, the client reads the clock again.
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void RpcPing(long sentMicroseconds)
    {
        if (IsHost)
            RpcId(Multiplayer.GetRemoteSenderId(), MethodName.RpcPong, sentMicroseconds);
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void RpcPong(long sentMicroseconds)
    {
        if (State != Role.Client || Multiplayer.GetRemoteSenderId() != 1)
            return;
        double ms = ((long)Time.GetTicksUsec() - sentMicroseconds) / 1000.0;
        if (ms < 0 || ms > 60000)
            return;
        PingMs = PingMs < 0 ? ms : PingMs * 0.8 + ms * 0.2;
        _pingMin = Math.Min(_pingMin, ms);
        _pingMax = Math.Max(_pingMax, ms);
        _pingSum += ms;
        _pingCount++;
    }

    public override void _PhysicsProcess(double delta)
    {
        long tick = SimClock.Instance.Tick;
        if (_reportAtTick >= 0 && tick == _reportAtTick)
            PrintReport();
        if (_quitAtTick >= 0 && tick >= _quitAtTick)
            GetTree().Quit();

        RunScriptedActions(tick);

        if (State != Role.Client)
            return;
        // Puppets are drawn a little behind the host's world time, interpolating between its snapshots. The delay is
        // in world seconds, so it follows the time scale; paused, the world is still and the newest pose is the answer.
        var clock = SimClock.Instance;
        double renderTime = clock.WorldTime - (clock.Paused ? 0.0 : InterpDelaySeconds * clock.TimeScale);
        foreach (var body in _bodies.Values)
        {
            if (!body.HasTarget || !IsInstanceValid(body.Node))
                continue;
            var (position, rotation) = body.PoseAt(renderTime);
            body.Node.GlobalTransform = new Transform3D(new Basis(rotation), position);
        }
    }

    private void SendMyPose()
    {
        if (!TryGetLocalPose(out var pose))
            return;
        RpcId(1, MethodName.RpcPlayerState, NetCodec.EncodePose(pose));
    }

    private void SendSnapshot()
    {
        // Bodies that were removed since the last snapshot (despawned) are announced once.
        foreach (var (id, body) in _bodies.ToArray())
        {
            if (IsInstanceValid(body.Node) && !body.Node.IsQueuedForDeletion())
                continue;
            _bodies.Remove(id);
            if (body.Kind >= 0)
                Rpc(MethodName.RpcDespawn, id);
        }

        var players = new List<(int, PoseState)>();
        if (TryGetLocalPose(out var mine))
            players.Add((1, mine));
        foreach (var (id, pose) in _clientPoses)
            players.Add((id, pose));

        var poses = new List<BodyPose>(_bodies.Count);
        foreach (var body in _bodies.Values)
        {
            var t = body.Node.GlobalTransform;
            poses.Add(new BodyPose(body.Id, t.Origin, t.Basis.GetRotationQuaternion()));
        }

        double worldTime = SimClock.Instance.WorldTime;
        for (int start = 0; start == 0 || start < poses.Count; start += BodiesPerMessage)
        {
            var chunk = poses.Skip(start).Take(BodiesPerMessage).ToList();
            var included = start == 0 ? players : new List<(int, PoseState)>();
            Rpc(MethodName.RpcSnapshot, NetCodec.EncodeSnapshot(worldTime, included, chunk));
        }
    }

    private bool TryGetLocalPose(out PoseState pose)
    {
        pose = default;
        if (GetViewport()?.GetCamera3D() is not { } camera)
            return false;
        byte mode = camera is FreeCamera free ? (byte)free.Mode : (byte)0;
        pose = new PoseState(mode, camera.GlobalPosition, camera.GlobalBasis.GetRotationQuaternion());
        return true;
    }

    // ------------------------------------------------------------------ messages: joining

    [Rpc(MultiplayerApi.RpcMode.AnyPeer)]
    private void RpcHello(string name)
    {
        if (!IsHost)
            return;
        int id = Multiplayer.GetRemoteSenderId();
        _players[id] = CleanName(name, id);
        EnsureAvatar(id);
        SendWelcome(id);
        BroadcastRoster();
        Changed?.Invoke();
    }

    private void SendWelcome(int peer)
    {
        var bodies = _bodies.Values
            .Where(b => b.Kind >= 0 && IsInstanceValid(b.Node))
            .Select(b =>
            {
                var t = b.Node.GlobalTransform;
                var color = b.Node.GetNodeOrNull<DelayedVisual>("DelayedVisual")?.Albedo ?? Colors.White;
                return new BodyDescription(b.Id, b.Kind, color, t.Origin, t.Basis.GetRotationQuaternion());
            })
            .ToList();
        var clock = SimClock.Instance;
        RpcId(peer, MethodName.RpcWelcome, clock.WorldTime, clock.StepIndex, clock.TimeScale, clock.Paused, NetCodec.EncodeBodies(bodies));
    }

    [Rpc]
    private void RpcWelcome(double worldTime, int stepIndex, float timeScale, bool paused, byte[] bodies)
    {
        if (State != Role.Client || Multiplayer.GetRemoteSenderId() != 1)
            return;
        var clock = SimClock.Instance;
        clock.WorldOffset = worldTime - clock.Time;
        _worldSynced = true;
        clock.ApplyFromHost(stepIndex, timeScale, paused);
        BecomePuppets();
        if (NetCodec.TryDecodeBodies(bodies, out var list))
            foreach (var b in list)
                SpawnPuppet(b.Id, b.Kind, b.Color, b.Position, b.Rotation);
        _welcomed = true;
        SetStatus("Connected. The host runs the physics and the speed of light.");
    }

    private void BroadcastRoster()
    {
        var ids = _players.Keys.ToArray();
        var names = ids.Select(i => _players[i]).ToArray();
        Rpc(MethodName.RpcRoster, ids, names);
    }

    [Rpc]
    private void RpcRoster(int[] ids, string[] names)
    {
        if (State != Role.Client || Multiplayer.GetRemoteSenderId() != 1)
            return;
        _players.Clear();
        for (int i = 0; i < ids.Length && i < names.Length; i++)
            _players[ids[i]] = names[i];
        int me = Multiplayer.GetUniqueId();
        foreach (var id in _players.Keys)
            if (id != me)
                EnsureAvatar(id);
        foreach (var id in _avatars.Keys.Where(id => !_players.ContainsKey(id)).ToArray())
            RemoveAvatar(id);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ messages: every frame

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void RpcPlayerState(byte[] data)
    {
        if (!IsHost)
            return;
        int id = Multiplayer.GetRemoteSenderId();
        if (!_players.ContainsKey(id) || !NetCodec.TryDecodePose(data, out var pose))
            return;
        _clientPoses[id] = pose;
        if (_avatars.TryGetValue(id, out var avatar))
            avatar.SetState(pose);
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void RpcSnapshot(byte[] data)
    {
        if (State != Role.Client || !_welcomed || Multiplayer.GetRemoteSenderId() != 1)
            return;
        if (!NetCodec.TryDecodeSnapshot(data, out double worldTime, out var players, out var bodies))
            return;
        SyncWorldTime(worldTime);
        int me = Multiplayer.GetUniqueId();
        foreach (var (id, pose) in players)
        {
            if (id == me)
                continue;
            EnsureAvatar(id);
            _avatars[id].SetState(pose);
        }
        foreach (var pose in bodies)
        {
            if (!_bodies.TryGetValue(pose.Id, out var body))
                continue;
            bool first = !body.HasTarget;
            body.AddSample(worldTime, pose.Position, pose.Rotation);
            if (first && IsInstanceValid(body.Node)) // the first pose is taken as is
                body.Node.GlobalTransform = new Transform3D(new Basis(pose.Rotation), pose.Position);
        }
    }

    /// <summary>
    /// Brings this machine's world time toward the host's: a jump when it is far off, otherwise a gentle nudge,
    /// so scripted movers never visibly hitch.
    /// </summary>
    private void SyncWorldTime(double hostWorldTime)
    {
        var clock = SimClock.Instance;
        double target = hostWorldTime - clock.Time;
        double difference = target - clock.WorldOffset;
        if (!_worldSynced || Math.Abs(difference) > TimeSnapSeconds)
            clock.WorldOffset = target;
        else
            clock.WorldOffset += Math.Clamp(difference, -TimeSlewPerSnapshot, TimeSlewPerSnapshot);
        _worldSynced = true;
    }

    // ------------------------------------------------------------------ messages: shared settings

    private void BroadcastSettings()
    {
        var clock = SimClock.Instance;
        Rpc(MethodName.RpcSettings, clock.StepIndex, clock.TimeScale, clock.Paused);
    }

    private void BroadcastStep() => Rpc(MethodName.RpcStep);

    [Rpc]
    private void RpcSettings(int stepIndex, float timeScale, bool paused)
    {
        if (State == Role.Client && Multiplayer.GetRemoteSenderId() == 1)
            SimClock.Instance.ApplyFromHost(stepIndex, timeScale, paused);
    }

    [Rpc]
    private void RpcStep()
    {
        if (State == Role.Client && Multiplayer.GetRemoteSenderId() == 1)
            SimClock.Instance.ApplyStepFromHost();
    }

    // A client's own input on the shared settings goes to the host, which applies it and tells everyone.
    public void RequestStepIndex(int index) => RpcId(1, MethodName.RpcAskStepIndex, index);
    public void RequestTimeScale(float scale) => RpcId(1, MethodName.RpcAskTimeScale, scale);
    public void RequestPaused(bool paused) => RpcId(1, MethodName.RpcAskPaused, paused);
    public void RequestStep() => RpcId(1, MethodName.RpcAskStep);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer)]
    private void RpcAskStepIndex(int index)
    {
        if (IsHost)
            SimClock.Instance.SetStepIndex(index);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer)]
    private void RpcAskTimeScale(float scale)
    {
        if (IsHost && float.IsFinite(scale))
            SimClock.Instance.SetTimeScale(scale);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer)]
    private void RpcAskPaused(bool paused)
    {
        if (IsHost)
            SimClock.Instance.SetPaused(paused);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer)]
    private void RpcAskStep()
    {
        if (IsHost)
            SimClock.Instance.StepTick();
    }

    // ------------------------------------------------------------------ physics bodies and thrown objects

    /// <summary>Host: a ball or thrown object was just added to the world; tell the clients to make a puppet of it.</summary>
    public void OnBodySpawned(RigidBody3D body, Color color)
    {
        int kind = Array.IndexOf(KindScenes, body.SceneFilePath);
        if (State != Role.Host || kind < 0)
            return;
        int id = _nextDynamicId++;
        _bodies[id] = new NetBody { Id = id, Node = body, Kind = kind };
        var t = body.GlobalTransform;
        Rpc(MethodName.RpcSpawn, id, kind, t.Origin, t.Basis.GetRotationQuaternion(), color);
    }

    [Rpc]
    private void RpcSpawn(int id, int kind, Vector3 position, Quaternion rotation, Color color)
    {
        if (State == Role.Client && Multiplayer.GetRemoteSenderId() == 1)
            SpawnPuppet(id, kind, color, position, rotation);
    }

    [Rpc]
    private void RpcDespawn(int id)
    {
        if (State != Role.Client || Multiplayer.GetRemoteSenderId() != 1 || !_bodies.Remove(id, out var body))
            return;
        if (IsInstanceValid(body.Node))
            body.Node.GetNodeOrNull<LightHistory>("LightHistory")?.Despawn();
    }

    /// <summary>Client: ask the host to throw an object. The host spawns it and everyone sees it arrive.</summary>
    public void RequestThrow(int kind, Vector3 origin, Vector3 direction) =>
        RpcId(1, MethodName.RpcAskThrow, kind, origin, direction);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer)]
    private void RpcAskThrow(int kind, Vector3 origin, Vector3 direction)
    {
        if (!IsHost)
            return;
        int sender = Multiplayer.GetRemoteSenderId();
        ulong now = Time.GetTicksMsec();
        if (_lastThrowMsec.TryGetValue(sender, out var last) && now - last < ThrowCooldownMs)
            return;
        _lastThrowMsec[sender] = now;
        bool finite = float.IsFinite(origin.X) && float.IsFinite(origin.Y) && float.IsFinite(origin.Z)
                      && float.IsFinite(direction.X) && float.IsFinite(direction.Y) && float.IsFinite(direction.Z);
        if (finite && direction.LengthSquared() > 1e-6f && GetTree().GetFirstNodeInGroup("thrower") is Thrower thrower)
            thrower.ThrowFrom(kind, origin, direction.Normalized());
    }

    /// <summary>Bodies that are part of the scene: the direct children of the scene root in the "net_body" group.</summary>
    private void CollectStaticBodies()
    {
        var root = GetTree().CurrentScene;
        if (root == null)
            return;
        int id = 0;
        foreach (var body in root.GetChildren().OfType<RigidBody3D>().Where(b => b.IsInGroup("net_body"))
                     .OrderBy(b => b.Name.ToString(), StringComparer.Ordinal))
            _bodies[id++] = new NetBody { Id = id - 1, Node = body, Kind = -1 };
    }

    /// <summary>Host: balls and thrown objects that already exist when the session starts join it.</summary>
    private void AdoptDynamicBodies()
    {
        foreach (var owner in new[] { GetTree().GetFirstNodeInGroup("spawner"), GetTree().GetFirstNodeInGroup("thrower") })
        {
            if (owner == null)
                continue;
            foreach (var child in owner.GetChildren().OfType<RigidBody3D>())
            {
                int kind = Array.IndexOf(KindScenes, child.SceneFilePath);
                if (kind < 0)
                    continue;
                int id = _nextDynamicId++;
                _bodies[id] = new NetBody { Id = id, Node = child, Kind = kind };
            }
        }
    }

    /// <summary>
    /// Client: the host owns the physics. Scene bodies are frozen and moved by snapshots; balls and thrown objects of
    /// the client's own game are cleared and the host's replace them.
    /// </summary>
    private void BecomePuppets()
    {
        _bodies.Clear();
        CollectStaticBodies();
        foreach (var body in _bodies.Values)
        {
            body.Node.Freeze = true;
            body.Node.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
        }
        if (GetTree().GetFirstNodeInGroup("spawner") is Spawner spawner)
        {
            _spawnerWasTimed = spawner.TimedSpawning;
            spawner.DespawnAll();
            spawner.TimedSpawning = false;
        }
        (GetTree().GetFirstNodeInGroup("thrower") as Thrower)?.Clear();
    }

    /// <summary>Client, session over: bodies simulate here again, from where the host last left them.</summary>
    private void ReleasePuppets()
    {
        foreach (var body in _bodies.Values)
        {
            if (!IsInstanceValid(body.Node))
                continue;
            if (body.Kind < 0)
                body.Node.Freeze = false;
            else
                body.Node.GetNodeOrNull<LightHistory>("LightHistory")?.Despawn();
        }
        if (GetTree().GetFirstNodeInGroup("spawner") is Spawner spawner)
            spawner.TimedSpawning = _spawnerWasTimed;
    }

    private void SpawnPuppet(int id, int kind, Color color, Vector3 position, Quaternion rotation)
    {
        if (kind < 0 || kind >= KindScenes.Length || _bodies.ContainsKey(id))
            return;
        var body = GD.Load<PackedScene>(KindScenes[kind]).Instantiate<RigidBody3D>();
        body.Freeze = true;
        body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
        body.Transform = new Transform3D(new Basis(rotation), position);
        body.GetNode<DelayedVisual>("DelayedVisual").Albedo = color;
        Container().AddChild(body);
        var puppet = new NetBody { Id = id, Node = body, Kind = kind };
        // Older than any snapshot that can follow, so the first real one is accepted; until then it stays put.
        puppet.AddSample(SimClock.Instance.WorldTime - 1.0, position, rotation);
        _bodies[id] = puppet;
    }

    /// <summary>Where puppets of spawned bodies live, so they are never mistaken for scene bodies.</summary>
    private Node3D Container()
    {
        if (_container != null && IsInstanceValid(_container))
            return _container;
        _container = new Node3D { Name = "NetBodies" };
        GetTree().CurrentScene.AddChild(_container);
        return _container;
    }

    // ------------------------------------------------------------------ avatars and names

    private void EnsureAvatar(int id)
    {
        if (_avatars.ContainsKey(id))
            return;
        var avatar = new RemoteAvatar(id, Color.FromHsv((id * 0.61803f) % 1f, 0.55f, 1f));
        GetTree().CurrentScene.AddChild(avatar);
        _avatars[id] = avatar;
        if (_clientPoses.TryGetValue(id, out var pose))
            avatar.SetState(pose);
    }

    private void RemoveAvatar(int id)
    {
        if (_avatars.Remove(id, out var avatar))
            avatar.Retire();
    }

    private static string LocalName() => CleanName(GameSettings.PlayerName, 0);

    private static string CleanName(string name, int id)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 24)
            clean = clean[..24];
        return clean.Length > 0 ? clean : id > 0 ? $"Player {id % 1000}" : "Player";
    }

    /// <summary>
    /// The version the signaling server checks against the bucket's accepted versions: the release tag without its
    /// leading v, read from the version.txt that release builds carry; "dev" for a build run from source.
    /// Override with `-- --net-version=...`.
    /// </summary>
    public static string GameVersion()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--net-version="))
                return arg["--net-version=".Length..];
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()) ?? "", "version.txt");
            if (File.Exists(path))
                return File.ReadAllText(path).Trim().TrimStart('v');
        }
        catch (IOException)
        {
            // unreadable: fall through
        }
        return "dev";
    }

    // ------------------------------------------------------------------ verification

    /// <summary>Command-line scripted actions for headless verification (see CLAUDE.md).</summary>
    private void RunScriptedActions(long tick)
    {
        bool ready = State == Role.Host || (State == Role.Client && _welcomed);
        if (!ready || tick % 30 != 0)
            return;
        if (_scriptedStepIndex >= 0)
        {
            SimClock.Instance.SetStepIndex(_scriptedStepIndex);
            _scriptedStepIndex = -1;
        }
        if (_scriptedThrows > 0 && GetViewport().GetCamera3D() is { } camera)
        {
            _scriptedThrows--;
            var forward = -camera.GlobalBasis.Z;
            var origin = camera.GlobalPosition + forward * 1.2f;
            if (IsClient)
                RequestThrow(0, origin, forward);
            else
                (GetTree().GetFirstNodeInGroup("thrower") as Thrower)?.ThrowFrom(0, origin, forward);
        }
    }

    private void PrintReport()
    {
        var clock = SimClock.Instance;
        var inv = CultureInfo.InvariantCulture;
        string F(Vector3 v) => string.Create(inv, $"({v.X:0.###},{v.Y:0.###},{v.Z:0.###})");
        GD.Print($"NET REPORT role={State} id={(Multiplayer.MultiplayerPeer != null ? Multiplayer.GetUniqueId() : 0)} " +
                 $"players=[{string.Join(",", _players.Select(p => $"{p.Key}:{p.Value}"))}] " +
                 $"worldTime={clock.WorldTime.ToString("0.###", inv)} c={clock.C.ToString("0.###", inv)} " +
                 $"timeScale={clock.TimeScale.ToString("0.##", inv)} paused={clock.Paused}");
        if (_pingCount > 0)
            GD.Print($"NET PING samples={_pingCount} avg={(_pingSum / _pingCount).ToString("0.00", inv)}ms " +
                     $"min={_pingMin.ToString("0.00", inv)}ms max={_pingMax.ToString("0.00", inv)}ms smoothed={PingMs.ToString("0.00", inv)}ms");
        foreach (var (id, avatar) in _avatars.OrderBy(a => a.Key))
            GD.Print($"NET AVATAR {id} pos={F(avatar.Position3D)}");
        foreach (var body in _bodies.Values.OrderBy(b => b.Id).Where(b => IsInstanceValid(b.Node)))
            GD.Print($"NET BODY {body.Id} kind={body.Kind} pos={F(body.Node.GlobalPosition)}");
        if (GetTree().CurrentScene?.GetNodeOrNull<Node3D>("OrbitingCube") is { } cube)
            GD.Print($"NET MOVER OrbitingCube worldTime={clock.WorldTime.ToString("0.#####", inv)} pos={F(cube.GlobalPosition)}");
    }
}
