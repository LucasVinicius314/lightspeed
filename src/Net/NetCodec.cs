using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Lightspeed.Net;

/// <summary>A player's pose as sent over the network: the camera, plus which mode it is in.</summary>
public readonly record struct PoseState(byte Mode, Vector3 Position, Quaternion Rotation);

/// <summary>A physics body's pose in a snapshot.</summary>
public readonly record struct BodyPose(int Id, Vector3 Position, Quaternion Rotation);

/// <summary>A dynamic body as described to a client that just joined.</summary>
public readonly record struct BodyDescription(int Id, int Kind, Color Color, Vector3 Position, Quaternion Rotation);

/// <summary>
/// Compact binary encoding for the unreliable per-frame messages. Decoding never trusts its input: a short,
/// oversized or non-finite message is dropped (the methods return false), never thrown on.
/// </summary>
internal static class NetCodec
{
    private const int MaxEntries = 2000;

    public static byte[] EncodePose(PoseState s)
    {
        using var ms = new MemoryStream(29);
        using var w = new BinaryWriter(ms);
        w.Write(s.Mode);
        WritePose(w, s.Position, s.Rotation);
        w.Flush();
        return ms.ToArray();
    }

    public static bool TryDecodePose(byte[] data, out PoseState state)
    {
        state = default;
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            byte mode = r.ReadByte();
            if (!ReadPose(r, out var pos, out var rot))
                return false;
            state = new PoseState(mode, pos, rot);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>One snapshot message. The host sends the players in the first message of a snapshot only.</summary>
    public static byte[] EncodeSnapshot(double worldTime, IReadOnlyList<(int Id, PoseState Pose)> players, IReadOnlyList<BodyPose> bodies)
    {
        using var ms = new MemoryStream(16 + players.Count * 33 + bodies.Count * 32);
        using var w = new BinaryWriter(ms);
        w.Write(worldTime);
        w.Write((ushort)players.Count);
        foreach (var (id, pose) in players)
        {
            w.Write(id);
            w.Write(pose.Mode);
            WritePose(w, pose.Position, pose.Rotation);
        }
        w.Write((ushort)bodies.Count);
        foreach (var b in bodies)
        {
            w.Write(b.Id);
            WritePose(w, b.Position, b.Rotation);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static bool TryDecodeSnapshot(byte[] data, out double worldTime, out List<(int Id, PoseState Pose)> players, out List<BodyPose> bodies)
    {
        worldTime = 0;
        players = new();
        bodies = new();
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            worldTime = r.ReadDouble();
            if (!double.IsFinite(worldTime))
                return false;
            int playerCount = r.ReadUInt16();
            if (playerCount > MaxEntries)
                return false;
            for (int i = 0; i < playerCount; i++)
            {
                int id = r.ReadInt32();
                byte mode = r.ReadByte();
                if (!ReadPose(r, out var pos, out var rot))
                    return false;
                players.Add((id, new PoseState(mode, pos, rot)));
            }
            int bodyCount = r.ReadUInt16();
            if (bodyCount > MaxEntries)
                return false;
            for (int i = 0; i < bodyCount; i++)
            {
                int id = r.ReadInt32();
                if (!ReadPose(r, out var pos, out var rot))
                    return false;
                bodies.Add(new BodyPose(id, pos, rot));
            }
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    public static byte[] EncodeBodies(IReadOnlyList<BodyDescription> bodies)
    {
        using var ms = new MemoryStream(2 + bodies.Count * 48);
        using var w = new BinaryWriter(ms);
        w.Write((ushort)bodies.Count);
        foreach (var b in bodies)
        {
            w.Write(b.Id);
            w.Write((byte)b.Kind);
            w.Write(b.Color.R);
            w.Write(b.Color.G);
            w.Write(b.Color.B);
            WritePose(w, b.Position, b.Rotation);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static bool TryDecodeBodies(byte[] data, out List<BodyDescription> bodies)
    {
        bodies = new();
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            int count = r.ReadUInt16();
            if (count > MaxEntries)
                return false;
            for (int i = 0; i < count; i++)
            {
                int id = r.ReadInt32();
                int kind = r.ReadByte();
                var color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                if (!ReadPose(r, out var pos, out var rot))
                    return false;
                bodies.Add(new BodyDescription(id, kind, color, pos, rot));
            }
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private static void WritePose(BinaryWriter w, Vector3 p, Quaternion q)
    {
        w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
        w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W);
    }

    private static bool ReadPose(BinaryReader r, out Vector3 position, out Quaternion rotation)
    {
        position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        bool finite = float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
                      && float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) && float.IsFinite(rotation.Z) && float.IsFinite(rotation.W);
        if (!finite || rotation.LengthSquared() < 0.5f)
            return false;
        rotation = rotation.Normalized();
        return true;
    }
}
