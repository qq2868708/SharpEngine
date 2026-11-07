using System.Numerics;
using SharpEngine.Rendering;
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
    private SkeletonRigPose? RigPose;

    public AssetPreviewWorld(ComponentRegistry Registry, string Id)
    {
        this.Registry = Registry;
        var Data = new SceneData
        {
            WorldId = Id,
            Name = "Asset Preview",
        };
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
        SkeletalMeshAsset? SkeletalMesh = null, SkeletonAsset? Definition = null)
    {
        var Data = World.Data.Clone();
        Data.Entities.RemoveAll(Entity => Entity.EntityId == "preview-model");
        MeshId = AssetId;
        MeshComponentId = null;
        Skeleton = null;
        DisplaySkeleton = null;
        RigPose = null;
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
            Skeleton = SkeletalMesh is null ? null : new SkeletonInstance(SkeletalMesh, Definition);
        }
        Data.Validate(Registry);
        World.Replace(Data);
        SetPose(null);
        if (Definition is not null)
        {
            DisplaySkeleton = Definition;
            RigPose = new SkeletonRigPose(Definition);
            UpdateBonePositions();
        }
    }

    public void SetPose(SkeletalPose? Pose)
    {
        if (Skeleton is null || MeshComponentId is null)
        {
            return;
        }
        var Frame = Skeleton.Prepare("preview-model", MeshComponentId, Pose, "SKELETON_READY");
        RigPose = Frame.RigPose;
        UpdateBonePositions();
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
            return BonePositions.Values.Concat(RigPose?.SocketModelMatrices.Values.Select(Matrix => Matrix.Translation) ?? []).ToArray();
        }
    }

    public void SetSkeleton(SkeletonAsset Value)
    {
        SetMesh(null, "engine.skeletal-mesh");
        DisplaySkeleton = Value;
        RigPose = new SkeletonRigPose(Value);
        UpdateBonePositions();
    }

    /// <summary>Change only the derived rig overlay during an authoring gesture, retaining the preview mesh.</summary>
    public void SetRigPreview(SkeletonAsset Value)
    {
        DisplaySkeleton = Value;
        RigPose = new SkeletonRigPose(Value);
        UpdateBonePositions();
    }

    private void UpdateBonePositions()
    {
        BonePositions.Clear();
        if (RigPose is null)
        {
            return;
        }
        foreach (var Bone in RigPose.BoneModelMatrices)
        {
            BonePositions.Add(Bone.Key, Bone.Value.Translation);
        }
    }

    public ViewportFrame Decorate(SharpEngine.UI.ViewportRenderContext Context, ViewportFrame Frame)
    {
        if (DisplaySkeleton is null)
        {
            return Frame;
        }
        var Matrix = Context.Camera.ViewProjection(Frame.Bounds.Width / Frame.Bounds.Height);
        var Lines = Frame.Lines.ToList();
        foreach (var Bone in DisplaySkeleton.Bones)
        {
            if (Bone.ParentBoneId is not null)
            {
                SceneRenderer.AddLine(Lines, BonePositions[Bone.ParentBoneId], BonePositions[Bone.BoneId],
                    Matrix, Frame.Bounds, new Vector4(0.91f, 0.72f, 0.35f, 1));
            }
        }
        foreach (var Bone in DisplaySkeleton.VirtualBones)
        {
            SceneRenderer.AddLine(Lines, BonePositions[Bone.SourceBoneId], BonePositions[Bone.BoneId],
                Matrix, Frame.Bounds, new Vector4(0.2f, 0.8f, 1, 1));
        }
        if (RigPose is not null)
        {
            foreach (var Socket in DisplaySkeleton.Sockets)
            {
                var Transform = RigPose.SocketModelMatrices[Socket.SocketId];
                var Origin = Transform.Translation;
                SceneRenderer.AddLine(Lines, BonePositions[Socket.ParentBoneId], Origin,
                    Matrix, Frame.Bounds, new Vector4(0.8f, 0.4f, 1, 1));
                var Axes = new[]
                {
                    Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
                };
                var Colors = new[]
                {
                    new Vector4(1, 0.2f, 0.2f, 1), new Vector4(0.2f, 1, 0.2f, 1), new Vector4(0.2f, 0.5f, 1, 1),
                };
                for (var Index = 0; Index < Axes.Length; Index++)
                {
                    var End = Origin + VectorMath.NormalizeOrZero(Vector3.TransformNormal(Axes[Index], Transform)) * 0.15f;
                    SceneRenderer.AddLine(Lines, Origin, End, Matrix, Frame.Bounds, Colors[Index]);
                }
            }
        }
        return Frame with
        {
            Lines = Lines.ToArray(),
        };
    }
}
