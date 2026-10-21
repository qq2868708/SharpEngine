using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Mathematics;
using SharpEngine.World;

namespace SharpEngine.Editor;

/// <summary>A transient scene owned by an asset editor. It never writes an asset or the main World.</summary>
public sealed class AssetPreviewWorld
{
    private readonly ComponentRegistry Registry;
    private SkeletonInstance? Skeleton;
    private string? MeshId;
    private string? MeshComponentId;
    private SkeletonAsset? DisplaySkeleton;
    private readonly Dictionary<string, Vector3> BonePositions = new(StringComparer.Ordinal);

    public AssetPreviewWorld(ComponentRegistry Registry, string Id)
    {
        this.Registry = Registry;
        var Data = new SceneData
        {
            WorldId = Id,
            Name = "Asset Preview",
        };
        var Camera = Data.CreateEntity(Registry, "Preview Camera");
        Camera.EntityId = "preview-camera";
        Data.ActiveCameraId = Camera.EntityId;
        Data.AddComponent(Registry, Camera.EntityId, "engine.camera", new JsonObject
        {
            ["FieldOfView"] = 35,
            ["NearPlane"] = 0.01,
            ["FarPlane"] = 10000,
        });
        var Sky = Data.CreateEntity(Registry, "Preview Sky Light");
        Sky.EntityId = "preview-sky-light";
        Data.AddComponent(Registry, Sky.EntityId, "engine.sky-light", new JsonObject
        {
            ["Intensity"] = 0.45,
        });
        var Light = Data.CreateEntity(Registry, "Preview Key Light");
        Light.EntityId = "preview-key-light";
        Light.Components[0].State["Rotation"] = new JsonArray(-35, -35, 0);
        Data.AddComponent(Registry, Light.EntityId, "engine.directional-light", new JsonObject
        {
            ["Intensity"] = 0.8,
        });
        Data.Validate(Registry);
        World = new WorldDocument(Data, Registry);
    }

    public WorldDocument World
    {
        get;
    }

    public void SetMesh(string? AssetId, string ComponentType, string? MaterialId = null,
        SkeletalMeshAsset? SkeletalMesh = null)
    {
        var Data = World.Data.Clone();
        Data.Entities.RemoveAll(Entity => Entity.EntityId == "preview-model");
        MeshId = AssetId;
        MeshComponentId = null;
        Skeleton = null;
        DisplaySkeleton = null;
        BonePositions.Clear();
        if (AssetId is not null)
        {
            var Model = Data.CreateEntity(Registry, "Preview Model");
            Model.EntityId = "preview-model";
            var Added = Data.AddComponent(Registry, Model.EntityId, ComponentType, new JsonObject
            {
                ["Mesh"] = AssetId,
                ["Material"] = MaterialId,
            });
            MeshComponentId = Added.Single(Component => Component.TypeId == ComponentType).ComponentId;
            Skeleton = SkeletalMesh is null ? null : new SkeletonInstance(SkeletalMesh);
        }
        Data.Validate(Registry);
        World.Replace(Data);
        SetPose(null);
    }

    public void SetPose(SkeletalPose? Pose)
    {
        if (Skeleton is null || MeshComponentId is null)
        {
            return;
        }
        var Frame = Skeleton.Prepare("preview-model", MeshComponentId, Pose, "SKELETON_READY");
        World.RenderStates = new Dictionary<string, ComponentFrameState>(StringComparer.Ordinal)
        {
            [MeshComponentId] = Frame,
        };
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["context"] = "preview",
            ["transient"] = true,
            ["meshId"] = MeshId,
            ["world"] = World.Data.ToJson(),
        };
    }

    public IReadOnlyList<Vector3> SkeletonPositions
    {
        get
        {
            return BonePositions.Values.ToArray();
        }
    }

    public void SetSkeleton(SkeletonAsset Value)
    {
        SetMesh(null, "engine.skeletal-mesh");
        DisplaySkeleton = Value;
        var Bones = Value.Bones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
        var Transforms = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        Matrix4x4 Resolve(SkeletonBone Bone)
        {
            if (Transforms.TryGetValue(Bone.BoneId, out var Existing))
            {
                return Existing;
            }
            var Local = TransformMath.Trs(Bone.BindTransform.Position,
                Bone.BindTransform.Rotation, Bone.BindTransform.Scale);
            var Parent = Bone.ParentBoneId is null ? Matrix4x4.Identity : Resolve(Bones[Bone.ParentBoneId]);
            var Result = Local * Parent;
            Transforms.Add(Bone.BoneId, Result);
            BonePositions.Add(Bone.BoneId, Result.Translation);
            return Result;
        }
        foreach (var Bone in Value.Bones)
        {
            Resolve(Bone);
        }
    }

    public WorldViewportFrame Decorate(WorldViewportFrame Frame)
    {
        if (DisplaySkeleton is null || World.ActiveCamera is null)
        {
            return Frame;
        }
        var Matrix = SceneRenderer.CameraMatrix(World, World.ActiveCamera, Frame.Bounds.Width / Frame.Bounds.Height);
        var Lines = Frame.Lines.ToList();
        foreach (var Bone in DisplaySkeleton.Bones)
        {
            if (Bone.ParentBoneId is not null)
            {
                SceneRenderer.AddLine(Lines, BonePositions[Bone.ParentBoneId], BonePositions[Bone.BoneId],
                    Matrix, Frame.Bounds, new Vector4(0.91f, 0.72f, 0.35f, 1));
            }
        }
        return Frame with
        {
            Lines = Lines.ToArray(),
        };
    }
}
