using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor.Layout;

namespace SharpEngine.Editor;

internal sealed class MaterialAssetEditorInstance : IAssetEditorInstance
{
    private readonly EditorAssetOpenContext Context;
    private readonly JsonAssetAuthoringDocument Document;
    private readonly StaticMeshAsset PreviewSphere;
    private MaterialAssetDescription Material = null!;

    public MaterialAssetEditorInstance(EditorAssetOpenContext Context)
    {
        this.Context = Context;
        Document = new JsonAssetAuthoringDocument(Context.Asset, Context.Path,
            Validate);
        RefreshAsset();
        var Assets = Context.Runtime.Assets ?? throw new InvalidOperationException(
            "Asset registry is unavailable");
        PreviewSphere = AssetContent.LoadStaticMesh(Assets, "engine://meshes/sphere");
    }

    public AssetDescriptor Asset => Context.Asset;
    public string EditorId => "material-editor";
    public string Title => Asset.Name;
    public string PrimaryPaneId => "preview";
    public bool IsDirty => Document.IsDirty;
    public bool IsReadOnly => Document.IsReadOnly;
    public IReadOnlyList<AssetEditorPaneDefinition> Panes { get; } =
    [
        new("preview", "Preview", false),
        new("details", "Details"),
        new("source", "Source"),
        new("diagnostics", "Diagnostics"),
    ];
    public IReadOnlyList<AssetEditorCommandDefinition> Commands { get; } =
    [
        new("undo", "Undo", "Edit", 100, "reset", true),
        new("redo", "Redo", "Edit", 110, "reset", true),
    ];

    public EditorLayoutDocument CreateDefaultLayout() =>
        AssetEditorInstanceUtilities.CreatePreviewLayout("preview", "details",
            "source", "diagnostics");

    public bool CanExecuteCommand(string CommandId)
    {
        return CommandId switch
        {
            "undo" => !IsReadOnly && Document.CanUndo,
            "redo" => !IsReadOnly && Document.CanRedo,
            _ => false,
        };
    }

    public void ExecuteCommand(string CommandId)
    {
        var Changed = CommandId == "undo" ? Document.Undo() :
            CommandId == "redo" && Document.Redo();
        if (Changed)
        {
            RefreshAsset();
        }
    }

    public bool TryHandleAction(string Action)
    {
        const string Prefix = "editor.material-field:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var Payload = Action[Prefix.Length..];
        var Separator = Payload.IndexOf(':');
        if (Separator <= 0)
        {
            return true;
        }
        var Field = Payload[..Separator];
        var Value = Payload[(Separator + 1)..].Trim();
        var Changed = Document.Edit("material-field", Root =>
        {
            SetField(Root, Field, Value);
        }, out var Message);
        if (Changed)
        {
            RefreshAsset();
        }
        else if (Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
        return true;
    }

    public string BuildPaneMarkup(string PaneId)
    {
        return PaneId switch
        {
            "preview" => "<div class=\"asset-preview-surface material-preview\"><viewport id=\"materialPreviewViewport\" data-viewport-kind=\"asset-material-preview\" data-input-surface=\"material-preview\" /><div class=\"asset-preview-overlay\"><span>" +
                AssetEditorInstanceUtilities.Escape(Material.Name) +
                "</span><span>" + AssetEditorInstanceUtilities.Escape(Material.Shader) +
                "</span></div></div>",
            "details" => BuildDetailsMarkup(),
            "source" => AssetEditorInstanceUtilities.Source(Document.Source),
            "diagnostics" => AssetEditorInstanceUtilities.Diagnostics([]),
            _ => string.Empty,
        };
    }

    public void Save()
    {
        Document.Save();
        RefreshAsset();
    }

    public void Update(float DeltaSeconds)
    {
        if (!Context.TryGetElementRect("materialPreviewViewport", out var Bounds))
        {
            return;
        }

        var Color = new Vector4(Material.BaseColor.R, Material.BaseColor.G,
            Material.BaseColor.B, Material.BaseColor.A);
        Context.SubmitViewportFrame("asset-material-preview",
            AssetPreviewRenderer.BuildMeshFrame(PreviewSphere.Positions,
                PreviewSphere.Indices, Bounds, Color,
                $"material-preview-camera:{Asset.Id}"));
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["assetId"] = Asset.Id,
            ["editorRootId"] = Context.EditorRootId,
            ["dirty"] = IsDirty,
            ["readOnly"] = IsReadOnly,
            ["documentRevision"] = Document.Revision,
            ["savepointRevision"] = Document.SavepointRevision,
            ["shader"] = Material.Shader,
            ["baseColor"] = new JsonArray(Material.BaseColor.R,
                Material.BaseColor.G, Material.BaseColor.B, Material.BaseColor.A),
            ["metallic"] = Material.Metallic,
            ["roughness"] = Material.Roughness,
        };
    }

