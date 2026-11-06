using Godot;

namespace Lightspeed.History;

/// <summary>
/// Records the parent Node3D's pose once per tick into a ring buffer.
/// Runs after all movement (high priority value) so the sample matches the tick's time.
/// </summary>
public partial class LightHistory : Node, ISampleSource
{
    private Sample[] _ring = null!;
    private int _head = -1;
    private int _count;

    /// <summary>Row of this object in the history texture.</summary>
    public int ObjectIndex { get; private set; }

    public int Count => _count;

    public Sample this[int k]
    {
        get
        {
            int cap = _ring.Length;
            return _ring[(_head - (_count - 1) + k + cap) % cap];
        }
    }

    public override void _Ready()
    {
        ProcessPhysicsPriority = 1000;
        _ring = new Sample[HistoryTexture.Capacity];
        ObjectIndex = HistoryTexture.Instance.Register(this);
    }

    public override void _PhysicsProcess(double delta)
    {
        var xf = GetParent<Node3D>().GlobalTransform;
        _head = (_head + 1) % _ring.Length;
        _ring[_head] = new Sample(xf.Origin.ToNumerics(), xf.Basis.GetRotationQuaternion().ToNumerics());
        if (_count < _ring.Length)
            _count++;
    }
}
