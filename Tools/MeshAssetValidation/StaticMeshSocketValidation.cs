using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Mathematics;

internal static class StaticMeshSocketValidation
{
    internal static void Run(StaticMeshAsset Legacy, Action<bool, string> Check)
    {
        Check(Legacy.Sockets.Count == 0, "Legacy static meshes load with no sockets");
        var Empty = Legacy.ToJson();
        Check(Empty["schemaVersion"]!.GetValue<int>() == 3 && Empty["sockets"]!.AsArray().Count == 0,
            "Static mesh v3 explicitly serializes an empty socket array");
        var Socket = new StaticMeshSocket("grip", "Grip", new Vector3(1, 2, 3),
            QuaternionMath.Euler(new Vector3(10, 25, 50)), new Vector3(1, 2, 0.5f));
        var Input = new List<StaticMeshSocket>
        {
            Socket,
        };
        var Asset = new StaticMeshAsset(Legacy.AssetId, Legacy.Name, Legacy.Primitive, Legacy.CoordinateSystem,
            Legacy.Positions, Legacy.Indices, Legacy.DefaultMaterial, Sockets: Input);
        Input.Clear();
        var Loaded = StaticMeshAsset.Parse(Asset.Serialize(false));
        Check(Loaded.Sockets.Count == 1 && Loaded.Sockets[0] == Socket &&
            Loaded.Serialize(false) == Asset.Serialize(false), "Socket non-default TRS round-trips deterministically and copies the input collection");
        Check(Vector3.Transform(Vector3.Zero, Socket.LocalMatrix) == Socket.Position &&
            Asset.ToMeshDescription().Positions.SequenceEqual(Legacy.Positions),
            "Socket frames use mesh-local TRS without changing renderer geometry");
        var DefaultSocket = Socket with
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        var DefaultAsset = new StaticMeshAsset(Legacy.AssetId, Legacy.Name, Legacy.Primitive, Legacy.CoordinateSystem,
            Legacy.Positions, Legacy.Indices, Sockets: [DefaultSocket]);
        Check(StaticMeshAsset.Parse(DefaultAsset.Serialize()).Sockets[0] == DefaultSocket,
            "Default Socket TRS is explicitly saved and reconstructed");
        foreach (var Mutate in new Action<JsonObject>[]
        {
            Root => Root["schemaVersion"] = 4,
            Root => Root.Remove("sockets"),
            Root => Root["sockets"] = null,
            Root => Root["sockets"]!.AsArray().Add(Root["sockets"]![0]!.DeepClone()),
            Root => Root["sockets"]![0]!["name"] = " ",
            Root => Root["sockets"]![0]!["socketId"] = " grip ",
            Root => Root["sockets"]![0]!["scale"] = new JsonArray(0, 1, 1),
            Root => Root["sockets"]![0]!["rotation"] = new JsonArray(0, 0, 0, 0),
            Root => Root["sockets"]![0]!["rotation"] = new JsonArray(0, 0, 1),
            Root => Root["sockets"]![0]!["position"] = new JsonArray(1e40, 0, 0),
            Root => Root["sockets"]![0]!["unknown"] = true,
            Root => Root["schemaVersion"] = 1,
        })
        {
            var Candidate = Asset.ToJson();
            Mutate(Candidate);
            var Rejected = false;
            try
            {
                StaticMeshAsset.Parse(Candidate.ToJsonString());
            }
            catch (InvalidDataException)
            {
                Rejected = true;
            }
            Check(Rejected, "Invalid socket schema or transform is rejected without silently losing data");
        }
        var DuplicateJson = Asset.Serialize(false).Replace("\"socketId\":\"grip\"", "\"socketId\":\"grip\",\"socketId\":\"other\"", StringComparison.Ordinal);
        var DuplicateRejected = false;
        try
        {
            StaticMeshAsset.Parse(DuplicateJson);
        }
        catch (InvalidDataException)
        {
            DuplicateRejected = true;
        }
        Check(DuplicateRejected, "Duplicate socket JSON properties are rejected");
        StaticMeshHierarchyValidation.Run(Legacy, Check);
    }
}
