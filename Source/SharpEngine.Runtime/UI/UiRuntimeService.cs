using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpEngine.Assets;
using SharpEngine.Modules;

namespace SharpEngine.UI;

internal sealed class UiRuntimeService : IUiRuntime
{
    private readonly Dictionary<string, UiViewModelContract> _contracts =
        new(StringComparer.Ordinal);
    private IUiScreenHost? _screenHost;
    private AssetRegistry? _assets;
    private string? _activeScreenId;
    private UiScreenRequest? _activeRequest;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    public IReadOnlyList<UiViewModelContract> ViewModelContracts
    {
        get
        {
            lock (_contracts)
                return _contracts.Values.OrderBy(value => value.Key,
                    StringComparer.Ordinal).ToArray();
        }
    }

    public string? ActiveScreenId
    {
        get
        {
            CheckThread();
            return _activeScreenId;
        }
    }

    /// <summary>Binds the native presentation and asset source. Called once by the host
    /// that owns the UI shell, before project Modules start.</summary>
    internal void AttachScreenHost(IUiScreenHost? ScreenHost, AssetRegistry? Assets)
    {
        CheckThread();
        _screenHost = ScreenHost;
        _assets = Assets;
    }

    public UiScreenResult ShowScreen(string AssetId) =>
        ShowScreen(new UiScreenRequest(AssetId ?? string.Empty));

    public UiScreenResult ShowScreen(UiScreenRequest Request)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(Request);
        var Requested = (Request.AssetId ?? string.Empty).Trim();
        if (Requested.Length == 0)
        {
            return new(false, "UI_SCREEN_ASSET_INVALID", null,
                "A UI screen asset id is required");
        }
        if (_screenHost is null || _assets is null)
        {
            return new(false, "UI_SCREEN_UNAVAILABLE", Requested,
                "This host does not present runtime UI screens");
        }

        var Asset = _assets.Find(Requested);
        if (Asset is null)
        {
            return new(false, "UI_SCREEN_ASSET_MISSING", Requested,
                $"UI screen asset '{Requested}' is not registered");
        }

        var PathValue = _assets.ResolvePath(Asset);
        if (PathValue is null || !File.Exists(PathValue))
        {
            return new(false, "UI_SCREEN_FILE_MISSING", Asset.Id,
                $"UI screen source for '{Asset.Id}' is missing");
        }

        UiDocumentAsset? Package = null;
        string Rml;
        try
        {
            if (Asset.Type.Equals("UiDocument", StringComparison.OrdinalIgnoreCase))
            {
                Package = UiDocumentAsset.Load(PathValue);
                Rml = Package.Rml;
            }
            else if (Asset.Type.Equals("RmlDocument", StringComparison.OrdinalIgnoreCase))
            {
                Rml = File.ReadAllText(PathValue);
            }
            else
            {
                Rml = string.Empty;
            }
        }
        catch (Exception Error) when (Error is InvalidDataException or IOException or
            UnauthorizedAccessException or JsonException)
        {
            return new(false, "UI_SCREEN_INVALID", Asset.Id, Error.Message);
        }
        if (Rml.Length == 0)
        {
            return new(false, "UI_SCREEN_TYPE_UNSUPPORTED", Asset.Id,
                $"Asset type '{Asset.Type}' is not a UI screen");
        }

        string Document;
        if (Package is { IsTemplate: true })
        {
            try
            {
                Document = ComposeTemplate(Package, Request);
            }
            catch (Exception Error) when (Error is InvalidDataException or
                InvalidOperationException or FormatException)
            {
                return new(false, "UI_SCREEN_COMPOSE_FAILED", Asset.Id, Error.Message);
            }
        }
        else
        {
            Document = Rml;
        }

        var FullPath = Path.GetFullPath(PathValue);
        if (!_screenHost.SetDocument(Document, FullPath, out var Message))
        {
            return new(false, "UI_SCREEN_PRESENT_FAILED", Asset.Id,
                Message.Length == 0 ? "The UI document could not be presented" : Message);
        }

