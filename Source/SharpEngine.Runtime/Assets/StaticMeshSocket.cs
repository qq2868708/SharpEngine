using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Mathematics;

namespace SharpEngine.Assets;

/// <summary>A named frame local to its parent Socket, or the immutable Mesh root when ParentSocketId is null.</summary>
public sealed record StaticMeshSocket(string SocketId, string Name, Vector3 Position,
    Quaternion Rotation, Vector3 Scale, string? ParentSocketId = null)
{
    public Matrix4x4 LocalMatrix
    {
        get
        {
            return TransformMath.Trs(Position, Rotation, Scale);
        }
    }
}

public sealed partial class StaticMeshAsset
{
    public IReadOnlyList<StaticMeshSocket> Sockets
    {
        get;
    }

    public StaticMeshSocketHierarchy SocketHierarchy
    {
        get;
    }

    private static JsonObject SocketJson(StaticMeshSocket Socket)
    {
        return new JsonObject
        {
            ["socketId"] = Socket.SocketId,
            ["name"] = Socket.Name,
            ["parentSocketId"] = Socket.ParentSocketId,
            ["position"] = new JsonArray(Socket.Position.X, Socket.Position.Y, Socket.Position.Z),
            ["rotation"] = new JsonArray(Socket.Rotation.X, Socket.Rotation.Y, Socket.Rotation.Z, Socket.Rotation.W),
            ["scale"] = new JsonArray(Socket.Scale.X, Socket.Scale.Y, Socket.Scale.Z),
        };
    }

    private static StaticMeshSocket ParseSocket(JsonNode? Node, int Version)
    {
        var Value = Node as JsonObject ?? throw new InvalidDataException("STATIC_MESH_SOCKET_SCHEMA: socket must be an object");
        if (Version == 2)
        {
            Exact(Value, "socketId", "name", "position", "rotation", "scale");
        }
        else
        {
            Exact(Value, "socketId", "name", "parentSocketId", "position", "rotation", "scale");
        }
        var Position = ParseVector3(Value["position"], "position");
        var Scale = ParseVector3(Value["scale"], "scale");
        var Rotation = Value["rotation"] as JsonArray;
        if (Rotation is null || Rotation.Count != 4)
        {
            throw new InvalidDataException("STATIC_MESH_SOCKET_TRANSFORM: rotation must contain four numbers");
        }
        return new StaticMeshSocket(Value["socketId"]!.GetValue<string>(), Value["name"]!.GetValue<string>(),
            new Vector3(Position.X, Position.Y, Position.Z),
            new Quaternion(Number(Rotation[0], "rotation"), Number(Rotation[1], "rotation"),
                Number(Rotation[2], "rotation"), Number(Rotation[3], "rotation")),
            new Vector3(Scale.X, Scale.Y, Scale.Z), Version == 2 ? null : Value["parentSocketId"]?.GetValue<string>());
    }
}
