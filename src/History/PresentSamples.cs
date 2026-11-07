using System.Numerics;

namespace Lightspeed.History;

/// <summary>
/// A track with one more, virtual sample: the pose one tick after the newest, extrapolated from the last two.
/// The retarded moment of a nearby part (close to the camera, or any part when c is large) falls after the newest real
/// sample, in the fraction of a tick that has passed since; without this the solver has to clamp to the newest sample,
/// so near parts step at the tick rate while the camera moves smoothly, and a ray cannot reach surfaces closer than
/// c x age. With the virtual sample every algorithm works unchanged, because the delay formula
/// <c>(Count - 1 - k) dt + age</c> already gives the right (negative) delay for it: pass <see cref="EffectiveAge"/>
/// instead of the age. A finished (despawned) part is not extended: it has no present. CPU twin of what
/// <c>fetch_o</c> in shaders/lc_common.gdshaderinc does for index <c>lc_count</c>.
/// </summary>
public sealed class PresentSamples : ISampleSource
{
    private ISampleSource _inner = null!;

    /// <summary>The real track. Set it each time before use; the wrapper reads its count and state live.</summary>
    public ISampleSource Inner
    {
        get => _inner;
        set => _inner = value;
    }

    private bool Extends => !_inner.IsFinished && _inner.Count >= 2;

    public int Count => _inner.Count + (Extends ? 1 : 0);

    public bool IsFinished => _inner.IsFinished;

    public Sample this[int k]
    {
        get
        {
            int n = _inner.Count;
            if (k < n)
                return _inner[k];
            var last = _inner[n - 1];
            if (_inner.BreaksBefore(n - 1))
                return last; // the newest sample is a teleport: there is no velocity to extrapolate, hold still
            var before = _inner[n - 2];
            return new Sample(
                Vector3.Lerp(before.Position, last.Position, 2f),
                Quaternion.Lerp(before.Rotation, last.Rotation, 2f)); // normalised, sign-corrected extrapolation
        }
    }

    public bool BreaksBefore(int k) => k < _inner.Count && _inner.BreaksBefore(k);

    /// <summary>The age to pass to the solver with this source: the real age is measured from the newest real sample, this is measured from the virtual one.</summary>
    public double EffectiveAge(double age, double dt) => Extends ? age - dt : age;
}
