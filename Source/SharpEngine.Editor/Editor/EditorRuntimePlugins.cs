using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private const string ProjectPluginsHost = "settings.plugins";
    private Task<JsonObject>? ProjectPluginsWork;
    private string ProjectPluginsOperation = "";
    private JsonArray ProjectPluginsList = new();
    private JsonObject? ProjectPluginsPlan;
    private string? ProjectPluginsDiagnostic;
    private ulong ProjectPluginsRevision;
    private bool ProjectPluginsOpen;
    private bool ProjectPluginsRestartRequired;
    private bool ProjectPluginsPreviousRestart;
    private string? ProjectPluginsManifestBeforeApply;

    public string DescribeProjectPluginsJson()
    {
        return ProjectPluginsState().ToJsonString();
    }

    private JsonObject ProjectPluginsState()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["revision"] = ProjectPluginsRevision,
            ["projectManifest"] = _projectManifest,
            ["busy"] = ProjectPluginsWork is not null,
            ["operation"] = ProjectPluginsOperation,
            ["plugins"] = ProjectPluginsList.DeepClone(),
            ["pendingPlan"] = ProjectPluginsPlan?.DeepClone(),
            ["diagnostic"] = ProjectPluginsDiagnostic,
            ["restartRequired"] = ProjectPluginsRestartRequired,
            ["settingsOpen"] = ProjectPluginsOpen,
        };
    }

    public JsonObject RequestProjectPlugins(string Operation, ulong ExpectedRevision,
        string? PluginId = null, string? ExpectedPlan = null)
    {
        if (Environment.CurrentManagedThreadId != McpOwnerThread || _projectManifest is null ||
            ExpectedRevision != ProjectPluginsRevision || ProjectPluginsWork is not null)
        {
            throw new InvalidOperationException("PLUGINS_REVISION_CONFLICT: Read plugins.describe and retry when idle on the Editor thread");
        }
        if (Operation == "cancel")
        {
            ProjectPluginsPlan = null;
            ProjectPluginsRevision++;
            RefreshProjectPlugins();
            return ProjectPluginsState();
        }
        if (PlayState != EditorPlayState.Stopped)
        {
            throw new InvalidOperationException("PLUGINS_STOP_REQUIRED: Stop Play before changing project Plugin configuration");
        }
        var Arguments = new List<string>();
        switch (Operation)
        {
            case "analyze":
                Arguments.Add("plugins");
                break;
            case "plan-enable":
            case "plan-disable":
                if (string.IsNullOrWhiteSpace(PluginId))
                {
                    throw new ArgumentException("A Plugin id is required");
                }
                Arguments.AddRange(["plan-plugin", "--plugin", PluginId, "--action", Operation == "plan-enable" ? "enable" : "disable"]);
                break;
            case "apply":
                var PlanId = ProjectPluginsPlan?["planId"]?.GetValue<string>();
                if (PlanId is null || ExpectedPlan != PlanId || ProjectPluginsPlan!["blockers"]!.AsArray().Count > 0 ||
                    ProjectPluginsPlan["changes"]!.AsArray().Count == 0)
                {
                    throw new InvalidOperationException("PLUGINS_PLAN_REQUIRED: Preview an unblocked plan and confirm its exact planId");
                }
                Arguments.AddRange([
                    "apply-plugin", "--plugin", ProjectPluginsPlan["pluginId"]!.GetValue<string>(),
                    "--action", ProjectPluginsPlan["enable"]!.GetValue<bool>() ? "enable" : "disable",
                    "--expected-plan", PlanId,
                ]);
                ProjectPluginsPreviousRestart = ProjectPluginsRestartRequired;
                ProjectPluginsManifestBeforeApply = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_projectManifest)));
                ProjectPluginsRestartRequired = true;
                break;
            default:
                throw new ArgumentException($"Unknown Plugin operation: {Operation}");
        }
        ProjectPluginsOperation = Operation;
        ProjectPluginsDiagnostic = null;
        ProjectPluginsWork = Task.Run(() => RunProjectPluginsTool(Arguments));
        ProjectPluginsRevision++;
        RefreshProjectPlugins();
        return ProjectPluginsState();
    }

    private JsonObject RunProjectPluginsTool(IReadOnlyList<string> Arguments)
    {
        var Configuration = Directory.GetParent(Path.GetDirectoryName(typeof(EditorRuntime).Assembly.Location)!)!.Name;
        var Executable = Path.Combine(_engineRoot, "Binaries", "Win64", Configuration, "Tools", "SharpEngine.ProjectSelector.exe");
        if (!File.Exists(Executable))
        {
            throw new FileNotFoundException("Build the matching Project Selector tool before analyzing Plugins", Executable);
        }
        var DirectoryPath = Path.Combine(_projectRoot!, "Saved", "PluginConfiguration");
        Directory.CreateDirectory(DirectoryPath);
        var OutputPath = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var Start = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _projectRoot!,
            };
            foreach (var Argument in Arguments.Concat(new[]
                     {
                         "--project", _projectManifest!, "--engine-root", _engineRoot,
                         "--no-ui", "--output", OutputPath,
                     }))
            {
                Start.ArgumentList.Add(Argument);
            }
            using var Process = System.Diagnostics.Process.Start(Start) ?? throw new IOException("Could not analyze project Plugins");
            var Output = Process.StandardOutput.ReadToEndAsync();
            var Errors = Process.StandardError.ReadToEndAsync();
            if (!Process.WaitForExit(180000))
            {
                Process.Kill(true);
                throw new IOException("Plugin analysis timed out");
            }
            Task.WaitAll(Output, Errors);
            var Result = File.Exists(OutputPath) ? JsonNode.Parse(File.ReadAllText(OutputPath))!.AsObject() :
                throw new IOException(Output.Result + Errors.Result);
            var Error = Result["error"]?.GetValue<string>();
            if (Error is not null)
            {
                throw new InvalidDataException(Error);
            }
            return Result;
        }
        finally
        {
            File.Delete(OutputPath);
        }
    }

    private void TickProjectPlugins()
    {
        if (ProjectPluginsWork is null || !ProjectPluginsWork.IsCompleted)
        {
            return;
        }
        var Work = ProjectPluginsWork;
        ProjectPluginsWork = null;
        try
        {
            var Result = Work.GetAwaiter().GetResult();
            if (ProjectPluginsOperation == "analyze")
            {
                ProjectPluginsList = Result["plugins"]!.DeepClone().AsArray();
                ProjectPluginsPlan = null;
            }
            else if (ProjectPluginsOperation == "apply")
            {
                foreach (var Change in Result["changes"]!.AsArray())
                {
                    var Plugin = ProjectPluginsList.FirstOrDefault(Item => Item!["id"]!.GetValue<string>() == Change!["id"]!.GetValue<string>());
                    if (Plugin is not null)
                    {
                        Plugin["enabled"] = Change!["enabled"]!.GetValue<bool>();
                    }
                }
                ProjectPluginsPlan = null;
                SetStatus("Plugin configuration saved; rebuild and restart Editor to activate it");
            }
            else
            {
                ProjectPluginsPlan = Result;
            }
        }
        catch (Exception Error)
        {
            ProjectPluginsDiagnostic = Error.Message;
            ProjectPluginsPlan = null;
            if (ProjectPluginsOperation == "apply")
            {
                var ManifestUnchanged = false;
                try
                {
                    ManifestUnchanged = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_projectManifest!))) == ProjectPluginsManifestBeforeApply;
                }
                catch (Exception ReadError) when (ReadError is IOException or UnauthorizedAccessException)
                {
                }
                ProjectPluginsRestartRequired = ProjectPluginsPreviousRestart || !ManifestUnchanged;
            }
        }
        ProjectPluginsOperation = "";
        ProjectPluginsRevision++;
        RefreshProjectPlugins();
    }

    private bool HandleProjectPluginsAction(string HostId, string Action)
    {
        if (HostId == ProjectPluginsHost && Action is "window.closed" or "window.close-requested")
        {
            ProjectPluginsOpen = false;
            _nativeApi.DestroyDockWindow(ProjectPluginsHost);
            return true;
        }
        if (Action == "editor.project-plugins")
        {
            if (_projectManifest is null || _nativeApi.CreateDockWindow(ProjectPluginsHost, "Project Plugins") == 0)
            {
                return true;
            }
            ProjectPluginsOpen = true;
            _nativeApi.SetHostMarkup(ProjectPluginsHost, "dockWindowTitle", "Project Plugins");
            RefreshProjectPlugins();
            return true;
        }
        const string Prefix = "editor.plugins:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Parts = Action[Prefix.Length..].Split(':', 2);
        try
        {
            RequestProjectPlugins(Parts[0], ProjectPluginsRevision,
                Parts.Length > 1 ? Parts[1] : null, ProjectPluginsPlan?["planId"]?.GetValue<string>());
        }
        catch (Exception Error)
        {
            ProjectPluginsDiagnostic = Error.Message;
            RefreshProjectPlugins();
        }
        return true;
    }

    private void RefreshProjectPlugins()
    {
        if (!ProjectPluginsOpen)
        {
            return;
        }
        static string Text(string Value) { return WebUtility.HtmlEncode(Value); }
        var Disabled = ProjectPluginsWork is not null || PlayState != EditorPlayState.Stopped;
        string Button(string Id, string Label, string Action, bool Available = true)
        {
            return $"<button id='{Id}' class='plugins-button{(Disabled || !Available ? " disabled" : "")}' action='{Text(Action)}' {(Disabled || !Available ? "disabled='disabled'" : "")}>{Text(Label)}</button>";
        }
        var Result = new StringBuilder("<div class='settings-window'><div class='settings-main plugins-main'><div class='settings-title'>项目插件</div>");
        Result.Append("<div class='plugins-note'>配置在重建并重新启动 Editor 后生效。依赖来自各模块的工程引用。</div>");
        Result.Append(Button("plugins-analyze", "分析项目插件", "editor.plugins:analyze"));
        if (ProjectPluginsWork is not null)
        {
            Result.Append("<div class='plugins-note'>正在分析与校验模块依赖…</div>");
        }
        if (ProjectPluginsRestartRequired)
        {
            Result.Append("<div id='plugins-restart-required' class='plugins-note'>配置已变更：请重建项目并重新启动 Editor。</div>");
        }
        if (ProjectPluginsPlan is not null)
        {
            Result.Append("<div class='plugins-plan'><div class='settings-title'>完整变更清单</div>");
            var ChangeIndex = 0;
            foreach (var Change in ProjectPluginsPlan["changes"]!.AsArray())
            {
                Result.Append($"<div class='plugins-row'><span id='plugins-change-{ChangeIndex}'>{(Change!["enabled"]!.GetValue<bool>() ? "启用" : "禁用")} {Text(Change["id"]!.GetValue<string>())}{(Change["clearsExplicitDisable"]!.GetValue<bool>() ? " — 撤销项目显式禁用" : "")}</span></div>");
                ChangeIndex++;
            }
            foreach (var Blocker in ProjectPluginsPlan["blockers"]!.AsArray())
            {
                Result.Append($"<div class='plugins-error'>{Text(Blocker!.GetValue<string>())}</div>");
            }
            Result.Append(Button("plugins-confirm", "确认保存", "editor.plugins:apply",
                ProjectPluginsPlan["blockers"]!.AsArray().Count == 0 && ProjectPluginsPlan["changes"]!.AsArray().Count > 0));
            Result.Append(Button("plugins-cancel", "取消", "editor.plugins:cancel"));
            Result.Append("</div>");
        }
        else
        {
            var Index = 0;
            foreach (var Plugin in ProjectPluginsList)
            {
                var Id = Plugin!["id"]!.GetValue<string>();
                Result.Append($"<div class='plugins-row'><div class='grow'>{Text(Id)}<div class='plugins-note'>{(Plugin["enabled"]!.GetValue<bool>() ? "配置已启用" : "配置已禁用")}</div></div>");
                Result.Append(Button($"plugins-enable-{Index}", "预览启用", "editor.plugins:plan-enable:" + Id));
                Result.Append(Button($"plugins-disable-{Index}", "预览禁用", "editor.plugins:plan-disable:" + Id));
                Result.Append("</div>");
                Index++;
            }
        }
        if (ProjectPluginsDiagnostic is not null)
        {
            Result.Append($"<div class='plugins-error'>{Text(ProjectPluginsDiagnostic)}</div>");
        }
        Result.Append("</div></div>");
        _nativeApi.SetHostMarkup(ProjectPluginsHost, "workspace", Result.ToString());
    }
}
