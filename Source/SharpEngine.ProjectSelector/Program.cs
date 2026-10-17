using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace SharpEngine.ProjectSelector;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var noUi = HasFlag(args, "--no-ui");
        try
        {
            if (args.Length == 0)
                throw new ArgumentException("A command or .sharpproject path is required");
            var command = args[0].EndsWith(".sharpproject",
                StringComparison.OrdinalIgnoreCase) ? "open-editor" :
                args[0].ToLowerInvariant();
            var implicitProject = command == "open-editor" &&
                                  args[0].EndsWith(".sharpproject",
                                      StringComparison.OrdinalIgnoreCase)
                ? args[0]
                : null;
            return command switch
            {
                "register" => Register(args, noUi),
                "unregister" => Unregister(args, noUi),
                "list" => List(args, noUi),
                "resolve" => Resolve(args, implicitProject, prompt: false),
                "generate" => Generate(args, implicitProject, noUi),
                "generateprojectfiles" or "generate-project-files" =>
                    GenerateProjectFiles(args, implicitProject, noUi),
                "open-solution" => OpenSolution(args, implicitProject, noUi),
                "open-editor" => OpenEditor(args, implicitProject, noUi),
                "run-game" => OpenEditor(args, implicitProject, noUi, "Game"),
                "build-engine" => BuildEngine(args),
                "generate-engine" => GenerateEngine(args),
                "migrate" => Migrate(args),
                "initialize-world" => InitializeWorld(args),
                _ => throw new ArgumentException($"Unknown Project Selector command '{command}'"),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteResult(args, new
            {
                success = false,
                error = exception.Message,
                exception = exception.GetType().Name,
            });
            if (!noUi)
                MessageBox.Show(exception.Message, "SharpEngine Project Selector",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Register(string[] args, bool noUi)
    {
        var engineRoot = RequiredOption(args, "--engine-root");
        var association = Option(args, "--association");
        var installed = HasFlag(args, "--installed");
        var store = new EngineAssociationStore();
        var registration = store.Register(engineRoot, association, installed);
        if (!HasFlag(args, "--no-file-association")) RegisterFileAssociation();
        WriteResult(args, new
        {
            success = true,
            registration.Association,
            registration.Root,
        });
        if (!noUi && Option(args, "--output") is null)
            MessageBox.Show($"Registered {registration.Association}\n{registration.Root}",
                "SharpEngine Engine Registration", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        return 0;
    }

    private static int Unregister(string[] args, bool noUi)
    {
        var association = RequiredOption(args, "--association");
        var removed = new EngineAssociationStore().Unregister(association);
        WriteResult(args, new { success = true, association, removed });
        if (!noUi && Option(args, "--output") is null)
            MessageBox.Show(removed ? $"Unregistered {association}" :
                    $"Association {association} was not registered",
                "SharpEngine Engine Registration", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        return 0;
    }

    private static int List(string[] args, bool noUi)
    {
        var registrations = new EngineAssociationStore().List();
        WriteResult(args, new { success = true, registrations });
        if (!noUi && Option(args, "--output") is null)
        {
            var message = registrations.Count == 0
                ? "No SharpEngine installations are registered."
                : string.Join(Environment.NewLine, registrations.Select(item =>
                    $"{item.Association} = {item.Root}"));
            MessageBox.Show(message, "Registered SharpEngine Builds",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        return 0;
    }

    private static int Resolve(string[] args, string? implicitProject, bool prompt)
    {
        var resolved = ResolveProject(args, implicitProject, prompt);
        WriteResult(args, new
        {
            success = true,
            project = resolved.Project.ManifestPath,
            projectId = resolved.Project.ProjectId,
            association = resolved.Project.EngineAssociation,
            engineRoot = resolved.Engine.Root,
            engineVersion = resolved.Engine.Version,
        });
        return 0;
    }

    private static int Generate(string[] args, string? implicitProject, bool noUi)
    {
        var resolved = ResolveProject(args, implicitProject, prompt: !noUi);
        var generated = SolutionGenerator.Generate(resolved);
        WriteResult(args, new
        {
            success = true,
            generated.SolutionPath,
            generated.WrapperProjectPath,
            generated.GeneratedPropsPath,
            generated.StampPath,
        });
        return 0;
    }

    private static int GenerateProjectFiles(string[] args, string? implicitProject, bool noUi)
    {
        if (implicitProject is not null || Option(args, "--project") is not null)
            return Generate(args, implicitProject, noUi);

        var engineRoot = Option(args, "--engine-root") ??
            Environment.GetEnvironmentVariable("SHARPENGINE_ROOT");
        if (string.IsNullOrWhiteSpace(engineRoot))
            throw new ArgumentException(
                "Engine project-file generation requires --engine-root when --project is omitted");
        engineRoot = Path.GetFullPath(engineRoot);
        var engine = ProjectContracts.ReadEngine(engineRoot);
        SolutionGenerator.GenerateEngine(engine.Root);
        WriteResult(args, new
        {
            success = true,
            engineRoot = engine.Root,
            solutionPath = Path.Combine(engine.Root, "SharpEngine.sln"),
        });
        return 0;
    }

    private static int OpenSolution(string[] args, string? implicitProject, bool noUi)
    {
        var resolved = ResolveProject(args, implicitProject, prompt: !noUi);
        var generated = SolutionGenerator.EnsureCurrent(resolved);
        _ = Process.Start(new ProcessStartInfo(generated.SolutionPath)
        {
            UseShellExecute = true,
        }) ?? throw new IOException("Could not open the generated Solution");
        WriteResult(args, new { success = true, generated.SolutionPath });
        return 0;
    }

    private static int OpenEditor(string[] args, string? implicitProject, bool noUi, string target = "Editor")
    {
        var resolved = ResolveProject(args, implicitProject, prompt: !noUi);
        var requestedConfiguration = Option(args, "--configuration");
        var configurations = requestedConfiguration is null
            ? new[] { "Development", "Debug", "Shipping" }
            : new[] { ValidateConfiguration(requestedConfiguration) };
        var executable = configurations.Select(configuration => Path.Combine(
                ProjectContracts.ResolveBinaries(resolved.Engine, "Win64", configuration,
                    target), "Engine.exe"))
            .FirstOrDefault(File.Exists) ??
            throw new FileNotFoundException(
                "No built Editor was found for the associated Engine. Build the Engine first.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = resolved.Project.Root,
        };
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(resolved.Project.ManifestPath);
        if (HasFlag(args, "--smoke-test")) start.ArgumentList.Add("--smoke-test");
        if (Option(args, "--frames") is { } frames)
        {
            start.ArgumentList.Add("--frames");
            start.ArgumentList.Add(frames);
        }
        if (Option(args, "--screenshot") is { } screenshot)
        {
            start.ArgumentList.Add("--screenshot");
            start.ArgumentList.Add(Path.GetFullPath(screenshot));
        }
        start.Environment["SHARPENGINE_ROOT"] = resolved.Engine.Root;
        using var process = Process.Start(start) ??
            throw new IOException("Could not start the SharpEngine Editor");
        if (HasFlag(args, "--wait"))
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"SharpEngine Editor exited with code {process.ExitCode}");
        }
        WriteResult(args, new { success = true, executable, project = resolved.Project.ManifestPath });
        return 0;
    }

    private static int BuildEngine(string[] args)
    {
        EngineBuildGraph.Build(RequiredOption(args, "--engine-root"), Option(args, "--target") ?? "Editor",
            Option(args, "--configuration") ?? "Debug", Option(args, "--platform") ?? "Win64");
        return 0;
    }

    private static int Migrate(string[] args)
    {
        var resolved = ResolveProject(args, null, prompt: false);
        ProjectMigration.Migrate(resolved);
        SolutionGenerator.Generate(resolved);
        return 0;
    }

    private static int InitializeWorld(string[] args)
    {
        ProjectMigration.CreateStartupWorld(ResolveProject(args, null, prompt: false));
        return 0;
    }

    private static int GenerateEngine(string[] args)
    {
        SolutionGenerator.GenerateEngine(RequiredOption(args, "--engine-root"));
        return 0;
    }

    private static ResolvedSharpProject ResolveProject(string[] args,
        string? implicitProject, bool prompt)
    {
        var projectPath = implicitProject ?? RequiredOption(args, "--project");
        var project = ProjectContracts.ReadProject(projectPath);
        var engineRoot = Option(args, "--engine-root");
        if (string.IsNullOrWhiteSpace(engineRoot))
            engineRoot = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT");
        var store = new EngineAssociationStore();
        if (string.IsNullOrWhiteSpace(engineRoot))
            engineRoot = store.Resolve(project.EngineAssociation);
        if (string.IsNullOrWhiteSpace(engineRoot) && prompt)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = $"Select the SharpEngine root for association {project.EngineAssociation}",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            if (dialog.ShowDialog() != DialogResult.OK)
                throw new InvalidOperationException(
                    $"Engine association '{project.EngineAssociation}' is not registered");
            engineRoot = dialog.SelectedPath;
            var selectedEngine = ProjectContracts.ReadEngine(engineRoot,
                project.EngineManifest);
            ProjectContracts.ValidateBinding(project, selectedEngine);
            store.Bind(project.EngineAssociation, selectedEngine.Root);
        }
        if (string.IsNullOrWhiteSpace(engineRoot))
            throw new InvalidOperationException(
                $"Engine association '{project.EngineAssociation}' is not registered");
        var engine = ProjectContracts.ReadEngine(engineRoot, project.EngineManifest);
        ProjectContracts.ValidateBinding(project, engine);
        return new ResolvedSharpProject(project, engine);
    }

    private static void RegisterFileAssociation()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !string.Equals(Path.GetExtension(executable), ".exe",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(executable), "dotnet.exe",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "File association registration requires the built Project Selector executable");
        using var extension = Registry.CurrentUser.CreateSubKey(
            @"Software\Classes\.sharpproject", writable: true)!;
        extension.SetValue(string.Empty, "SharpEngine.Project", RegistryValueKind.String);
        using var projectClass = Registry.CurrentUser.CreateSubKey(
            @"Software\Classes\SharpEngine.Project", writable: true)!;
        projectClass.SetValue(string.Empty, "SharpEngine Project", RegistryValueKind.String);
        SetShellCommand("open", "Open in SharpEngine", executable, "open-editor");
        DeleteShellCommand("generate");
        SetShellCommand("GenerateProjectFiles", "Generate Project Files", executable,
            "GenerateProjectFiles");
        SetShellCommand("solution", "Open Visual Studio Solution", executable,
            "open-solution");
    }

    private static void DeleteShellCommand(string verb)
    {
        using var shell = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\SharpEngine.Project\shell", writable: true);
        shell?.DeleteSubKeyTree(verb, throwOnMissingSubKey: false);
    }

    private static void SetShellCommand(string verb, string label, string executable,
        string command)
    {
        using var shell = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\SharpEngine.Project\shell\{verb}", writable: true)!;
        shell.SetValue(string.Empty, label, RegistryValueKind.String);
        using var commandKey = shell.CreateSubKey("command", writable: true)!;
        commandKey.SetValue(string.Empty,
            $"\"{executable}\" {command} --project \"%1\"", RegistryValueKind.String);
    }

    private static void WriteResult(string[] args, object value)
    {
        var output = Option(args, "--output");
        if (string.IsNullOrWhiteSpace(output)) return;
        var fullPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        File.Move(temporary, fullPath, true);
    }

    private static string ValidateConfiguration(string value) => value switch
    {
        "Debug" or "Development" or "Shipping" => value,
        _ => throw new ArgumentException($"Unsupported configuration '{value}'"),
    };

    private static bool HasFlag(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name,
            StringComparison.OrdinalIgnoreCase));

    private static string RequiredOption(string[] args, string name) =>
        Option(args, name) ?? throw new ArgumentException($"Required option is missing: {name}");

    private static string? Option(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }
}