    public void Dispose()
    {
    }

    private void RefreshAsset()
    {
        var Root = Document.Snapshot;
        var Color = Root["baseColor"]!.AsArray().Select(Node =>
            Node!.GetValue<float>()).ToArray();
        Material = new MaterialAssetDescription(
            Root["name"]!.GetValue<string>(), Root["shader"]!.GetValue<string>(),
            new AssetColor(Color[0], Color[1], Color[2], Color[3]),
            Root["metallic"]!.GetValue<float>(),
            Root["roughness"]!.GetValue<float>());
    }

    private static void Validate(JsonObject Root)
    {
        var Allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "$schema", "schemaVersion", "name", "shader", "baseColor",
            "metallic", "roughness",
        };
        if (Root.Any(Property => !Allowed.Contains(Property.Key)) ||
            Root["schemaVersion"]?.GetValue<int>() != 1 ||
            string.IsNullOrWhiteSpace(Root["name"]?.GetValue<string>()) ||
            string.IsNullOrWhiteSpace(Root["shader"]?.GetValue<string>()))
        {
            throw new InvalidDataException("Invalid Material schema");
        }

        var Color = Root["baseColor"]?.AsArray() ??
            throw new InvalidDataException("Material baseColor is required");
        if (Color.Count != 4 || Color.Any(Node => !TryUnit(Node)))
        {
            throw new InvalidDataException("Material baseColor requires four values from 0 to 1");
        }
        if (!TryUnit(Root["metallic"]) || !TryUnit(Root["roughness"]))
        {
            throw new InvalidDataException("Material metallic and roughness must be from 0 to 1");
        }
    }

    private static bool TryUnit(JsonNode? Node)
    {
        try
        {
            var Value = Node?.GetValue<float>() ?? float.NaN;
            return float.IsFinite(Value) && Value is >= 0 and <= 1;
        }
        catch (Exception Exception) when (Exception is InvalidOperationException or
            FormatException)
        {
            return false;
        }
    }

    private static void SetField(JsonObject Root, string Field, string Value)
    {
        if (Field == "shader")
        {
            Root["shader"] = Value;
            return;
        }

        var Number = float.Parse(Value, NumberStyles.Float,
            CultureInfo.InvariantCulture);
        if (Field == "metallic" || Field == "roughness")
        {
            Root[Field] = Number;
            return;
        }

        var Index = Field switch
        {
            "color-r" => 0,
            "color-g" => 1,
            "color-b" => 2,
            "color-a" => 3,
            _ => throw new InvalidDataException($"Unknown Material field '{Field}'"),
        };
        Root["baseColor"]!.AsArray()[Index] = Number;
    }

    private string BuildDetailsMarkup()
    {
        var Markup = new StringBuilder("<div class=\"asset-inspector\">")
            .Append(AssetEditorInstanceUtilities.ReadOnlyField("Asset ID", Asset.Id))
            .Append(AssetEditorInstanceUtilities.Field("Shader", Material.Shader,
                "editor.material-field:shader:", IsReadOnly));
        var Components = new[]
        {
            ("Base Color R", "color-r", Material.BaseColor.R),
            ("Base Color G", "color-g", Material.BaseColor.G),
            ("Base Color B", "color-b", Material.BaseColor.B),
            ("Base Color A", "color-a", Material.BaseColor.A),
            ("Metallic", "metallic", Material.Metallic),
            ("Roughness", "roughness", Material.Roughness),
        };
        foreach (var Component in Components)
        {
            Markup.Append(AssetEditorInstanceUtilities.Field(Component.Item1,
                Component.Item3.ToString("0.###", CultureInfo.InvariantCulture),
                $"editor.material-field:{Component.Item2}:", IsReadOnly, "0..1"));
        }
        Markup.Append("</div>");
        return Markup.ToString();
    }
}
