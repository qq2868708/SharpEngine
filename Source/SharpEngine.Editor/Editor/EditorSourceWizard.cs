using System.Text;
using System.Text.Json;

namespace SharpEngine.Editor;

public sealed partial class EditorRuntime
{
    private const string SourceWizardHost = "source.create";
    private readonly int _sourceOwnerThread = Environment.CurrentManagedThreadId;
    private IReadOnlyList<SourceModule> _sourceModules = [];
    private int _sourceModuleIndex;
    private string _sourceDirectory = "", _sourceName = "", _sourceTemplate = "component";
    private bool _sourceWizardOpen;

    private CSharpSourceCreation SourceCommands()
    {
        if (_projectManifest is null) throw new InvalidOperationException("Open a project before creating C# source");
        var location = new DirectoryInfo(Path.GetDirectoryName(typeof(EditorRuntime).Assembly.Location)!);
        var configuration = location.Parent?.Name is "Debug" or "Development" or "Shipping" ? location.Parent.Name : "Debug";
        return new(_projectManifest, _engineRoot, configuration, "Win64");
    }

    public string DescribeSourceModulesJson() => JsonSerializer.Serialize(new
    { schemaVersion = 1, modules = SourceCommands().Modules() }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public CreateSourceResult CreateCSharpSource(CreateSourceRequest request)
    {
        if (Environment.CurrentManagedThreadId != _sourceOwnerThread)
            return new(1, false, "WRONG_THREAD", "Source creation must run on the editor thread");
        var result = SourceCommands().Create(request);
        if (result.Success && RequestProjectBuild is not null)
        {
            RequestProjectBuild();
            result = result with { Message = "C# source created; project build queued" };
        }
        SetStatus(result.Message);
        return result;
    }

    private void OpenSourceWizard()
    {
        try
        {
            _sourceModules = SourceCommands().Modules();
            if (_sourceModules.Count == 0) throw new InvalidOperationException("Project has no Modules");
            _sourceModuleIndex = 0; _sourceDirectory = ""; _sourceName = "";
            _sourceTemplate = _sourceModules[0].Type == "Runtime" ? "component" : "class";
            if (_nativeApi.CreateDockWindow(SourceWizardHost, "New C# Class") == 0)
                throw new InvalidOperationException("Source wizard requires the document UI host");
            _sourceWizardOpen = true;
            _nativeApi.SetHostMarkup(SourceWizardHost, "dockWindowTitle", "New C# Class");
            RefreshSourceWizard();
        }
        catch (Exception error) { SetStatus("New C# Class: " + error.Message); }
    }

    private bool HandleSourceAction(string host, string action)
    {
        if (host == SourceWizardHost && action == "window.closed")
        { _sourceWizardOpen = false; _nativeApi.DestroyDockWindow(SourceWizardHost); return true; }
        if (!action.StartsWith("editor.source-", StringComparison.Ordinal)) return false;
        try
        {
            if (action == "editor.source-open") { OpenSourceWizard(); return true; }
            if (action == "editor.source-build") { RequestProjectBuild?.Invoke(); return true; }
            if (action == "editor.source-cancel")
            { _sourceWizardOpen = false; _nativeApi.DestroyDockWindow(SourceWizardHost); return true; }
            if (!_sourceWizardOpen) return true;
            if (action.StartsWith("editor.source-module:"))
            {
                var index = int.Parse(action["editor.source-module:".Length..]);
                if (index < 0 || index >= _sourceModules.Count) throw new ArgumentException("Unknown Module");
                _sourceModuleIndex = index;
                _sourceTemplate = _sourceModules[index].Type == "Runtime" ? "component" : "class";
                RefreshSourceWizard();
            }
            else if (action.StartsWith("editor.source-template:"))
            { _sourceTemplate = action["editor.source-template:".Length..]; RefreshSourceWizard(); }
            else if (action.StartsWith("editor.source-directory:"))
            {
                _sourceDirectory = action["editor.source-directory:".Length..];
                _nativeApi.SetHostMarkup(SourceWizardHost, "source-namespace", Escape(SourceNamespace()));
            }
            else if (action.StartsWith("editor.source-name:")) _sourceName = action["editor.source-name:".Length..];
            else if (action == "editor.source-create")
            {
                var result = CreateCSharpSource(new(_sourceModules[_sourceModuleIndex].ProjectPath,
                    _sourceDirectory, _sourceName, _sourceTemplate));
                _nativeApi.SetHostMarkup(SourceWizardHost, "source-feedback", "<span>" + Escape(result.Success
                    ? $"Created {_sourceName}.cs. Build queued; reload applies when Stopped." : result.Message) + "</span>");
            }
        }
        catch (Exception error)
        { _nativeApi.SetHostMarkup(SourceWizardHost, "source-feedback", "<span>" + Escape(error.Message) + "</span>"); SetStatus(error.Message); }
        return true;
    }

    private string SourceNamespace() => _sourceModules[_sourceModuleIndex].RootNamespace +
        (_sourceDirectory.Length == 0 ? "" : "." + _sourceDirectory.Replace('\\', '.').Replace('/', '.'));

    private void RefreshSourceWizard()
    {
        var module = _sourceModules[_sourceModuleIndex];
        var html = new StringBuilder("<div class=\"settings-window\"><div class=\"settings-sidebar\"><div class=\"settings-title\">Module</div>");
        for (var index = 0; index < _sourceModules.Count; index++)
            html.Append("<button id=\"source-module-").Append(index).Append("\" class=\"settings-page ")
                .Append(index == _sourceModuleIndex ? "active" : "").Append("\" data-action=\"editor.source-module:")
                .Append(index).Append("\"><span>").Append(Escape(_sourceModules[index].Id + " · " + _sourceModules[index].Type)).Append("</span></button>");
        html.Append("</div><div class=\"settings-content\"><div class=\"settings-header\"><span class=\"settings-heading\">New C# Class</span></div><div class=\"settings-fields\">");
        html.Append("<div class=\"settings-field\"><div class=\"settings-field-copy\"><label>Template</label></div><div class=\"settings-control\"><button id=\"source-template-class\" class=\"settings-action ")
            .Append(_sourceTemplate == "class" ? "primary" : "").Append("\" data-action=\"editor.source-template:class\">Plain Class</button>");
        if (module.Type == "Runtime") html.Append("<button id=\"source-template-component\" class=\"settings-action ")
            .Append(_sourceTemplate == "component" ? "primary" : "").Append("\" data-action=\"editor.source-template:component\">Component</button>");
        html.Append("</div></div>");
        html.Append("<div class=\"settings-field\"><div class=\"settings-field-copy\"><label>Module directory</label></div><span>").Append(Escape(Path.GetDirectoryName(module.ProjectPath) ?? "")).Append("</span></div>");
        Field("directory", "Subdirectory", _sourceDirectory);
        Field("name", "Type name", _sourceName);
        html.Append("<div class=\"settings-field\"><div class=\"settings-field-copy\"><label>Namespace</label><span>From Module and directory</span></div><span id=\"source-namespace\">").Append(Escape(SourceNamespace())).Append("</span></div>");
        html.Append("<div class=\"settings-field\"><button id=\"source-create\" class=\"settings-action primary\" data-action=\"editor.source-create\">Create</button><button class=\"settings-action\" data-action=\"editor.source-cancel\">Close</button></div></div><div id=\"source-feedback\" class=\"settings-footer\" style=\"height: 46px;\"><span>Creates one .cs file. Existing files are never overwritten.</span></div></div></div>");
        _nativeApi.SetHostMarkup(SourceWizardHost, "workspace", html.ToString());
        void Field(string id, string label, string value) => html.Append("<div class=\"settings-field\"><div class=\"settings-field-copy\"><label>").Append(label)
            .Append("</label></div><div class=\"settings-control\"><input id=\"source-").Append(id).Append("\" value=\"").Append(Escape(value))
            .Append("\" data-change-action=\"editor.source-").Append(id).Append(":\" /></div></div>");
    }
}
