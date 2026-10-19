using System.Numerics;
using SharpEngine.Animation;
using SharpEngine.Assets;
using SharpEngine.Mathematics;

var Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var Registry = AssetRegistry.Load(Root);
if (Registry.Diagnostics.Any(Diagnostic => Diagnostic.IsError))
{
    throw new InvalidOperationException(string.Join("; ", Registry.Diagnostics.Select(Diagnostic => Diagnostic.Message)));
}

var SourceSkeleton = SkeletonContent.Load(Registry, "engine://skeletons/uefn-mannequin");
var SourceMesh = SkeletalMeshContent.Load(Registry, "engine://meshes/uefn-mannequin");
var SourceAnimation = SkeletalAnimationContent.Load(Registry, "engine://animations/uefn-mannequin-walk");
SkeletalAnimationCompatibility.Validate(SourceAnimation, SourceMesh);
var TargetBones = SourceSkeleton.Bones.Select(Bone => Bone.BoneId == "pelvis"
    ? Bone with
    {
        BindTransform = Bone.BindTransform with
        {
            Position = Bone.BindTransform.Position + new Vector3(0, 0.05f, 0),
        },
    }
    : Bone).ToArray();
var TargetSkeleton = new SkeletonAsset(
    "engine://skeletons/uefn-mannequin-c",
    "UEFN Mannequin C Skeleton",
    TargetBones);
var BoneMap = SourceSkeleton.Bones.ToDictionary(Bone => Bone.BoneId, Bone => Bone.BoneId,
    StringComparer.Ordinal);
var Retargeted = SkeletalAnimationRetargeter.Retarget(
    SourceAnimation,
    SourceSkeleton,
    TargetSkeleton,
    BoneMap,
    "engine://animations/uefn-mannequin-walk-c",
    "UEFN Mannequin Walk C");
var TargetMesh = new SkeletalMeshAsset(
    "engine://meshes/uefn-mannequin-c",
    "UEFN Mannequin C Skeletal Mesh",
    TargetSkeleton.AssetId,
    TargetSkeleton.Bones,
    SourceMesh.Positions,
    SourceMesh.Indices,
    SourceMesh.Influences,
    SourceMesh.DefaultMaterial);
SkeletalAnimationCompatibility.Validate(Retargeted, TargetMesh);
Reject(() => SkeletalAnimationCompatibility.Validate(SourceAnimation, TargetMesh),
    "source animation must not drive the target mesh before retargeting");

var SourceFrame = new SkeletonInstance(SourceMesh).Prepare(
    "source", "source-mesh", SourceAnimation.Sample(0), "SOURCE_READY");
var TargetFrame = new SkeletonInstance(TargetMesh).Prepare(
    "target", "target-mesh", Retargeted.Sample(0), "TARGET_READY");

Check(Retargeted.SkeletonId == TargetSkeleton.AssetId, "retarget output must reference target skeleton");
Check(Retargeted.Bones.Count == TargetSkeleton.Bones.Count, "retarget output must carry target hierarchy");
Check(Retargeted.Tracks.Count == SourceAnimation.Tracks.Count, "retarget output must preserve animated tracks");
Check(Retargeted.Tracks.All(Track => Track.Keyframes.Count == 5), "retarget output must preserve source key times");
Check(Retargeted.Sample(0).Bones.Count == TargetSkeleton.Bones.Count, "retarget output must sample a complete target pose");
Check(SkeletalAnimationAsset.Parse(Retargeted.Serialize(false)).Serialize(false) == Retargeted.Serialize(false),
    "retarget output serialization must be deterministic");
Check(Retargeted.Tracks.Any(Track => Track.BoneId == "pelvis" &&
    Track.Keyframes[0].Transform.Position.Y > SourceAnimation.Tracks.Single(Other => Other.BoneId == "pelvis")
        .Keyframes[0].Transform.Position.Y),
    "retarget output must apply target bind changes");
Check(TargetFrame.Positions.Count == SourceFrame.Positions.Count &&
    TargetFrame.Positions.Zip(SourceFrame.Positions, (Target, Source) => Vector3.Distance(Target, Source))
        .All(Distance => Distance <= 1e-4f),
    "retargeted animation must produce equivalent skinning on the target mesh");
Check(TargetFrame.Positions.All(VectorMath.IsFinite), "target skeletal mesh must receive finite retargeted positions");

var MissingBoneMap = new Dictionary<string, string>(BoneMap, StringComparer.Ordinal);
MissingBoneMap.Remove("pelvis");
Reject(() => SkeletalAnimationRetargeter.Retarget(SourceAnimation, SourceSkeleton, TargetSkeleton,
    MissingBoneMap, "engine://animations/missing", "Missing"),
    "retarget must reject an unmapped animated bone");

var DuplicateBoneMap = new Dictionary<string, string>(BoneMap, StringComparer.Ordinal)
{
    ["spine_01"] = "pelvis",
};
Reject(() => SkeletalAnimationRetargeter.Retarget(SourceAnimation, SourceSkeleton, TargetSkeleton,
    DuplicateBoneMap, "engine://animations/duplicate", "Duplicate"),
    "retarget must reject duplicate target bones");

Console.WriteLine($"[RetargetValidation] PASS: assertions=14 source={SourceAnimation.AssetId} target={Retargeted.SkeletonId} tracks={Retargeted.Tracks.Count}");

static void Check(bool Condition, string Message)
{
    if (!Condition)
    {
        throw new InvalidOperationException(Message);
    }
}

static void Reject(Action Action, string Message)
{
    try
    {
        Action();
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected rejection: {Message}");
}
