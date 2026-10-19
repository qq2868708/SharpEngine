using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace SharpEngine.ProjectSelector;

internal static class ProjectTargets
{
    internal static readonly string[] SupportedTargets = ["Editor", "Game", "Server"];
    internal static readonly string[] DefaultTargets = ["Editor", "Game"];

    internal static IReadOnlyList<string> Read(JsonElement Root)
    {
        if (!Root.TryGetProperty("targets", out var Element))
        {
            return DefaultTargets;
        }
        if (Element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Project property 'targets' must be an array");
        }
        var Targets = Element.EnumerateArray().Select(Value => Value.ValueKind == JsonValueKind.String
            ? Value.GetString() ?? string.Empty
            : string.Empty).ToArray();
        Validate(Targets);
        return Targets;
    }

    internal static void Validate(IReadOnlyList<string> Targets)
    {
        if (Targets.Count == 0)
        {
            throw new InvalidDataException("Project property 'targets' must contain at least Editor");
        }
        if (Targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Targets.Count)
        {
            throw new InvalidDataException("Project property 'targets' must not contain duplicates");
        }
        foreach (var Target in Targets)
        {
            if (!SupportedTargets.Contains(Target, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Project target '{Target}' is unsupported; supported targets are Editor, Game, Server");
            }
        }
        if (!Targets.Contains("Editor", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Project property 'targets' must contain Editor");
        }
    }

    internal static IReadOnlyList<string> UpdateManifest(string ManifestPath, string Target, bool Add)
    {
        if (!SupportedTargets.Contains(Target, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Project target '{Target}' is unsupported; supported targets are Editor, Game, Server");
        }
        if (!Add && Target.Equals("Editor", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Editor target is required and cannot be removed");
        }

        var Root = JsonNode.Parse(File.ReadAllText(ManifestPath))?.AsObject() ??
            throw new InvalidDataException("Project manifest must be a JSON object");
        var Current = Root["targets"] is JsonArray Existing
            ? Existing.Select(Value => Value?.GetValue<string>() ?? string.Empty).ToList()
            : DefaultTargets.ToList();
        Validate(Current);
        var ExistingTarget = Current.Any(Value => Value.Equals(Target,
            StringComparison.OrdinalIgnoreCase));
        if (Add && !ExistingTarget)
        {
            Current.Add(Target);
        }
        else if (!Add && ExistingTarget)
        {
            Current.RemoveAll(Value => Value.Equals(Target, StringComparison.OrdinalIgnoreCase));
        }
        Current = SupportedTargets.Where(Supported => Current.Any(Value =>
                Value.Equals(Supported, StringComparison.OrdinalIgnoreCase))).ToList();
        Validate(Current);

        var Targets = new JsonArray();
        foreach (var Value in Current)
        {
            Targets.Add(Value);
        }
        Root["targets"] = Targets;
        var TemporaryPath = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(TemporaryPath, Root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            }) + Environment.NewLine);
            File.Move(TemporaryPath, ManifestPath, true);
        }
        finally
        {
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }
        return Current;
    }
}
