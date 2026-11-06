namespace Lightspeed.History;

/// <summary>Bridges Godot math types and the engine-free System.Numerics types used by the solver.</summary>
public static class NumericsExtensions
{
    public static System.Numerics.Vector3 ToNumerics(this Godot.Vector3 v) => new(v.X, v.Y, v.Z);
    public static System.Numerics.Quaternion ToNumerics(this Godot.Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    public static Godot.Vector3 ToGodot(this System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    public static Godot.Quaternion ToGodot(this System.Numerics.Quaternion q) => new(q.X, q.Y, q.Z, q.W);
}
