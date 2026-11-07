using System.Numerics;
using SharpEngine.Assets;
using SharpEngine.Mathematics;

internal static class StaticMeshHierarchyValidation
{
    internal static void Run(StaticMeshAsset Mesh, Action<bool, string> Check)
    {
        var Parent = new StaticMeshSocket("parent", "Parent", new Vector3(1, 2, 3),
            QuaternionMath.Euler(new Vector3(15, 35, -5)), new Vector3(2, 0.75f, 1.25f));
        var Child = new StaticMeshSocket("child", "Child", new Vector3(0.2f, 0.1f, -0.3f),
            QuaternionMath.Euler(new Vector3(5, 20, 0)), new Vector3(1.2f, 1, 0.8f), "parent");
        var Leaf = new StaticMeshSocket("leaf", "Leaf", new Vector3(0.1f, 0, 0), Quaternion.Identity, Vector3.One, "child");
        var Hierarchy = new StaticMeshSocketHierarchy([Leaf, Child, Parent]);
        Check(Near(Hierarchy.ModelMatrices["child"], Child.LocalMatrix * Parent.LocalMatrix) &&
            Near(Hierarchy.ModelMatrices["leaf"], Leaf.LocalMatrix * Child.LocalMatrix * Parent.LocalMatrix),
            "Out-of-order Socket records compose local * parent frames through rotation and non-uniform scale");
        var Moved = new StaticMeshSocketHierarchy([Leaf, Child, Parent with
        {
            Position = Parent.Position + Vector3.UnitX,
        }]);
        Check(Vector3.Distance(Moved.ModelMatrices["leaf"].Translation - Hierarchy.ModelMatrices["leaf"].Translation, Vector3.UnitX) < 0.0001f &&
            Moved.Sockets[0] == Leaf, "Moving an ancestor updates descendant frames without rewriting descendant local TRS");
        Check(Hierarchy.GetSubtreeIds("parent").SequenceEqual(new[]
        {
            "parent", "child", "leaf",
        }), "Subtree discovery includes the selected Socket and all descendants in stable order");
        Check(Hierarchy.HasChildren("parent") && Hierarchy.HasChildren("child") && !Hierarchy.HasChildren("leaf") && !Hierarchy.HasChildren(string.Empty),
            "Leaf deletion guards distinguish Socket parents from leaves and the implicit Mesh root");
        var Asset = new StaticMeshAsset(Mesh.AssetId, Mesh.Name, Mesh.Primitive, Mesh.CoordinateSystem,
            Mesh.Positions, Mesh.Indices, Mesh.DefaultMaterial, Sockets: Hierarchy.Sockets);
        var Loaded = StaticMeshAsset.Parse(Asset.Serialize(false));
        Check(Loaded.Serialize(false) == Asset.Serialize(false) && Loaded.Sockets[0].ParentSocketId == "child" &&
            Near(Loaded.SocketHierarchy.ModelMatrices["leaf"], Hierarchy.ModelMatrices["leaf"]),
            "Socket parent references and inherited frames round-trip deterministically in schema v3");
        var Legacy = Asset.ToJson();
        Legacy["schemaVersion"] = 2;
        foreach (var Socket in Legacy["sockets"]!.AsArray())
        {
            Socket!.AsObject().Remove("parentSocketId");
        }
        var V2 = StaticMeshAsset.Parse(Legacy.ToJsonString());
        Check(V2.Sockets.All(Socket => Socket.ParentSocketId is null) &&
            V2.ToJson()["sockets"]!.AsArray().All(Socket => Socket!.AsObject().ContainsKey("parentSocketId")),
            "Flat schema v2 sockets migrate explicitly to children of the immutable Mesh root");
        var MissingParentField = Asset.ToJson();
        MissingParentField["sockets"]![0]!.AsObject().Remove("parentSocketId");
        Reject(() => StaticMeshAsset.Parse(MissingParentField.ToJsonString()), "Schema v3 requires an explicit parentSocketId, including null roots");
        Reject(() => new StaticMeshSocketHierarchy([Parent with
        {
            ParentSocketId = "missing",
        }]), "Missing parents are rejected");
        Reject(() => new StaticMeshSocketHierarchy([Parent with
        {
            ParentSocketId = "parent",
        }]), "Self-parenting is rejected");
        Reject(() => new StaticMeshSocketHierarchy([Parent with
        {
            ParentSocketId = "leaf",
        }, Child, Leaf]), "Multi-level parenting cycles are rejected");
        Reject(() => new StaticMeshSocketHierarchy([Parent with
        {
            Scale = new Vector3(1e12f),
        }, Child with
        {
            Scale = new Vector3(1e12f),
        }]), "Non-finite or non-invertible accumulated frames are rejected");
        var Deep = Enumerable.Range(0, 4096).Select(Index => new StaticMeshSocket("node-" + Index, "Node " + Index,
            new Vector3(0.0001f, 0, 0), Quaternion.Identity, Vector3.One, Index == 0 ? null : "node-" + (Index - 1))).Reverse().ToArray();
        var DeepHierarchy = new StaticMeshSocketHierarchy(Deep);
        Check(DeepHierarchy.GetSubtreeIds("node-0").Count == 4096 &&
            Math.Abs(DeepHierarchy.ModelMatrices["node-4095"].Translation.X - 0.4096f) < 0.0001f,
            "Deep flat-serialized hierarchies evaluate and enumerate without recursive stack growth");
        Check(Hierarchy.ToJson().ToJsonString() == new StaticMeshSocketHierarchy(Hierarchy.Sockets).ToJson().ToJsonString(),
            "Derived Socket pose snapshots are deterministic and independent of instance caches");

        void Reject(Action Operation, string Message)
        {
            var Rejected = false;
            try
            {
                Operation();
            }
            catch (InvalidDataException)
            {
                Rejected = true;
            }
            Check(Rejected, Message);
        }
    }

    private static bool Near(Matrix4x4 First, Matrix4x4 Second)
    {
        var Delta = new[]
        {
            First.M11 - Second.M11, First.M12 - Second.M12, First.M13 - Second.M13, First.M14 - Second.M14,
            First.M21 - Second.M21, First.M22 - Second.M22, First.M23 - Second.M23, First.M24 - Second.M24,
            First.M31 - Second.M31, First.M32 - Second.M32, First.M33 - Second.M33, First.M34 - Second.M34,
            First.M41 - Second.M41, First.M42 - Second.M42, First.M43 - Second.M43, First.M44 - Second.M44,
        };
        return Delta.All(Value => Math.Abs(Value) < 0.0001f);
    }
}
