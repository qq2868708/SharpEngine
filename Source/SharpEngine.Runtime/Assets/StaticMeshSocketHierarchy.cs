using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Mathematics;

namespace SharpEngine.Assets;

/// <summary>Immutable Socket definitions and their validated Mesh-space frames. The Mesh root is identity.</summary>
public sealed class StaticMeshSocketHierarchy
{
    private readonly IReadOnlyDictionary<string, StaticMeshSocket[]> Children;

    public IReadOnlyList<StaticMeshSocket> Sockets
    {
        get;
    }

    public IReadOnlyDictionary<string, Matrix4x4> ModelMatrices
    {
        get;
    }

    public StaticMeshSocketHierarchy(IReadOnlyList<StaticMeshSocket> Sockets)
    {
        ArgumentNullException.ThrowIfNull(Sockets);
        this.Sockets = Array.AsReadOnly(Sockets.ToArray());
        var ById = new Dictionary<string, StaticMeshSocket>(StringComparer.Ordinal);
        var Names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Socket in this.Sockets)
        {
            if (Socket is null || string.IsNullOrWhiteSpace(Socket.SocketId) ||
                Socket.SocketId != Socket.SocketId.Trim() || !ById.TryAdd(Socket.SocketId, Socket) ||
                string.IsNullOrWhiteSpace(Socket.Name) || Socket.Name != Socket.Name.Trim() || !Names.Add(Socket.Name))
            {
                throw new InvalidDataException("STATIC_MESH_SOCKET_IDENTITY: Socket IDs and names must be non-empty and unique within the asset");
            }
            var Length = Socket.Rotation.LengthSquared();
            if (!VectorMath.IsFinite(Socket.Position) || !VectorMath.IsFinite(Socket.Scale) ||
                !float.IsFinite(Socket.Rotation.X) || !float.IsFinite(Socket.Rotation.Y) ||
                !float.IsFinite(Socket.Rotation.Z) || !float.IsFinite(Socket.Rotation.W) ||
                Length < 0.999f || Length > 1.001f || Socket.Scale.X <= 0 || Socket.Scale.Y <= 0 || Socket.Scale.Z <= 0)
            {
                throw new InvalidDataException($"STATIC_MESH_SOCKET_TRANSFORM: sockets/{Socket.SocketId} requires finite TRS, a unit quaternion and positive scale");
            }
        }
        foreach (var Socket in this.Sockets)
        {
            if (Socket.ParentSocketId is not null && !ById.ContainsKey(Socket.ParentSocketId))
            {
                throw new InvalidDataException($"STATIC_MESH_SOCKET_PARENT: sockets/{Socket.SocketId}/parentSocketId must reference an existing Socket or null for the Mesh root");
            }
        }
        Children = this.Sockets.GroupBy(Socket => Socket.ParentSocketId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(Group => Group.Key, Group => Group.ToArray(), StringComparer.Ordinal);
        var Models = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        var Pending = new Queue<StaticMeshSocket>(Children.GetValueOrDefault(string.Empty) ?? []);
        while (Pending.Count != 0)
        {
            var Socket = Pending.Dequeue();
            var Parent = Socket.ParentSocketId is null ? Matrix4x4.Identity : Models[Socket.ParentSocketId];
            var Model = Socket.LocalMatrix * Parent;
            var Invertible = TransformMath.TryInverse(Model, out _);
            if (!Invertible)
            {
                throw new InvalidDataException($"STATIC_MESH_SOCKET_TRANSFORM: sockets/{Socket.SocketId} composes to a non-finite or non-invertible Mesh-space frame");
            }
            Models.Add(Socket.SocketId, Model);
            foreach (var Child in Children.GetValueOrDefault(Socket.SocketId) ?? [])
            {
                Pending.Enqueue(Child);
            }
        }
        if (Models.Count != this.Sockets.Count)
        {
            throw new InvalidDataException("STATIC_MESH_SOCKET_CYCLE: Socket parents must form an acyclic hierarchy below the Mesh root");
        }
        ModelMatrices = new ReadOnlyDictionary<string, Matrix4x4>(Models);
    }

    /// <summary>Returns whether an existing Socket directly owns child Sockets.</summary>
    public bool HasChildren(string SocketId) { return ModelMatrices.ContainsKey(SocketId) && Children.ContainsKey(SocketId); }

    /// <summary>Returns the selected Socket and its descendants in deterministic breadth-first order.</summary>
    public IReadOnlyList<string> GetSubtreeIds(string SocketId)
    {
        if (!ModelMatrices.ContainsKey(SocketId))
        {
            throw new InvalidDataException("STATIC_MESH_SOCKET_SELECTION: Socket does not exist");
        }
        var Result = new List<string>();
        var Pending = new Queue<string>();
        Pending.Enqueue(SocketId);
        while (Pending.Count != 0)
        {
            var Id = Pending.Dequeue();
            Result.Add(Id);
            foreach (var Child in Children.GetValueOrDefault(Id) ?? [])
            {
                Pending.Enqueue(Child.SocketId);
            }
        }
        return Result;
    }

    public JsonObject ToJson()
    {
        var Models = new JsonObject();
        foreach (var Entry in ModelMatrices.OrderBy(Entry => Entry.Key, StringComparer.Ordinal))
        {
            var Matrix = Entry.Value;
            Models[Entry.Key] = new JsonArray(Matrix.M11, Matrix.M12, Matrix.M13, Matrix.M14,
                Matrix.M21, Matrix.M22, Matrix.M23, Matrix.M24, Matrix.M31, Matrix.M32, Matrix.M33, Matrix.M34,
                Matrix.M41, Matrix.M42, Matrix.M43, Matrix.M44);
        }
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["socketModelMatrices"] = Models,
        };
    }
}
