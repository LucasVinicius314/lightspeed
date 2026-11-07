using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Lightspeed.Core;

namespace Lightspeed.History;

/// <summary>
/// Packs every object's history into GPU textures for the delayed-visual shader.
///
/// <c>lc_history</c> is a Texture2DArray. Each layer holds <see cref="Chunk"/> consecutive time slots, so
/// a tick only re-uploads the one layer being written, not the whole history. Within a layer, row = object,
/// and each sample takes two texels: (position.xyz, teleport-flag) then rotation quaternion (xyzw).
/// <c>lc_meta</c> has one texel per object row: (birth tick, death tick or -1, row in use), with ticks taken
/// modulo <see cref="TickWrap"/> so float32 never loses precision however long the simulation runs.
/// Sample time is implicit (the newest slot is "now", each older slot one tick earlier), so no absolute
/// time is stored. The shared write head, sample count and tick are shader globals.
/// </summary>
public partial class HistoryTexture : Node
{
    /// <summary>Time slots per texture layer. Must match CHUNK in the shader.</summary>
    public const int Chunk = 16;
    /// <summary>Object rows in the texture. Fixed; the per-tick upload size depends on it.</summary>
    public const int MaxObjects = 1024;
    /// <summary>Ticks are stored modulo this. Must match TICK_WRAP in the shader.</summary>
    public const int TickWrap = 1 << 20;

    private const int MinCapacity = 300;
    private const int MaxCapacity = 8192; // the shader's binary search covers 2^14 samples

    /// <summary>
    /// Samples kept per object: <see cref="GameSettings.HistorySeconds"/> at the tick rate, in whole chunks.
    /// Light from farther than c times this is gone, so lowering c later shrinks the visible range (shown on
    /// the HUD). Changing the setting resizes the buffer in place, keeping the newest samples.
    /// </summary>
    public static int Capacity { get; private set; } = MinCapacity;

    /// <summary>Raised after the buffer was resized, so the HUD can refresh its numbers.</summary>
    public static event Action? Resized;

    /// <summary>Samples for a history of <paramref name="seconds"/>, rounded up to whole chunks and clamped.</summary>
    public static int CapacityFor(double seconds)
    {
        int samples = (int)Math.Ceiling(seconds / SimClock.TickDelta);
        return (Math.Clamp(samples, MinCapacity, MaxCapacity) + Chunk - 1) / Chunk * Chunk;
    }

    /// <summary>Rough memory for a history of <paramref name="seconds"/>: the texture plus its CPU copy.</summary>
    public static double EstimateMegabytes(double seconds) =>
        CapacityFor(seconds) / Chunk * (Chunk * 2.0 * MaxObjects * 4 * sizeof(float)) * 2 / 1e6;

    public static HistoryTexture Instance { get; private set; } = null!;

    /// <summary>Samples currently held per object (shared write head).</summary>
    public int SampleCount => _count;
    public int ActiveTracks => _tracks.Count;

    /// <summary>Seconds since the newest sample, so the shader sees time between ticks.</summary>
    public static double FractionalAge =>
        SimClock.Instance.Paused ? SimClock.Instance.FrozenAge : Engine.GetPhysicsInterpolationFraction() * SimClock.TickDelta;

    /// <summary>Seconds since the newest sample of <paramref name="track"/> (older for a despawned object).</summary>
    public static double AgeOf(HistoryTrack track) =>
        (SimClock.Instance.Tick - track.LastTick) * SimClock.TickDelta + FractionalAge;

    private readonly List<HistoryTrack> _tracks = new();
    private readonly Stack<int> _freeRows = new();
    private int _nextRow;

    private float[][] _layerData = null!;
    private byte[] _scratch = null!;
    private Texture2DArray _history = null!;
    private Image _metaImage = null!;
    private ImageTexture _metaTexture = null!;
    private bool _metaDirty;
    private int _head = -1;
    private int _count;

