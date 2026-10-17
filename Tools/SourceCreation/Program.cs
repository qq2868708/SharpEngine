using System.Text.Json;
using SharpEngine.Editor;

try
{
    string Option(string name, string fallback = "")
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
    var manifest = Path.GetFullPath(Option("--project"));
    var command = new CSharpSourceCreation(manifest, Path.GetFullPath(Option("--engine-root")),
        Option("--configuration", "Debug"), "Win64");
    var module = Option("--module").Replace('\\', '/');
    var directory = Option("--directory");
    var suppliedNamespace = Option("--namespace");
    if (suppliedNamespace.Length != 0)
    {
        var descriptor = command.Modules().Single(value => value.ProjectPath == module);
        var expected = descriptor.RootNamespace + (directory.Length == 0 ? "" : "." + directory.Replace('\\', '.').Replace('/', '.'));
        if (suppliedNamespace != expected) throw new ArgumentException($"Namespace is derived from the Module and directory: {expected}");
    }
    var result = command.Create(new(module, directory, Option("--name"), Option("--template", "component")));
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return result.Success ? 0 : 1;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
