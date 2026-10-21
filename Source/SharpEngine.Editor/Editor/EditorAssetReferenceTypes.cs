using SharpEngine.Assets;

namespace SharpEngine.Editor;

internal static class EditorAssetReferenceTypes
{
    public static bool Matches(string manifestType, Type referenceType)
    {
        var name = referenceType.Name;
        if (name.Equals(nameof(StaticMeshAsset), StringComparison.Ordinal))
            return manifestType.Equals("Mesh", StringComparison.OrdinalIgnoreCase) ||
                manifestType.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase);
        return manifestType.Equals(SharpEngine.Components.ComponentValue.AssetTypeName(referenceType), StringComparison.OrdinalIgnoreCase);
    }
}