    public override void _EnterTree()
    {
        Instance = this;
        ProcessPhysicsPriority = 2000;
        ProcessMode = ProcessModeEnum.Always; // _Process keeps the shader age current; the tick itself skips while paused

        Capacity = CapacityFor(GameSettings.HistorySeconds);
        GameSettings.Changed += OnSettingsChanged;

        int layers = Capacity / Chunk;
        int floatsPerLayer = Chunk * 2 * MaxObjects * 4;
        _layerData = new float[layers][];
        var images = new Godot.Collections.Array<Image>();
        for (int i = 0; i < layers; i++)
        {
            _layerData[i] = new float[floatsPerLayer];
            images.Add(Image.CreateEmpty(Chunk * 2, MaxObjects, false, Image.Format.Rgbaf));
        }
        _scratch = new byte[floatsPerLayer * sizeof(float)];
        _history = new Texture2DArray();
        _history.CreateFromImages(images);

        _metaImage = Image.CreateEmpty(1, MaxObjects, false, Image.Format.Rgbaf);
        _metaTexture = ImageTexture.CreateFromImage(_metaImage);

        RenderingServer.GlobalShaderParameterAdd("lc_history", RenderingServer.GlobalShaderParameterType.Sampler2Darray, _history);
        RenderingServer.GlobalShaderParameterAdd("lc_meta", RenderingServer.GlobalShaderParameterType.Sampler2D, _metaTexture);
        RenderingServer.GlobalShaderParameterAdd("lc_head", RenderingServer.GlobalShaderParameterType.Int, 0);
        RenderingServer.GlobalShaderParameterAdd("lc_count", RenderingServer.GlobalShaderParameterType.Int, 0);
        RenderingServer.GlobalShaderParameterAdd("lc_tick", RenderingServer.GlobalShaderParameterType.Int, 0);
        RenderingServer.GlobalShaderParameterAdd("lc_age", RenderingServer.GlobalShaderParameterType.Float, 0f);
        RenderingServer.GlobalShaderParameterAdd("lc_c", RenderingServer.GlobalShaderParameterType.Float, SimClock.Instance.C);
    }

    public override void _ExitTree() => GameSettings.Changed -= OnSettingsChanged;

    private double _resizeAtMsec = -1;

    private void OnSettingsChanged()
    {
        // Dragging the slider fires this many times; resize once it settles.
        _resizeAtMsec = CapacityFor(GameSettings.HistorySeconds) != Capacity ? Time.GetTicksMsec() + 400 : -1;
    }

    public override void _Process(double delta)
    {
        RenderingServer.GlobalShaderParameterSet("lc_age", (float)FractionalAge);
        if (_resizeAtMsec >= 0 && Time.GetTicksMsec() >= _resizeAtMsec)
        {
            _resizeAtMsec = -1;
            Resize(CapacityFor(GameSettings.HistorySeconds));
        }
    }

    /// <summary>
    /// Re-packs the buffer for a new capacity without losing the recent past: every sample is moved by its age
    /// (how many ticks before now), then each object's own ring is resized to match. Runs between ticks.
    /// </summary>
    private void Resize(int newCapacity)
    {
        if (newCapacity == Capacity)
            return;

        int oldCapacity = Capacity;
        int keep = Math.Min(_count, newCapacity);
        int rows = Math.Max(_nextRow, 1);
        int layers = newCapacity / Chunk;
        int floatsPerLayer = Chunk * 2 * MaxObjects * 4;

        var data = new float[layers][];
        for (int i = 0; i < layers; i++)
            data[i] = new float[floatsPerLayer];

        for (int age = 0; age < keep; age++)
        {
            int oldSlot = ((_head - age) % oldCapacity + oldCapacity) % oldCapacity;
            int newSlot = keep - 1 - age;
            var from = _layerData[oldSlot / Chunk];
            var to = data[newSlot / Chunk];
            int fromOffset = oldSlot % Chunk * 2 * 4; // two texels of four floats per sample
            int toOffset = newSlot % Chunk * 2 * 4;
            for (int row = 0; row < rows; row++)
            {
                int rowStart = row * Chunk * 2 * 4;
                Array.Copy(from, rowStart + fromOffset, to, rowStart + toOffset, 8);
            }
        }

        _layerData = data;
        Capacity = newCapacity;
        _head = keep - 1;
        _count = keep;
        foreach (var track in _tracks)
            track.Resize(newCapacity);

        var images = new Godot.Collections.Array<Image>();
        for (int i = 0; i < layers; i++)
        {
            Buffer.BlockCopy(data[i], 0, _scratch, 0, _scratch.Length);
            images.Add(Image.CreateFromData(Chunk * 2, MaxObjects, false, Image.Format.Rgbaf, _scratch));
        }
        var previous = _history;
        _history = new Texture2DArray();
        _history.CreateFromImages(images);
        RenderingServer.GlobalShaderParameterSet("lc_history", _history);
        RenderingServer.GlobalShaderParameterSet("lc_head", _head);
        RenderingServer.GlobalShaderParameterSet("lc_count", _count);

        // Release the old texture and the temporary images now rather than whenever the GC gets to them: they
        // are hundreds of megabytes at long histories.
        previous.Dispose();
        foreach (var image in images)
            image.Dispose();
        GC.Collect();
        Resized?.Invoke();
    }

