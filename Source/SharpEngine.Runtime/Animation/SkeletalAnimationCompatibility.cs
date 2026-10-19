namespace SharpEngine.Animation;

/// <summary>Validates that an animation can drive one skeletal mesh without implicit retargeting.</summary>
public static class SkeletalAnimationCompatibility
{
    /// <summary>
    /// Validates the explicit skeleton identity and embedded hierarchy/bind data.
    /// A mismatch is rejected; this method never retargets or mutates either asset.
    /// </summary>
    public static void Validate(SkeletalAnimationAsset Animation, SkeletalMeshAsset Mesh)
    {
        ArgumentNullException.ThrowIfNull(Animation);
        ArgumentNullException.ThrowIfNull(Mesh);
        if (Animation.SkeletonId is not null &&
            !string.Equals(Animation.SkeletonId, Mesh.SkeletonId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Animation '{Animation.AssetId}' references skeleton '{Animation.SkeletonId}', " +
                $"but mesh '{Mesh.AssetId}' references '{Mesh.SkeletonId}'");
        }
        SkeletonBinding.Validate(Mesh.Bones, Animation.Bones, "Animation and skeletal mesh");
    }
}
