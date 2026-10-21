using System.Net;
using System.Text;

namespace SharpEngine.ProjectSelector;

/// <summary>Projectless authoring tool state; all completions commit on the native UI thread.</summary>
internal sealed class BrowserUiApplication
{
    private sealed record WorkResult(string Kind, string? Manifest = null);

    private readonly ProjectBrowser Browser;
    private readonly string Configuration;
    private readonly List<string> Projects = [];
    private readonly Func<string?> PickProject;
    private readonly Func<string?> PickDirectory;
    private readonly Action<string> OpenProject;
    private readonly string CoverCache;
    private Task<WorkResult>? Work;
    private ProjectPreview? Preview;
    private string? CoverTexture;
    private string Status = "选择一个项目以开始";
    private string Diagnostic = "";
    private string ManifestInput = "";
    private string ProjectName = "MyGame";
    private string ParentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private bool Creating;
    private bool CloseRequested;
    private ulong StateRevision = 1;

    internal BrowserUiApplication(ProjectBrowser Browser, string Configuration,
        Func<string?> PickProject, Func<string?> PickDirectory, Action<string> OpenProject,
        string? CoverCache = null)
    {
        this.Browser = Browser;
        this.Configuration = Configuration;
        this.PickProject = PickProject;
        this.PickDirectory = PickDirectory;
        this.OpenProject = OpenProject;
        this.CoverCache = CoverCache ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SharpEngine", "Previews");
        try
        {
            Projects.AddRange(Browser.Recent());
            if (Projects.Count > 0)
            {
                Select(Projects[0]);
            }
        }
        catch (Exception Error)
        {
            Diagnostic = Error.Message;
        }
    }

    internal ulong Revision
    {
        get
        {
            return StateRevision;
        }
    }

    internal void RequestClose()
    {
        CloseRequested = true;
        StateRevision++;
    }

    internal bool IsBusy
    {
        get
        {
            return Work is not null;
        }
    }

    internal bool ShouldClose
    {
        get
        {
            return CloseRequested;
        }
    }

    internal object Snapshot()
    {
        return new
        {
            schemaVersion = 1,
            stateRevision = StateRevision,
            mode = "project-browser",
            configuration = Configuration,
            busy = IsBusy,
            status = Status,
            diagnostic = Diagnostic,
            selectedProject = Preview,
            projects = Projects.ToArray(),
            creating = Creating,
            editorAssemblyLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(Assembly =>
                Assembly.GetName().Name == "SharpEngine.Editor"),
            projectModulesLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(Assembly =>
                Assembly.GetCustomAttributesData().Any(Attribute =>
                    Attribute.AttributeType.FullName == "SharpEngine.Modules.SharpEngineModuleAttribute") &&
                Assembly.GetName().Name != "SharpEngine.Runtime"),
        };
    }

    internal void Handle(string Action)
    {
        if (Action is "window.close-requested" or "window.closed" or "window.close")
        {
            RequestClose();
            return;
        }
        if (IsBusy)
        {
            return;
        }
        try
        {
            if (Action.StartsWith("browser.input.manifest:", StringComparison.Ordinal))
            {
                ManifestInput = Action["browser.input.manifest:".Length..];
            }
            else if (Action.StartsWith("browser.input.name:", StringComparison.Ordinal))
            {
                ProjectName = Action["browser.input.name:".Length..];
            }
            else if (Action.StartsWith("browser.input.parent:", StringComparison.Ordinal))
            {
                ParentDirectory = Action["browser.input.parent:".Length..];
            }
            else
            {
                Execute(Action);
            }
        }
        catch (Exception Error)
        {
            Diagnostic = Error.Message;
            Status = "操作未完成";
        }
        StateRevision++;
    }