    /// <summary>Allocates a history row. Returns null (and logs an error) when all rows are in use.</summary>
    public HistoryTrack? CreateTrack()
    {
        int row;
        if (_freeRows.Count > 0)
            row = _freeRows.Pop();
        else if (_nextRow < MaxObjects)
            row = _nextRow++;
        else
        {
            GD.PushError($"HistoryTexture: all {MaxObjects} object rows are in use; this object will not be visible.");
            return null;
        }

        var track = new HistoryTrack(Capacity, row, SimClock.TickDelta);
        _tracks.Add(track);
        return track;
    }

    /// <summary>Smoothed milliseconds the per-tick history update (recording, upload) costs on the CPU.</summary>
    public double UpdateMs { get; private set; }

    public override void _PhysicsProcess(double delta)
    {
        if (GetTree().Paused)
            return;
        long start = Stopwatch.GetTimestamp();
        long tick = SimClock.Instance.Tick;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity)
            _count++;

        int layer = _head / Chunk;
        int col = _head % Chunk * 2;
        var data = _layerData[layer];

        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            var track = _tracks[i];
            if (track.LastTick == tick)
                WriteSample(data, track, col);
            if (track.MetaDirty)
                WriteMeta(track);
            if (track.IsExpired(tick))
            {
                ReleaseRow(track);
                _tracks.RemoveAt(i);
            }
        }

        // Only the layer written this tick goes to the GPU.
        Buffer.BlockCopy(data, 0, _scratch, 0, _scratch.Length);
        _history.UpdateLayer(Image.CreateFromData(Chunk * 2, MaxObjects, false, Image.Format.Rgbaf, _scratch), layer);

        if (_metaDirty)
        {
            _metaTexture.Update(_metaImage);
            _metaDirty = false;
        }

        RenderingServer.GlobalShaderParameterSet("lc_head", _head);
        RenderingServer.GlobalShaderParameterSet("lc_count", _count);
        RenderingServer.GlobalShaderParameterSet("lc_tick", (int)(tick % TickWrap));
        RenderingServer.GlobalShaderParameterSet("lc_c", SimClock.Instance.C);

        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        UpdateMs = UpdateMs == 0 ? ms : UpdateMs * 0.95 + ms * 0.05;
    }

    private static void WriteSample(float[] data, HistoryTrack track, int col)
    {
        var s = track.Newest;
        int o = track.Row * Chunk * 2 * 4 + col * 4;
        data[o] = s.Position.X;
        data[o + 1] = s.Position.Y;
        data[o + 2] = s.Position.Z;
        data[o + 3] = track.NewestIsBreak ? 1f : 0f;
        data[o + 4] = s.Rotation.X;
        data[o + 5] = s.Rotation.Y;
        data[o + 6] = s.Rotation.Z;
        data[o + 7] = s.Rotation.W;
    }

    private void WriteMeta(HistoryTrack track)
    {
        track.MetaDirty = false;
        if (track.BirthTick < 0)
            return;
        _metaImage.SetPixel(0, track.Row, new Color(
            track.BirthTick % TickWrap,
            track.IsFinished ? track.LastTick % TickWrap : -1f,
            1f, 0f));
        _metaDirty = true;
    }

    private void ReleaseRow(HistoryTrack track)
    {
        _metaImage.SetPixel(0, track.Row, new Color(0, 0, 0, 0));
        _metaDirty = true;
        _freeRows.Push(track.Row);
    }
}
