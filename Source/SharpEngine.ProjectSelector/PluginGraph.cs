using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Plugins;

namespace SharpEngine.ProjectSelector;

internal sealed record PluginChange(string Id, string Uri, bool Enabled, bool ClearsExplicitDisable);

internal sealed record PluginChangePlan(int SchemaVersion, string PlanId, string PluginId,
    bool Enable, IReadOnlyList<PluginChange> Changes, IReadOnlyList<string> Blockers);

/// <summary>Evaluated module references own dependencies; Plugin manifests only group modules.</summary>
internal sealed class PluginGraph
{
    private readonly ResolvedSharpProject Project;
    private readonly PluginConfiguration Configuration;
    private readonly Dictionary<string, string> Uris = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> Dependencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> RequiredBy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, IReadOnlyList<string>>> VariantReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string[] Owners, string Diagnostic)> Cycles = [];
    private readonly List<string> Fingerprints = [];
    private readonly Dictionary<string, string> InputHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly byte[] ProjectBytes;

    private PluginGraph(ResolvedSharpProject Project)
    {
        this.Project = Project;
        ProjectBytes = File.ReadAllBytes(Project.Project.ManifestPath);
        Configuration = PluginConfiguration.Read(Project.Engine.ManifestPath, Project.Project.ManifestPath);
        Capture(Project.Project.ManifestPath);
        Capture(Project.Engine.ManifestPath);
    }

    internal IReadOnlyList<string> PluginIds
    {
        get
        {
            return Uris.Keys.OrderBy(Id => Id, StringComparer.Ordinal).ToArray();
        }
    }

    private void Capture(string PathValue)
    {
        var Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PathValue)));
        InputHashes[PathValue] = Hash;
    }

    internal bool IsEnabled(string Id) { return Configuration.Effective.Contains(Uris[Id], StringComparer.OrdinalIgnoreCase); }

    internal static PluginGraph Load(ResolvedSharpProject Project,
        Func<string, IReadOnlyList<string>>? EvaluateReferences = null)
    {
        var Graph = new PluginGraph(Project);
        var Owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (Root, Scheme) in new[]
                 {
                     (Project.Engine.Root, "engine"), (Project.Project.Root, "project"),
                 })
        {
            var PluginsRoot = Path.Combine(Root, "Plugins");
            if (!Directory.Exists(PluginsRoot))
            {
                continue;
            }
            foreach (var Manifest in Directory.EnumerateFiles(PluginsRoot, "*.sharpplugin", SearchOption.AllDirectories)
                         .OrderBy(PathValue => PathValue, StringComparer.Ordinal))
            {
                var Descriptor = PluginContracts.Read(Manifest);
                var Uri = Scheme + "://" + Path.GetRelativePath(Root, Manifest).Replace('\\', '/');
                if (!Graph.Uris.TryAdd(Descriptor.Id, Uri))
                {
                    throw new InvalidDataException($"SEPLUGIN003: Duplicate Plugin id '{Descriptor.Id}'");
                }
                Graph.Dependencies.Add(Descriptor.Id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                Graph.Capture(Manifest);
                foreach (var Relative in Descriptor.Modules)
                {
                    var Module = ProjectContracts.ResolvePluginModule(Descriptor, Relative);
                    if (!File.Exists(Module) || !Owners.TryAdd(Module, Descriptor.Id))
                    {
                        throw new InvalidDataException($"SEPLUGIN026: Missing or duplicate Plugin Module: {Module}");
                    }
                }
            }
        }
        foreach (var Uri in Graph.Configuration.Effective.Concat(Graph.Configuration.ProjectDisabled))
        {
            if (!Graph.Uris.Values.Contains(Uri, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"SEPLUGIN011: Configured Plugin was not found: {Uri}");
            }
        }
        var Roots = Project.Project.Modules.Select(Relative =>
                (Path: Path.GetFullPath(Path.Combine(Project.Project.Root, Relative)), Label: "Project Module"))
            .Concat(EngineBuildGraph.Discover(Project.Engine.Root)
                .Where(Module => Module.Role is "Runtime" or "Editor")
                .Select(Module => (Path: Module.Path, Label: "Core Module"))).ToArray();
        var ReferenceCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> ReadReferences(string Module)
        {
            var Cached = ReferenceCache.TryGetValue(Module, out var References);
            if (Cached)
            {
                return References!;
            }
            Graph.Capture(Module);
            References = EvaluateReferences is null ? Graph.Evaluate(Module) : EvaluateReferences(Module);
            if (EvaluateReferences is not null)
            {
                Graph.VariantReferences[Module] = new Dictionary<string, IReadOnlyList<string>>
                {
                    ["all"] = References,
                };
            }
            ReferenceCache.Add(Module, References);
            Graph.Fingerprints.Add(Module + "\n" + string.Join('\n', References.OrderBy(Value => Value, StringComparer.Ordinal)));
            return References;
        }
        foreach (var Module in Owners.Keys.Concat(Roots.Select(Root => Root.Path))
                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(PathValue => PathValue, StringComparer.Ordinal))
        {
            _ = ReadReferences(Module);
            foreach (var Variant in Graph.VariantReferences[Module].Keys)
            {
                var References = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var Visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Follow(string Current)
                {
                    if (!Visited.Add(Current))
                    {
                        return;
                    }
                    _ = ReadReferences(Current);
                    foreach (var Reference in Graph.VariantReferences[Current][Variant])
                    {
                        var PathValue = Path.GetFullPath(Reference);
                        References.Add(PathValue);
                        // Stop at another Plugin: its own dependencies retain the chain.
                        if (!Owners.ContainsKey(PathValue))
                        {
                            Follow(PathValue);
                        }
                    }
                }
                Follow(Module);
                var HasOwner = Owners.TryGetValue(Module, out var Owner);
                foreach (var Reference in References)
                {
                    var HasDependency = Owners.TryGetValue(Path.GetFullPath(Reference), out var Dependency);
                    if (!HasDependency || Owner == Dependency)
                    {
                        continue;
                    }
                    if (HasOwner)
                    {
                        Graph.Dependencies[Owner!].Add(Dependency!);
                    }
                    else
                    {
                        var Label = Roots.First(Root => Root.Path.Equals(Module, StringComparison.OrdinalIgnoreCase)).Label;
                        var HasRequired = Graph.RequiredBy.TryGetValue(Dependency!, out var Required);
                        if (!HasRequired)
                        {
                            Required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            Graph.RequiredBy.Add(Dependency!, Required);
                        }
                        Required!.Add($"{Label}: {Module} -> {Dependency} ({Variant})");
                    }
                }
            }
        }
        var Variants = Graph.VariantReferences.Values.SelectMany(Value => Value.Keys).Distinct().ToArray();
        foreach (var Variant in Variants)
        {
            var Complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var Stack = new List<string>();
            void CheckCycle(string Module)
            {
                var Position = Stack.FindIndex(Value => Value.Equals(Module, StringComparison.OrdinalIgnoreCase));
                if (Position >= 0)
                {
                    var Cycle = Stack.Skip(Position).Append(Module).ToArray();
                    var CycleOwners = Cycle.Where(Owners.ContainsKey).Select(PathValue => Owners[PathValue]).Distinct().ToArray();
                    Graph.Cycles.Add((CycleOwners,
                        $"SEPLUGIN029: Module dependency cycle ({Variant}): " + string.Join(" -> ", Cycle)));
                    return;
                }
                if (!Complete.Add(Module))
                {
                    return;
                }
                Stack.Add(Module);
                var HasReferences = Graph.VariantReferences.TryGetValue(Module, out var References);
                if (HasReferences)
                {
                    foreach (var Reference in References![Variant])
                    {
                        CheckCycle(Path.GetFullPath(Reference));
                    }
                }
                Stack.RemoveAt(Stack.Count - 1);
            }
            foreach (var Module in Graph.VariantReferences.Keys)
            {
                CheckCycle(Module);
            }
        }
        return Graph;
    }

    private IReadOnlyList<string> Evaluate(string Module)
    {
        var References = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        VariantReferences[Module] = new Dictionary<string, IReadOnlyList<string>>();
        // Selection is project-wide. Inspect every supported target/configuration so
        // a Debug-only or Server-only dependency cannot escape the impact preview.
        foreach (var Target in Project.Project.Targets)
        {
            foreach (var ConfigurationName in EngineBuildGraph.Configurations)
            {
                var Start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(Module)!,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var Argument in new[]
                         {
                             "msbuild", Module, "-nologo", "-getItem:ProjectReference",
                             "-getProperty:MSBuildAllProjects", "-p:DesignTimeBuild=true",
                             "-p:SharpEngineUnifiedBuild=true",
                             $"-p:Configuration={ConfigurationName}", $"-p:SharpEngineTarget={Target}",
                             "-p:SharpEnginePlatform=Win64",
                         })
                {
                    Start.ArgumentList.Add(Argument);
                }
                using var Process = System.Diagnostics.Process.Start(Start) ??
                    throw new IOException("Could not evaluate Module dependencies");
                var Output = Process.StandardOutput.ReadToEndAsync();
                var Errors = Process.StandardError.ReadToEndAsync();
                if (!Process.WaitForExit(30000))
                {
                    Process.Kill(true);
                    throw new IOException($"SEPLUGIN027: Module evaluation timed out: {Module}");
                }
                Task.WaitAll(Output, Errors);
                if (Process.ExitCode != 0)
                {
                    throw new InvalidDataException($"SEPLUGIN027: Module evaluation failed: {Module}\n{Errors.Result}{Output.Result}");
                }
                using var Document = JsonDocument.Parse(Output.Result);
                var Items = Document.RootElement.GetProperty("Items").GetProperty("ProjectReference");
                var Paths = Items.EnumerateArray().Select(Item =>
                    Path.GetFullPath(Item.GetProperty("FullPath").GetString()!)).ToArray();
                VariantReferences[Module][Target + "/" + ConfigurationName] = Paths;
                foreach (var PathValue in Paths)
                {
                    References.Add(PathValue);
                }
                var Imports = Document.RootElement.GetProperty("Properties").GetProperty("MSBuildAllProjects")
                    .GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var Import in Imports.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (File.Exists(Import))
                    {
                        Capture(Import);
                    }
                }
                Fingerprints.Add(Target + "/" + ConfigurationName + "\n" +
                    string.Join('\n', Paths.OrderBy(PathValue => PathValue, StringComparer.Ordinal)));
            }
        }
        return References.ToArray();
    }

    internal PluginChangePlan Plan(string PluginId, bool Enable)
    {
        if (!Uris.ContainsKey(PluginId))
        {
            throw new InvalidDataException($"SEPLUGIN028: Unknown Plugin id: {PluginId}");
        }
        var Affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var Chains = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var Blockers = new List<string>();
        void Visit(string Id, string[] Chain)
        {
            if (!Affected.Add(Id))
            {
                return;
            }
            Chains[Id] = Chain.Append(Id).ToArray();
            var Next = Enable ? Dependencies[Id]
                : Dependencies.Where(Pair => Pair.Value.Contains(Id) && IsEnabled(Pair.Key))
                    .Select(Pair => Pair.Key);
            foreach (var Dependency in Next.OrderBy(Value => Value, StringComparer.Ordinal))
            {
                Visit(Dependency, Chains[Id]);
            }
        }
        Visit(PluginId, []);
        if (Enable)
        {
            Blockers.AddRange(Cycles.Where(Cycle => Cycle.Owners.All(Id => IsEnabled(Id) || Affected.Contains(Id)))
                .Select(Cycle => Cycle.Diagnostic));
        }
        if (!Enable)
        {
            foreach (var Id in Affected)
            {
                var Exists = RequiredBy.TryGetValue(Id, out var Required);
                if (Exists)
                {
                    Blockers.AddRange(Required!.Select(Value => "SEPLUGIN030: Cannot disable dependency chain: " + Value +
                        "; " + string.Join(" -> ", Chains[Id].Reverse())));
                }
            }
        }
        var Changes = Affected.OrderBy(Id => Id, StringComparer.Ordinal)
            .Where(Id => IsEnabled(Id) != Enable || (!Enable && Id == PluginId) ||
                (Enable && Configuration.ProjectDisabled.Contains(Uris[Id], StringComparer.OrdinalIgnoreCase)))
            .Select(Id => new PluginChange(Id, Uris[Id], Enable,
                Enable && Configuration.ProjectDisabled.Contains(Uris[Id], StringComparer.OrdinalIgnoreCase))).ToArray();
        var PlanId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', Fingerprints.Concat(InputHashes.Select(Pair => Pair.Key + "\n" + Pair.Value))
                .OrderBy(Value => Value, StringComparer.Ordinal)) +
            JsonSerializer.Serialize(new
            {
                PluginId,
                Enable,
                Changes,
                Blockers,
            }))));
        return new PluginChangePlan(1, PlanId, PluginId, Enable, Changes, Blockers);
    }

    internal void Apply(PluginChangePlan Plan, string ExpectedPlanId)
    {
        var Current = this.Plan(Plan.PluginId, Plan.Enable);
        if (Current.PlanId != ExpectedPlanId || Plan.PlanId != ExpectedPlanId ||
            InputsChanged())
        {
            throw new InvalidDataException("SEPLUGIN031: Plugin plan is stale. Preview the complete changes again.");
        }
        if (Current.Blockers.Count > 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, Current.Blockers));
        }
        if (Current.Changes.Count == 0)
        {
            return;
        }
        var Enabled = Configuration.ProjectEnabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var Disabled = Configuration.ProjectDisabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var Change in Current.Changes)
        {
            if (Change.Enabled)
            {
                Disabled.Remove(Change.Uri);
                Enabled.Add(Change.Uri);
            }
            else
            {
                Enabled.Remove(Change.Uri);
                Disabled.Add(Change.Uri);
            }
        }
        var Root = JsonNode.Parse(ProjectBytes)!.AsObject();
        Root["plugins"] = JsonSerializer.SerializeToNode(Enabled.OrderBy(Uri => Uri, StringComparer.Ordinal));
        Root["disabledPlugins"] = JsonSerializer.SerializeToNode(Disabled.OrderBy(Uri => Uri, StringComparer.Ordinal));
        var PathValue = Project.Project.ManifestPath;
        var LockDirectory = Path.Combine(Project.Project.Root, "Saved", "PluginConfiguration");
        Directory.CreateDirectory(LockDirectory);
        using var Lease = new FileStream(Path.Combine(LockDirectory, "plugins.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var Temporary = PathValue + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(Temporary, Root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
            }) + Environment.NewLine, new UTF8Encoding(false));
            _ = PluginConfiguration.Read(Project.Engine.ManifestPath, Temporary);
            if (InputsChanged())
            {
                throw new InvalidDataException("SEPLUGIN031: Project changed while applying Plugin plan");
            }
            File.Move(Temporary, PathValue, true);
        }
        finally
        {
            File.Delete(Temporary);
        }
    }

    private bool InputsChanged()
    {
        return !File.Exists(Project.Project.ManifestPath) ||
            !File.ReadAllBytes(Project.Project.ManifestPath).SequenceEqual(ProjectBytes) ||
            InputHashes.Any(Pair => !File.Exists(Pair.Key) ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Pair.Key))) != Pair.Value);
    }
}
