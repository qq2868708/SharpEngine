using System.Diagnostics;
using System.Text.Json;
using SharpEngine.Logging;
using SharpEngine.Interop;

namespace SharpEngine.ProjectSelector;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--mcp-stdio")
        {
            return UiMcpSessionHost.RunBridge("project-browser");
        }
        ApplicationConfiguration.Initialize();
        var noUi = HasFlag(args, "--no-ui");
        SharpLog.Initialize("ProjectSelector", engineRoot: FindEngineRoot());
        try
        {
            if (args.Length == 0 || args[0].Equals("browse", StringComparison.OrdinalIgnoreCase))
            {
                if (noUi)
                {
                    throw new ArgumentException("SEPROJECT002: Project browser requires a graphical session");
                }
                return LaunchBrowser(args);
            }
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
                "ensure-registered" => EnsureRegistered(args),
                "unregister" => Unregister(args, noUi),
                "list" => List(args, noUi),
                "resolve" => Resolve(args, implicitProject, prompt: false),
                "generate" => Generate(args, implicitProject, noUi),
                "generateprojectfiles" or "generate-project-files" =>
                    GenerateProjectFiles(args, implicitProject, noUi),
                "add-target" => UpdateProjectTarget(args, implicitProject, noUi, Add: true),
                "remove-target" => UpdateProjectTarget(args, implicitProject, noUi, Add: false),
                "open-solution" => OpenSolution(args, implicitProject, noUi),
                "open-editor" => OpenEditor(args, implicitProject, noUi),
                "run-game" => OpenEditor(args, implicitProject, noUi, "Game"),
                "build-engine" => BuildEngine(args),
                "generate-engine" => GenerateEngine(args),
                "migrate" => Migrate(args),
                "initialize-world" => InitializeWorld(args),
                "preview-project" => PreviewProject(args),
                "plugins" or "plan-plugin" or "apply-plugin" => PluginCommand(args),
                _ => throw new ArgumentException($"Unknown Project Selector command '{command}'"),
            };
        }
        catch (Exception exception)
        {
            SharpLog.Error("ProjectSelector", exception.Message, exception);
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
        finally { SharpLog.Shutdown(); }
    }

    private static string FindEngineRoot()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPENGINE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "engine.json"))) return current.FullName;
        return AppContext.BaseDirectory;
    }

    private static int Register(string[] args, bool noUi)
    {
        var engineRoot = RequiredOption(args, "--engine-root");
        var association = Option(args, "--association");
        var installed = HasFlag(args, "--installed");
        var store = new EngineAssociationStore();
        var registration = store.Register(engineRoot, association, installed);
        if (!HasFlag(args, "--no-file-association")) RegisterFileAssociation(engineRoot);
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

    private static int EnsureRegistered(string[] Args)
    {
        var Registration = new EngineFirstUse().EnsureRegistered(
            Option(Args, "--engine-root") ?? FindEngineRoot(), EngineFirstUse.ExecutablePath);
        WriteResult(Args, new
        {
            success = true,
            Registration.Association,
            Registration.Root,
        });
        return 0;
    }

    private static void EnsureFirstUse(string EngineRoot)
    {
        if (Environment.GetEnvironmentVariable("SHARPENGINE_REGISTER_ON_FIRST_USE") == "0")
        {
            return;
        }
        try
        {
            _ = new EngineFirstUse().EnsureRegistered(EngineRoot, EngineFirstUse.ExecutablePath);
        }
        catch (Exception Error)
        {
            // Desktop integration must not prevent editing or project-file generation.
            // Retrying on the next use also repairs partial/missing registry entries.
            SharpLog.Warning("ProjectSelector", "Automatic desktop registration failed: " + Error.Message);
        }
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
        EnsureFirstUse(resolved.Engine.Root);
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
        EnsureFirstUse(engine.Root);
        WriteResult(args, new
        {
            success = true,
            engineRoot = engine.Root,
            solutionPath = Path.Combine(engine.Root, "SharpEngine.sln"),
        });
        return 0;
    }

    private static int UpdateProjectTarget(string[] args, string? implicitProject,
        bool noUi, bool Add)
    {
        var Target = RequiredOption(args, "--target");
        var Resolved = ResolveProject(args, implicitProject, prompt: !noUi);
        var Targets = ProjectTargets.UpdateManifest(Resolved.Project.ManifestPath, Target, Add);
        var UpdatedProject = ProjectContracts.ReadProject(Resolved.Project.ManifestPath);
        var Generated = SolutionGenerator.Generate(new ResolvedSharpProject(UpdatedProject,
            Resolved.Engine));
        WriteResult(args, new
        {
            success = true,
            project = UpdatedProject.ManifestPath,
            target = Target,
            added = Add,
            targets = Targets,
            Generated.SolutionPath,
        });
        return 0;
    }

    private static int OpenSolution(string[] args, string? implicitProject, bool noUi)
    {
        var resolved = ResolveProject(args, implicitProject, prompt: !noUi);
        var generated = SolutionGenerator.EnsureCurrent(resolved);
        EnsureFirstUse(resolved.Engine.Root);
        _ = Process.Start(new ProcessStartInfo(generated.SolutionPath)
        {
            UseShellExecute = true,
        }) ?? throw new IOException("Could not open the generated Solution");
        WriteResult(args, new { success = true, generated.SolutionPath });
        return 0;
    }

    internal static int OpenEditor(string[] args, string? implicitProject, bool noUi, string target = "Editor")
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
            CreateNoWindow = true,
            WorkingDirectory = resolved.Project.Root,
        };
        if (target == "Editor")
        {
            EnsureFirstUse(resolved.Engine.Root);
        }
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(resolved.Project.ManifestPath);
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith("--render-backend=", StringComparison.Ordinal))
                start.ArgumentList.Add(args[index]);
            else if (args[index] == "--render-backend")
            {
                start.ArgumentList.Add(args[index]);
                if (index + 1 < args.Length) start.ArgumentList.Add(args[++index]);
            }
        }
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

    private static int PreviewProject(string[] Args)
    {
        var Browser = new ProjectBrowser(Option(Args, "--engine-root") ?? FindEngineRoot());
        var Preview = Browser.Preview(RequiredOption(Args, "--project"));
        WriteCommandResult(Args, Preview);
        return Preview.Compatible ? 0 : 1;
    }

    private static int LaunchBrowser(string[] Args)
    {
        var Engine = ProjectContracts.ReadEngine(Option(Args, "--engine-root") ?? FindEngineRoot());
        var Requested = Option(Args, "--configuration");
        var Preferred = Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))?.Name ?? "Development";
        var Configurations = Requested is null ? new[]
            {
                Preferred, "Development", "Debug", "Shipping",
            }.Where(Value => Value is "Debug" or "Development" or "Shipping").Distinct()
            : new[]
            {
                ValidateConfiguration(Requested),
            };
        var Executable = Configurations.Select(Configuration => Path.Combine(
                ProjectContracts.ResolveBinaries(Engine, "Win64", Configuration, "Editor"), "Engine.exe"))
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Build the engine Editor target before opening Project Browser");
        var Start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Engine.Root,
        };
        Start.ArgumentList.Add("--project-browser");
        using var Process = System.Diagnostics.Process.Start(Start) ?? throw new IOException("Could not start the engine Project Browser");
        return 0;
    }

    private static int PluginCommand(string[] Args)
    {
        var Project = ResolveProject(Args, null, false);
        var Graph = PluginGraph.Load(Project);
        if (Args[0].Equals("plugins", StringComparison.OrdinalIgnoreCase))
        {
            WriteCommandResult(Args, new
            {
                schemaVersion = 1,
                plugins = Graph.PluginIds.Select(Id => new
                {
                    id = Id,
                    enabled = Graph.IsEnabled(Id),
                }).ToArray(),
            });
            return 0;
        }
        var Action = RequiredOption(Args, "--action");
        if (Action is not ("enable" or "disable"))
        {
            throw new ArgumentException("Plugin action must be enable or disable");
        }
        var Plan = Graph.Plan(RequiredOption(Args, "--plugin"), Action == "enable");
        if (Args[0].Equals("apply-plugin", StringComparison.OrdinalIgnoreCase))
        {
            Graph.Apply(Plan, RequiredOption(Args, "--expected-plan"));
        }
        WriteCommandResult(Args, Plan);
        return Plan.Blockers.Count == 0 ? 0 : 1;
    }

    private static void WriteCommandResult(string[] Args, object Result)
    {
        var Serialized = JsonSerializer.Serialize(Result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        using var Document = JsonDocument.Parse(Serialized);
        WriteResult(Args, Document.RootElement);
        if (Option(Args, "--output") is null)
        {
            Console.WriteLine(Serialized);
        }
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
        var EngineRoot = RequiredOption(args, "--engine-root");
        SolutionGenerator.GenerateEngine(EngineRoot);
        EnsureFirstUse(EngineRoot);
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

    private static void RegisterFileAssociation(string EngineRoot)
    {
        var Executable = EngineFirstUse.ExecutablePath;
        EngineFirstUse.ValidateExecutable(Executable);
        if (new ProjectFileAssociation().Register(Executable, EngineRoot))
        {
            SharpLog.Info("ProjectSelector", "Registered .sharpproject shell actions and notified Explorer");
        }
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