    private void Execute(string Action)
    {
        Diagnostic = "";
        if (Action.StartsWith("browser.select:", StringComparison.Ordinal))
        {
            var Index = int.Parse(Action["browser.select:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            Select(Projects[Index]);
            return;
        }
        switch (Action)
        {
            case "browser.pick-project":
                var Chosen = PickProject();
                if (Chosen is not null)
                {
                    AddProject(Chosen);
                }
                break;
            case "browser.load-path":
                AddProject(ManifestInput);
                break;
            case "browser.show-create":
                Creating = true;
                break;
            case "browser.pick-parent":
                var Directory = PickDirectory();
                if (Directory is not null)
                {
                    ParentDirectory = Directory;
                }
                break;
            case "browser.create":
                var Name = ProjectName;
                var Parent = ParentDirectory;
                Begin("正在创建项目", () => new WorkResult("created", Browser.Create(Name, Parent)));
                break;
            case "browser.open":
                var Manifest = RequiredProject();
                Begin("正在打开项目", () =>
                {
                    OpenProject(Manifest);
                    return new WorkResult("opened", Manifest);
                });
                break;
            case "browser.back":
                Creating = false;
                break;
            default:
                throw new ArgumentException($"Unknown Project Browser action: {Action}");
        }
    }

    private string RequiredProject()
    {
        if (Preview is null || !Preview.Compatible)
        {
            throw new InvalidOperationException("请先选择有效且兼容的项目");
        }
        return Preview.ManifestPath;
    }

    private void AddProject(string Manifest)
    {
        var FullPath = Path.GetFullPath(Manifest.Trim().Trim('"'));
        Select(FullPath);
        if (!Projects.Contains(FullPath, StringComparer.OrdinalIgnoreCase))
        {
            Projects.Insert(0, FullPath);
        }
        Browser.Remember(FullPath);
    }

    private void Select(string Manifest)
    {
        Preview = Browser.Preview(Manifest);
        ManifestInput = Manifest;
        Creating = false;
        CoverTexture = null;
        Status = Preview.Diagnostic;
        if (Preview.Compatible && Preview.ImagePath is not null)
        {
            CoverTexture = Browser.CreateCoverTexture(Preview.ImagePath, CoverCache);
        }
    }

    private void Begin(string Message, Func<WorkResult> Operation)
    {
        Status = Message;
        Work = Task.Run(Operation);
    }

    internal bool Tick()
    {
        if (Work is null || !Work.IsCompleted)
        {
            return false;
        }
        var Completed = Work;
        Work = null;
        try
        {
            var Result = Completed.GetAwaiter().GetResult();
            if (Result.Kind == "created")
            {
                AddProject(Result.Manifest!);
                Status = "项目已创建，点击打开项目进入 Editor";
            }
            else if (Result.Kind == "opened")
            {
                Browser.Remember(Result.Manifest!);
                Status = "项目已在 Editor 中打开";
                CloseRequested = true;
            }

        }
        catch (Exception Error)
        {
            Diagnostic = Error.Message;
            Status = "操作未完成，请检查错误后重试";
        }
        StateRevision++;
        return true;
    }

    private static string Text(string Value) { return WebUtility.HtmlEncode(Value); }

    private string Button(string Id, string Label, string Action, bool Available = true)
    {
        return $"<button id='{Id}' class='browser-button{(IsBusy || !Available ? " disabled" : "")}' action='{Text(Action)}' {(IsBusy || !Available ? "disabled='disabled'" : "")}>{Text(Label)}</button>";
    }

    internal string Render()
    {
        var Result = new StringBuilder();
        Result.Append("<div class='browser-heading'><span class='browser-title'>项目</span><span class='browser-subtitle'>选择、预览或创建项目</span><div class='grow'/>");
        Result.Append(Button("browser-new", "创建项目", "browser.show-create"));
        Result.Append(Button("browser-browse", "浏览项目…", "browser.pick-project"));
        Result.Append("</div><div class='browser-path'><input id='browser-path-input' type='text' data-change-action='browser.input.manifest:' value='");
        Result.Append(Text(ManifestInput));
        Result.Append(IsBusy ? "' disabled='disabled'/>" : "'/>");
        Result.Append(Button("browser-load-path", "预览项目", "browser.load-path"));
        Result.Append("</div><div class='browser-columns'><div class='browser-projects'><div class='section-label'>最近项目</div>");
        for (var Index = 0; Index < Projects.Count; Index++)
        {
            var Item = Browser.Preview(Projects[Index]);
            var Selected = Item.ManifestPath == Preview?.ManifestPath ? " scene" : "";
            Result.Append($"<button id='browser-project-{Index}' class='tree{Selected}' action='browser.select:{Index}' {(IsBusy ? "disabled='disabled'" : "")}>{Text(Item.Name)}<span class='browser-project-version'>{Text(Item.EngineVersion)}</span></button>");
        }
        if (Projects.Count == 0)
        {
            Result.Append("<div class='browser-empty'>暂无最近项目<br/>浏览已有项目或创建一个新项目。</div>");
        }
        Result.Append("</div><div class='browser-details'>");
        if (Creating)
        {
            RenderCreate(Result);
        }
        else
        {
            RenderPreview(Result);
        }
        Result.Append("</div></div><div class='browser-status'>");
        Result.Append(Text(Status));
        Result.Append("</div>");
        if (Diagnostic.Length > 0)
        {
            Result.Append($"<div id='browser-diagnostic' class='browser-diagnostic'>{Text(Diagnostic)}</div>");
        }
        return Result.ToString();
    }

    private void RenderCreate(StringBuilder Result)
    {
        Result.Append("<div class='browser-panel-title'>创建项目</div><div class='browser-field-label'>项目名称</div>");
        Result.Append($"<input id='browser-name-input' type='text' data-change-action='browser.input.name:' value='{Text(ProjectName)}' {(IsBusy ? "disabled='disabled'" : "")}/>");
        Result.Append("<div class='browser-field-label'>保存目录</div>");
        Result.Append($"<input id='browser-parent-input' type='text' data-change-action='browser.input.parent:' value='{Text(ParentDirectory)}' {(IsBusy ? "disabled='disabled'" : "")}/>");
        Result.Append("<div class='browser-actions'>");
        Result.Append(Button("browser-pick-parent", "选择目录…", "browser.pick-parent"));
        Result.Append(Button("browser-create", "创建项目", "browser.create"));
        Result.Append(Button("browser-create-back", "返回", "browser.back"));
        Result.Append("</div>");
    }

    private void RenderPreview(StringBuilder Result)
    {
        Result.Append("<div class='browser-cover'>");
        if (CoverTexture is null)
        {
            Result.Append("<div class='browser-cover-placeholder'>暂无已保存的项目封面</div>");
        }
        else
        {
            Result.Append($"<img id='browser-cover-image' src='{Text(CoverTexture.Replace('\\', '/'))}'/>");
        }
        Result.Append("</div>");
        if (Preview is null)
        {
            Result.Append("<div class='browser-empty'>选择项目以查看名称、路径和引擎版本。</div>");
            return;
        }
        Result.Append($"<div class='browser-panel-title'>{Text(Preview.Name)}</div><div class='browser-metadata'>引擎版本：{Text(Preview.EngineVersion)}<br/>引擎关联：{Text(Preview.EngineAssociation)}<br/>{Text(Preview.ManifestPath)}<br/>{Text(Preview.Diagnostic)}</div><div class='browser-actions'>");
        Result.Append(Button("browser-open", "打开项目", "browser.open", Preview.Compatible));
        Result.Append("</div>");
    }

}
