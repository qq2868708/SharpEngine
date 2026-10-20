using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;
using SharpEngine.Input;

namespace SharpEngine.Editor;

internal sealed class InputAssetEditorInstance : IAssetEditorInstance
{
    private readonly EditorAssetOpenContext Context;
    private readonly JsonAssetAuthoringDocument Document;

    public InputAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        Document = new JsonAssetAuthoringDocument(Context.Asset, Context.Path,
            Validate);
        Panes = IsContext ?
        [
            new("details", "Context", false),
            new("bindings", "Bindings"),
            new("summary", "Summary"),
            new("source", "Source"),
            new("diagnostics", "Diagnostics"),
        ] :
        [
            new("details", "Action", false),
            new("summary", "Summary"),
            new("source", "Source"),
            new("diagnostics", "Diagnostics"),
        ];
        Commands = IsContext ?
        [
            new("add-binding", "Add Binding", "Bindings", 50, "add", true),
            new("undo", "Undo", "Edit", 100, "reset", true),
            new("redo", "Redo", "Edit", 110, "reset", true),
        ] :
        [
            new("undo", "Undo", "Edit", 100, "reset", true),
            new("redo", "Redo", "Edit", 110, "reset", true),
        ];
    }

    public AssetDescriptor Asset => Context.Asset;
    public string EditorId => IsContext ? "input-context-editor" : "input-action-editor";
    public string Title => Asset.Name;
    public string PrimaryPaneId => "details";
    public bool IsDirty => Document.IsDirty;
    public bool IsReadOnly => Document.IsReadOnly;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; }
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; }
    private bool IsContext => Asset.Type.Equals("InputMappingContext",
        StringComparison.OrdinalIgnoreCase);

    public EditorLayoutDocument CreateDefaultLayout()
    {
        var Bottom = IsContext ? new[] { "bindings", "source", "diagnostics" } :
            ["source", "diagnostics"];
        return AssetEditorInstanceUtilities.CreateDocumentLayout("details", "summary",
            Bottom);
    }

    public bool CanExecuteCommand(string CommandId)
    {
        return CommandId switch
        {
            "add-binding" => IsContext && !IsReadOnly,
            "undo" => !IsReadOnly && Document.CanUndo,
            "redo" => !IsReadOnly && Document.CanRedo,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        if (CommandId == "undo")
        {
            Document.Undo();
        }
        else if (CommandId == "redo")
        {
            Document.Redo();
        }
        else if (CommandId == "add-binding")
        {
            AddBinding();
        }
    }

    public bool TryHandleAction(string Action)
    {
        const string FieldPrefix = "editor.input-field:";
        if (Action.StartsWith(FieldPrefix, StringComparison.Ordinal))
        {
            var Payload = Action[FieldPrefix.Length..];
            var Separator = Payload.IndexOf(':');
            if (Separator > 0)
            {
                SetField(Payload[..Separator], Payload[(Separator + 1)..]);
            }
            return true;
        }

        const string BindingPrefix = "editor.input-binding:";
        if (Action.StartsWith(BindingPrefix, StringComparison.Ordinal))
        {
            var Parts = Action[BindingPrefix.Length..].Split(':', 3);
            if (Parts.Length == 3 && int.TryParse(Parts[0], out var Index))
            {
                SetBindingField(Index, Parts[1], Parts[2]);
            }
            return true;
        }

        const string RemovePrefix = "editor.input-binding-remove:";
        if (Action.StartsWith(RemovePrefix, StringComparison.Ordinal) &&
            int.TryParse(Action[RemovePrefix.Length..], out var RemoveIndex))
        {
            RemoveBinding(RemoveIndex);
            return true;
        }
        if (Action == "editor.input-toggle-continuous")
        {
            Toggle("continuous");
            return true;
        }
        if (Action == "editor.input-toggle-active")
        {
            Toggle("activateByDefault");
            return true;
        }

        return false;
    }

    public string BuildPaneMarkup(string PaneId)
    {
        return PaneId switch
        {
            "details" => BuildDetailsMarkup(),
            "bindings" => BuildBindingsMarkup(),
            "summary" => BuildSummaryMarkup(),
            "source" => AssetEditorInstanceUtilities.Source(Document.Source),
            "diagnostics" => AssetEditorInstanceUtilities.Diagnostics([]),
            _ => string.Empty,
        };
    }

    public void Save()
    {
        Document.Save();
    }

    public void Update(float DeltaSeconds)
    {
    }

    public JsonObject Describe()
    {
        var Root = Document.Snapshot;
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assetId"] = Asset.Id,
            ["editorRootId"] = Context.EditorRootId,
            ["assetType"] = Asset.Type,
            ["dirty"] = IsDirty,
            ["readOnly"] = IsReadOnly,
            ["documentRevision"] = Document.Revision,
            ["savepointRevision"] = Document.SavepointRevision,
            ["bindingCount"] = Root["bindings"]?.AsArray().Count ?? 0,
            ["document"] = Root,
        };
    }

    public void Dispose()
    {
    }

    private void SetField(string Field, string Value)
    {
        var Changed = Document.Edit("input-field", Root =>
        {
            Root[Field] = Field switch
            {
                "priority" => int.Parse(Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture),
                _ => Value.Trim(),
            };
        }, out var Message);
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
    }

    private void Toggle(string Field)
    {
        var Changed = Document.Edit("input-toggle", Root =>
        {
            Root[Field] = !(Root[Field]?.GetValue<bool>() ?? false);
        }, out var Message);
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
    }

    private void AddBinding()
    {
        var Actions = Context.Runtime.Assets?.Assets.Where(Candidate =>
            Candidate.Type.Equals("InputAction", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Candidate => Candidate.Id, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        if (Actions.Length == 0)
        {
            Context.SetStatus("No InputAction asset is available");
            return;
        }

        var Changed = Document.Edit("input-binding-add", Root =>
        {
            Root["bindings"]!.AsArray().Add(new JsonObject
            {
                ["action"] = Actions[0].Id,
                ["control"] = "Keyboard/Space",
                ["scaleX"] = 1.0f,
                ["scaleY"] = 0.0f,
                ["modifiers"] = new JsonArray(),
                ["exactModifiers"] = false,
            });
        }, out var Message);
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
    }

    private void RemoveBinding(int Index)
    {
        var Changed = Document.Edit("input-binding-remove", Root =>
        {
            var Bindings = Root["bindings"]!.AsArray();
            if (Index < 0 || Index >= Bindings.Count)
            {
                throw new InvalidDataException("Binding index is out of range");
            }
            Bindings.RemoveAt(Index);
        }, out var Message);
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
    }

    private void SetBindingField(int Index, string Field, string Value)
    {
        var Changed = Document.Edit("input-binding-field", Root =>
        {
            var Bindings = Root["bindings"]!.AsArray();
            if (Index < 0 || Index >= Bindings.Count)
            {
                throw new InvalidDataException("Binding index is out of range");
            }
            var Binding = Bindings[Index]!.AsObject();
            Binding[Field] = Field switch
            {
                "scaleX" or "scaleY" => float.Parse(Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture),
                "exactModifiers" => bool.Parse(Value),
                "modifiers" => new JsonArray(Value.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(Item => (JsonNode)Item).ToArray()),
                _ => Value.Trim(),
            };
        }, out var Message);
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
    }

    private void Validate(JsonObject Root)
    {
        if (IsContext)
        {
            var ActionIds = Context.Runtime.Assets?.Assets.Where(Candidate =>
                Candidate.Type.Equals("InputAction", StringComparison.OrdinalIgnoreCase))
                .Select(Candidate => Candidate.Id).ToArray() ?? [];
            _ = InputRuntimeService.ParseMappingContextAsset(Root.ToJsonString(),
                Asset, ActionIds);
        }
        else
        {
            _ = InputRuntimeService.ParseActionAsset(Root.ToJsonString(), Asset);
        }
    }

    private string BuildDetailsMarkup()
    {
        var Root = Document.Snapshot;
        var Markup = new StringBuilder("<div class=\"asset-inspector\">")
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Asset ID", Asset.Id));
        if (IsContext)
        {
            Markup.Append(AssetEditorInstanceUtilities.Field("Route",
                Root["route"]!.GetValue<string>(), "editor.input-field:route:",
                IsReadOnly, "Editor, RuntimeUi, or Gameplay"))
                .Append(AssetEditorInstanceUtilities.Field("Priority",
                    Root["priority"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture),
                    "editor.input-field:priority:", IsReadOnly))
                .Append(ToggleButton("Active By Default",
                    Root["activateByDefault"]!.GetValue<bool>(),
                    "editor.input-toggle-active"));
        }
        else
        {
            Markup.Append(AssetEditorInstanceUtilities.Field("Value Type",
                Root["valueType"]!.GetValue<string>(),
                "editor.input-field:valueType:", IsReadOnly,
                "Button, Axis1D, or Axis2D"))
                .Append(ToggleButton("Continuous",
                    Root["continuous"]!.GetValue<bool>(),
                    "editor.input-toggle-continuous"));
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    private string BuildBindingsMarkup()
    {
        if (!IsContext)
        {
            return "<div class=\"ui-editor-empty\">InputAction has no bindings</div>";
        }

        var Markup = new StringBuilder(
            "<div class=\"asset-inspector input-bindings\">");
        var Bindings = Document.Snapshot["bindings"]!.AsArray();
        for (var Index = 0; Index < Bindings.Count; Index++)
        {
            var Binding = Bindings[Index]!.AsObject();
            var Prefix = $"editor.input-binding:{Index}:";
            if (IsReadOnly)
            {
                Markup.Append("<div class=\"ui-inspector-heading input-binding-heading\"><strong>Binding ")
                    .Append(Index + 1).Append("</strong></div>")
                    .Append(AssetEditorInstanceUtilities.ReadOnlyField("Action",
                        Binding["action"]!.GetValue<string>()))
                    .Append(AssetEditorInstanceUtilities.ReadOnlyField("Control",
                        Binding["control"]!.GetValue<string>()))
                    .Append(AssetEditorInstanceUtilities.ReadOnlyField("Scale",
                        Binding["scaleX"]!.ToJsonString() + ", " +
                        Binding["scaleY"]!.ToJsonString()))
                    .Append(AssetEditorInstanceUtilities.ReadOnlyField("Modifiers",
                        string.Join(", ", Binding["modifiers"]!.AsArray().Select(Node =>
                            Node!.GetValue<string>()))))
                    .Append(AssetEditorInstanceUtilities.ReadOnlyField("Exact Modifiers",
                        Binding["exactModifiers"]!.GetValue<bool>().ToString()));
                continue;
            }
            Markup.Append("<div class=\"ui-inspector-heading input-binding-heading\"><strong>Binding ")
                .Append(Index + 1).Append("</strong><button data-action=\"editor.input-binding-remove:")
                .Append(Index).Append("\" title=\"Remove binding\">Remove</button></div>")
                .Append(AssetEditorInstanceUtilities.Field("Action",
                    Binding["action"]!.GetValue<string>(), Prefix + "action:", IsReadOnly))
                .Append(AssetEditorInstanceUtilities.Field("Control",
                    Binding["control"]!.GetValue<string>(), Prefix + "control:", IsReadOnly))
                .Append(AssetEditorInstanceUtilities.Field("Scale X",
                    Binding["scaleX"]!.GetValue<float>().ToString("0.###",
                        CultureInfo.InvariantCulture), Prefix + "scaleX:", IsReadOnly))
                .Append(AssetEditorInstanceUtilities.Field("Scale Y",
                    Binding["scaleY"]!.GetValue<float>().ToString("0.###",
                        CultureInfo.InvariantCulture), Prefix + "scaleY:", IsReadOnly))
                .Append(AssetEditorInstanceUtilities.Field("Modifiers",
                    string.Join(", ", Binding["modifiers"]!.AsArray().Select(Node =>
                        Node!.GetValue<string>())), Prefix + "modifiers:", IsReadOnly))
                .Append(AssetEditorInstanceUtilities.Field("Exact Modifiers",
                    Binding["exactModifiers"]!.GetValue<bool>().ToString(),
                    Prefix + "exactModifiers:", IsReadOnly));
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }

    private string BuildSummaryMarkup()
    {
        var Root = Document.Snapshot;
        return "<div class=\"asset-inspector\">" +
            AssetEditorInstanceUtilities.ReadOnlyField("Type", Asset.Type) +
            AssetEditorInstanceUtilities.ReadOnlyField("Schema", "1") +
            AssetEditorInstanceUtilities.ReadOnlyField("Bindings",
                (Root["bindings"]?.AsArray().Count ?? 0).ToString()) +
            "</div>";
    }

    private string ToggleButton(string Label, bool Value, string Action)
    {
        var Disabled = IsReadOnly ? " disabled=\"disabled\"" : string.Empty;
        return $"<div class=\"asset-field\"><label>{AssetEditorInstanceUtilities.Escape(Label)}</label><button class=\"settings-toggle {(Value ? "enabled" : string.Empty)}\" data-action=\"{Action}\"{Disabled}>{(Value ? "Enabled" : "Disabled")}</button></div>";
    }
}