        _activeScreenId = Asset.Id;
        _activeRequest = Request with
        {
            AssetId = Asset.Id,
        };
        return new(true, "UI_SCREEN_SHOWN", Asset.Id, $"Presenting {Asset.Name}");
    }

    public bool HandleScreenAction(string Action)
    {
        CheckThread();
        if (_screenHost is null || _activeScreenId is null ||
            _activeRequest?.Parts is not { Count: > 0 } Parts ||
            string.IsNullOrWhiteSpace(Action))
        {
            return false;
        }
        var Handled = false;
        foreach (var Part in Parts.Values.OfType<IUiActionHandler>())
        {
            if (Part.TryHandleAction(Action))
            {
                Handled = true;
                break;
            }
        }
        if (!Handled)
        {
            return false;
        }
        // A part changed its own per-use state; recompose so the presented document
        // shows it. Recomposing never mutates the shared definition.
        var Refresh = ShowScreen(_activeRequest);
        return Refresh.Success;
    }

    /// <summary>Renders a reusable template definition into a presentable document. The
    /// definition stays immutable; <paramref name="Request"/> supplies this use's values
    /// and parts, so two instances of one template remain independent.</summary>
    private static string ComposeTemplate(UiDocumentAsset Package, UiScreenRequest Request)
    {
        var Template = UiLayoutTemplate.Parse(Package.Rml);
        var Body = Template.Render(
            new UiLayoutContext(Request.Width, Request.Height, Package.Name),
            Request.Values ?? new JsonObject(), Request.Parts);
        var Builder = new System.Text.StringBuilder("<rml><head>");
        foreach (var Href in Package.StyleSheets)
        {
            Builder.Append("<link type=\"text/rcss\" href=\"")
                .Append(System.Net.WebUtility.HtmlEncode(Href))
                .Append("\" />");
        }
        Builder.Append("</head><body>").Append(Body).Append("</body></rml>");
        return Builder.ToString();
    }

    public void ClearScreen()
    {
        CheckThread();
        _activeRequest = null;
        if (_screenHost is null || _activeScreenId is null)
        {
            return;
        }
        _ = _screenHost.ClearDocument();
        _activeScreenId = null;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            throw new InvalidOperationException(
                "Runtime UI operations must run on the engine thread");
        }
    }

    public IDisposable RegisterViewModel<T>(string id, int version) where T : class
    {
        var normalized = (id ?? string.Empty).Trim();
        if (!Regex.IsMatch(normalized, "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
                RegexOptions.CultureInvariant))
            throw new ArgumentException(
                "UI ViewModel IDs use lower-case dot or dash separated tokens", nameof(id));
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version),
            "UI ViewModel versions are positive integers");
        var contract = new UiViewModelContract(normalized, version,
            typeof(T).FullName ?? typeof(T).Name, BuildSchema(typeof(T)));
        lock (_contracts)
        {
            if (!_contracts.TryAdd(contract.Key, contract))
                throw new InvalidOperationException(
                    $"UI ViewModel contract '{contract.Key}' is already registered");
        }
        return ModuleRegistrationScope.Track(new Registration(() =>
        {
            lock (_contracts)
            {
                if (_contracts.TryGetValue(contract.Key, out var current) &&
                    ReferenceEquals(current, contract))
                    _contracts.Remove(contract.Key);
            }
        }));
    }

    private static string BuildSchema(Type type)
    {
        var schema = SchemaFor(type, new HashSet<Type>());
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["title"] = type.Name;
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject SchemaFor(Type source, HashSet<Type> visiting)
    {
        var type = Nullable.GetUnderlyingType(source) ?? source;
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid))
            return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type.IsEnum)
            return new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(Enum.GetNames(type)
                    .Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()),
            };
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
            type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong))
            return new JsonObject { ["type"] = "integer" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new JsonObject { ["type"] = "number" };
        var enumerable = type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);
        if (enumerable)
        {
            var itemType = type.IsArray ? type.GetElementType() :
                type.GetInterfaces().Append(type)
                    .FirstOrDefault(candidate => candidate.IsGenericType &&
                        candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    ?.GetGenericArguments()[0];
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = itemType is null
                    ? new JsonObject() : SchemaFor(itemType, visiting),
            };
        }
        if (!visiting.Add(type))
            throw new InvalidOperationException(
                $"UI ViewModel '{type.FullName}' contains a recursive property graph");
        var properties = new JsonObject();
        foreach (var property in type.GetProperties(BindingFlags.Instance |
                     BindingFlags.Public).Where(value => value.GetMethod?.IsPublic == true &&
                     value.GetIndexParameters().Length == 0).OrderBy(value => value.Name,
                     StringComparer.Ordinal))
            properties[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] =
                SchemaFor(property.PropertyType, visiting);
        visiting.Remove(type);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
