using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SharpEngine.Editor;

public sealed record SourceModule(string Id, string Type, string ProjectPath, string RootNamespace);
public sealed record CreateSourceRequest(string ModuleProject, string Directory, string Name, string Template);
public sealed record CreateSourceResult(int SchemaVersion, bool Success, string Code, string Message,
    string? Path = null, string? Namespace = null, string? ComponentTypeId = null);

/// <summary>Shared file-creation command; source files are external authoring inputs.</summary>
internal sealed class CSharpSourceCreation(string manifest, string engineRoot, string configuration, string platform)
{
    private readonly string _root = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
    private static readonly HashSet<string> Keywords = new(("abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while").Split(' '), StringComparer.Ordinal);

    internal IReadOnlyList<SourceModule> Modules()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        return json.RootElement.GetProperty("modules").EnumerateArray().Select(item =>
        {
            var relative = item.GetString()!;
            var project = Contained(Path.Combine(_root, "Source"), Path.GetFullPath(Path.Combine(_root, relative)));
            CheckLinks(project);
            var properties = Evaluate(project, false).GetProperty("Properties");
            return new SourceModule(properties.GetProperty("SharpEngineModuleId").GetString()!,
                properties.GetProperty("SharpEngineModuleType").GetString()!,
                relative.Replace('\\', '/'), properties.GetProperty("RootNamespace").GetString()!);
        }).ToArray();
    }

    internal CreateSourceResult Create(CreateSourceRequest request)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.ModuleProject);
            ArgumentNullException.ThrowIfNull(request.Directory);
            ArgumentNullException.ThrowIfNull(request.Name);
            if (request.Template is not ("component" or "class")) throw new ArgumentException("Unknown C# template");
            var modules = Modules();
            var module = modules.SingleOrDefault(value => value.ProjectPath.Equals(request.ModuleProject.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Choose a Module declared in this project");
            if (module.Type is not ("Runtime" or "Editor")) throw new ArgumentException("Module type must be Runtime or Editor");
            if (request.Template == "component" && module.Type != "Runtime") throw new ArgumentException("Components require a Runtime Module");
            if (!Identifier(request.Name)) throw new ArgumentException("Type name must be a C# identifier, without an extension");
            if (request.Name is "." or ".." || request.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(request.Name, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new ArgumentException("Type name is reserved by the filesystem");
            var segments = request.Directory.Length == 0 ? [] : request.Directory.Replace('\\', '/').Split('/');
            if (segments.Any(segment => !Identifier(segment))) throw new ArgumentException("Every directory segment must be a C# identifier");
            var ns = string.Join('.', new[] { module.RootNamespace }.Concat(segments));
            if (ns.Split('.').Any(segment => !Identifier(segment))) throw new ArgumentException("Module RootNamespace is not a valid C# namespace");
            var project = Path.GetFullPath(Path.Combine(_root, module.ProjectPath));
            var moduleRoot = Path.GetDirectoryName(project)!;
            var directory = segments.Aggregate(moduleRoot, Path.Combine);
            var target = Contained(moduleRoot, Path.Combine(directory, request.Name + ".cs"));
            CheckLinks(target);
            // A parent SDK project can also include nested Modules. Do not create ambiguous source ownership.
            foreach (var other in modules.Where(value => value != module))
            {
                var otherRoot = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(_root, other.ProjectPath)))!;
                if (IsWithin(otherRoot, target)) throw new ArgumentException("Target lies inside another Module; choose an unambiguous Module directory");
            }
            if (File.Exists(target) || System.IO.Directory.Exists(target)) throw new IOException("A file or directory already exists at the destination");
            var typeId = request.Template == "component" ? Guid.NewGuid().ToString() : null;
            var source = request.Template == "component"
                ? $"using SharpEngine.Components;\n\nnamespace {ns};\n\n[ComponentTypeId(\"{typeId}\")]\n[ComponentSchemaVersion(1)]\npublic sealed partial class {request.Name} : SharpComponent\n{{\n}}\n"
                : $"namespace {ns};\n\npublic sealed class {request.Name}\n{{\n}}\n";
            var createdDirectories = new List<string>();
            var missing = new Stack<string>();
            for (var path = directory; !System.IO.Directory.Exists(path); path = Path.GetDirectoryName(path)!) missing.Push(path);
            string? staged = null;
            var published = false;
            try
            {
                while (missing.TryPop(out var path)) { System.IO.Directory.CreateDirectory(path); createdDirectories.Add(path); }
                staged = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(staged, source, new UTF8Encoding(false));
                File.Move(staged, target, overwrite: false);
                published = true;
                var compile = Evaluate(project, true).GetProperty("Items").GetProperty("Compile");
                if (!compile.EnumerateArray().Any(item => string.Equals(item.GetProperty("FullPath").GetString(), target, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The Module excludes this file from Compile items; adjust its source inclusion rules first");
                return new(1, true, "SOURCE_CREATED", "C# source created", target, ns, typeId);
            }
            catch
            {
                // Never remove a concurrently edited file while rolling back a failed inclusion check.
                if (published && File.Exists(target) && File.ReadAllText(target) == source) File.Delete(target);
                foreach (var path in createdDirectories.AsEnumerable().Reverse())
                    if (!System.IO.Directory.EnumerateFileSystemEntries(path).Any()) System.IO.Directory.Delete(path);
                throw;
            }
            finally { if (staged is not null && File.Exists(staged)) File.Delete(staged); }
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or System.Xml.XmlException or JsonException or UnauthorizedAccessException)
        { return new(1, false, "SOURCE_CREATE_REJECTED", error.Message); }
    }

    private JsonElement Evaluate(string project, bool compile)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root };
        foreach (var argument in new[] { "msbuild", project, "-nologo",
            "-getProperty:SharpEngineModuleId,SharpEngineModuleType,RootNamespace", $"-p:Configuration={configuration}",
            $"-p:SharpEnginePlatform={platform}", "-p:SharpEngineTarget=Editor", $"-p:SharpEngineRoot={Path.TrimEndingDirectorySeparator(engineRoot) + Path.DirectorySeparatorChar}",
            $"-p:SharpEngineRuntimeApi={Path.Combine(engineRoot, "Binaries", platform, configuration, "Editor", "SharpEngine.Runtime.dll")}",
            $"-p:SharpEngineEditorApi={Path.Combine(engineRoot, "Binaries", platform, configuration, "Editor", "SharpEngine.Editor.dll")}" }) start.ArgumentList.Add(argument);
        if (compile) start.ArgumentList.Add("-getItem:Compile");
        using var process = Process.Start(start) ?? throw new IOException("Cannot evaluate Module project");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000)) { process.Kill(true); throw new IOException("Module evaluation timed out"); }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException("Module evaluation failed: " + stderr.Result);
        using var json = JsonDocument.Parse(stdout.Result);
        return json.RootElement.Clone();
    }

    internal static bool Identifier(string value) => value.Length > 0 && !Keywords.Contains(value) &&
        (value[0] == '_' || char.IsLetter(value[0])) && value.Skip(1).All(c => c == '_' || char.IsLetterOrDigit(c) ||
            char.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format);
    private static bool IsWithin(string root, string path) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Contained(string root, string path) => IsWithin(root, path) ? Path.GetFullPath(path) : throw new ArgumentException("Source path escapes its Module");
    private static void CheckLinks(string path)
    {
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
            if (System.IO.Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Source creation through directory links is unsupported");
    }
}
