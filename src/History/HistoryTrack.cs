using System;
using System.Numerics;

namespace Lightspeed.History;

/// <summary>
/// One object's recorded history: a ring buffer of per-tick samples plus its lifetime (first and last
/// sample tick) and where it lives in the history texture. Engine-free so it can be unit-tested, and
/// it outlives the object's node so a despawned object can still be seen until its last light passes.
/// </summary>
public sealed class HistoryTrack : ISampleSource
{
    private readonly Sample[] _ring;
    private readonly bool[] _breaks;
    private int _head = -1;
    private int _count;

    // Linear and angular speed between consecutive samples over the last SpeedWindow ticks.
    private const int SpeedWindow = 120;
    private readonly float[] _linear = new float[SpeedWindow];
    private readonly float[] _angular = new float[SpeedWindow];
    private readonly double _tickSeconds;
    private int _speedHead;

    public HistoryTrack(int capacity, int row, double tickSeconds = 1.0 / 60)
    {
        _ring = new Sample[capacity];
        _breaks = new bool[capacity];
        _tickSeconds = tickSeconds;
        Row = row;
    }

    /// <summary>Corners of the box holding every position the part has been recorded at.</summary>
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    /// <summary>Fastest linear speed (m/s) between consecutive samples over about the last 2 s.</summary>
    public float PeakLinearSpeed { get; private set; }

    /// <summary>Fastest rotation speed (rad/s) between consecutive samples over about the last 2 s.</summary>
    public float PeakAngularSpeed { get; private set; }

    /// <summary>
    /// Upper bound on the speed of any point within <paramref name="radius"/> of the object's origin:
    /// the origin's speed plus the spin speed at that radius.
    /// </summary>
    public float PeakVertexSpeed(float radius) => PeakLinearSpeed + PeakAngularSpeed * radius;

    /// <summary>Row of this object in the history texture.</summary>
    public int Row { get; }

    public int Capacity => _ring.Length;
    public int Count => _count;

    /// <summary>Tick of the first recorded sample, or -1 before any.</summary>
    public long BirthTick { get; private set; } = -1;

    /// <summary>Tick of the newest recorded sample, or -1 before any.</summary>
    public long LastTick { get; private set; } = -1;

    /// <summary>The object was despawned: no more samples will be recorded.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>Birth or death changed and the texture's metadata row needs rewriting.</summary>
    public bool MetaDirty { get; set; }

    public Sample this[int k] => _ring[Slot(k)];

    public Sample Newest => this[_count - 1];
    public bool NewestIsBreak => _count > 0 && _breaks[Slot(_count - 1)];

    public bool BreaksBefore(int k) => k > 0 && _breaks[Slot(k)];

    /// <summary>
    /// Records the pose for <paramref name="tick"/>. Pass <paramref name="isBreak"/> for a teleport so the
    /// solver does not interpolate from the previous sample. Skipped ticks are filled with the last pose.
    /// </summary>
    public void Record(long tick, Sample sample, bool isBreak = false)
    {
        if (IsFinished)
            return;

        if (_count == 0)
        {
            BirthTick = tick;
            MetaDirty = true;
        }
        else if (tick <= LastTick)
        {
            // Same tick recorded twice: the later pose wins.
            _ring[_head] = sample;
            _breaks[_head] = isBreak || _breaks[_head];
            return;
        }
        else
        {
            for (long t = LastTick + 1; t < tick; t++)
                Push(_ring[_head], false);
        }

        Push(sample, isBreak);
        LastTick = tick;
    }

    /// <summary>The object stopped existing after its newest sample.</summary>
    public void Retire()
    {
        if (IsFinished)
            return;
        IsFinished = true;
        MetaDirty = true;
    }

    /// <summary>
    /// A despawned object stays visible until its newest sample is older than the history window
    /// (the longest any light can still be on its way); after that the row can be reused.
    /// </summary>
    public bool IsExpired(long currentTick) =>
        IsFinished && (BirthTick < 0 || currentTick - LastTick >= Capacity);

    private void Push(Sample sample, bool isBreak)
    {
        RecordSpeed(sample, isBreak);
        BoundsMin = _count == 0 ? sample.Position : Vector3.Min(BoundsMin, sample.Position);
        BoundsMax = _count == 0 ? sample.Position : Vector3.Max(BoundsMax, sample.Position);
        _head = (_head + 1) % _ring.Length;
        _ring[_head] = sample;
        _breaks[_head] = isBreak;
        if (_count < _ring.Length)
            _count++;
    }

    private void RecordSpeed(Sample sample, bool isBreak)
    {
        float linear = 0f, angular = 0f;
        if (_count > 0 && !isBreak) // a teleport is not motion
        {
            var prev = _ring[_head];
            linear = (float)(Vector3.Distance(prev.Position, sample.Position) / _tickSeconds);
            float dot = MathF.Min(1f, MathF.Abs(Quaternion.Dot(prev.Rotation, sample.Rotation)));
            angular = (float)(2.0 * Math.Acos(dot) / _tickSeconds);
        }
        _linear[_speedHead] = linear;
        _angular[_speedHead] = angular;
        _speedHead = (_speedHead + 1) % SpeedWindow;

        float peakLinear = 0f, peakAngular = 0f;
        for (int i = 0; i < SpeedWindow; i++)
        {
            peakLinear = MathF.Max(peakLinear, _linear[i]);
            peakAngular = MathF.Max(peakAngular, _angular[i]);
        }
        PeakLinearSpeed = peakLinear;
        PeakAngularSpeed = peakAngular;
    }

    private int Slot(int k)
    {
        if ((uint)k >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(k));
        return (_head - (_count - 1) + k + _ring.Length) % _ring.Length;
    }
}
