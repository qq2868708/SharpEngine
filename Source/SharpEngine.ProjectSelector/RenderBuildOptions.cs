using System.Text.Json;

namespace SharpEngine.ProjectSelector;

internal static class RenderBuildOptions
{
    internal static string Validate(string Value)
    {
        if (Value is not ("OpenGL" or "Both"))
        {
            throw new InvalidDataException("renderBackends must be OpenGL or Both");
        }
        return Value;
    }

    internal static string Read(JsonElement Root)
    {
        var HasValue = Root.TryGetProperty("renderBackends", out var Value);
        if (!HasValue)
        {
            return "Both";
        }
        if (Value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("renderBackends must be a string: OpenGL or Both");
        }
        return Validate(Value.GetString()!);
    }
}
