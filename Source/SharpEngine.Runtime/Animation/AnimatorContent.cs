using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SharpEngine.Assets;
using SharpEngine.Playables;

namespace SharpEngine.Animation;

// Reconstructable asset bindings shared by components and authoring preparation.
internal static class AnimatorContent
{
    private sealed class ContentCache
    {
        internal int Generation;
        internal readonly ConcurrentDictionary<(string Clip, string Mesh), SkeletalAnimationAsset> Clips = new();
        internal readonly ConcurrentDictionary<string, SkeletalMeshAsset> Meshes = new(StringComparer.OrdinalIgnoreCase);
    }
    private static readonly ConditionalWeakTable<AssetRegistry, ContentCache> Caches = new();

    internal static int Generation(AssetRegistry Assets) { return Caches.GetOrCreateValue(Assets).Generation; }
    internal static SkeletalMeshAsset Mesh(AssetRegistry Assets, string Id) { return Caches.GetOrCreateValue(Assets).Meshes.GetOrAdd(Id, Key => SkeletalMeshContent.Load(Assets, Key)); }

    internal static SkeletalAnimationAsset Resolve(AssetRegistry Assets, string Clip, string Mesh)
    {
        return Caches.GetOrCreateValue(Assets).Clips.GetOrAdd((Clip, Mesh), Key =>
        {
            var Animation = SkeletalAnimationContent.Load(Assets, Key.Clip);
            var Geometry = AnimatorContent.Mesh(Assets, Key.Mesh);
            if (Animation.SkeletonId is not null && !string.Equals(Animation.SkeletonId, Geometry.SkeletonId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Animator clip and mesh must reference the same skeleton.");
            }
            var Bones = Geometry.Bones.ToDictionary(Bone => Bone.BoneId, StringComparer.Ordinal);
            // Partial clips are supported; every animated bone must belong to the same hierarchy.
            foreach (var Bone in Animation.Bones)
            {
                var Found = Bones.TryGetValue(Bone.BoneId, out var Target);
                if (!Found || Target!.ParentBoneId != Bone.ParentBoneId)
                {
                    throw new InvalidDataException($"Animator bone '{Bone.BoneId}' is incompatible with the mesh hierarchy.");
                }
            }
            return Animation;
        });
    }

    internal static void Invalidate(AssetRegistry Assets)
    {
        var Cache = Caches.GetOrCreateValue(Assets);
        Cache.Clips.Clear();
        Cache.Meshes.Clear();
        Cache.Generation++;
    }

    internal static SkeletalAnimationPlayable CreatePlayable(AssetRegistry Assets, string ComponentId, string EntityId,
        string Clip, string Mesh, double Time, bool Playing, bool Loop)
    {
        var Playable = new SkeletalAnimationPlayable(ComponentId, Resolve(Assets, Clip, Mesh), EntityId,
            Loop ? PlayableLoopMode.Loop : PlayableLoopMode.Once);
        if (Playing)
        {
            Playable.Play();
        }
        // Restore after Play: seeking to the endpoint must retain Completed, rather than restart.
        Playable.Seek(Time);
        return Playable;
    }
}
