using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SharpEngine.UI;

/// <summary>Controls understood by the first XML UI renderer.</summary>
public enum UiNodeKind
{
    Root,
    Panel,
    Row,
    Column,
    Text,
    Button,
    Input,
    Image,
    Spacer,
}

public readonly record struct UiSize(float Width, float Height);

public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

public readonly record struct UiColor(byte R, byte G, byte B, byte A = 255)
{
    public static UiColor White => new(255, 255, 255);
    public static UiColor Transparent => new(0, 0, 0, 0);
    public static UiColor Parse(string? value, UiColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var text = value.Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
        {
            text = text[1..];
            if (text.Length is 6 or 8 &&
                uint.TryParse(text, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var packed))
            {
                var r = (byte)(packed >> (text.Length == 8 ? 24 : 16));
                var g = (byte)(packed >> (text.Length == 8 ? 16 : 8));
                var b = (byte)(packed >> (text.Length == 8 ? 8 : 0));
                var a = text.Length == 8 ? (byte)packed : (byte)255;
                return new UiColor(r, g, b, a);
            }
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "white" => White,
            "black" => new UiColor(0, 0, 0),
            "transparent" => Transparent,
            "gray" or "grey" => new UiColor(128, 128, 128),
            _ => fallback,
        };
    }
}

public enum UiTextAlignment : byte
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>A retained UI node parsed from the small XML UI vocabulary.</summary>
public sealed class UiNode
{
    private readonly Dictionary<string, string> _attributes;
    private readonly List<UiNode> _children = new();

    internal UiNode(UiNodeKind kind, string id,
        IReadOnlyDictionary<string, string> attributes)
    {
        Kind = kind;
        Id = id;
        _attributes = new Dictionary<string, string>(attributes,
            StringComparer.OrdinalIgnoreCase);
    }

    public UiNodeKind Kind { get; }
    public string Id { get; }
    public UiNode? Parent { get; internal set; }
    public UiRect Bounds { get; internal set; }
    public bool Visible { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public IReadOnlyList<UiNode> Children => _children;
    public IReadOnlyDictionary<string, string> Attributes =>
        new ReadOnlyDictionary<string, string>(_attributes);

    public string? Text => GetAttribute("text") ?? GetAttribute("value");
    public string? BindingPath => GetAttribute("bind");
    public string? ActionName => GetAttribute("action");
    public bool IsInteractive => Kind is UiNodeKind.Button or UiNodeKind.Input;

    public string? GetAttribute(string name) =>
        _attributes.TryGetValue(name, out var value) ? value : null;

    public string GetAttribute(string name, string fallback) =>
        GetAttribute(name) ?? fallback;

    public bool GetBool(string name, bool fallback = false) =>
        bool.TryParse(GetAttribute(name), out var value) ? value : fallback;

    public float GetFloat(string name, float fallback = 0) =>
        float.TryParse(GetAttribute(name), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public UiColor GetColor(string name, UiColor fallback) =>
        UiColor.Parse(GetAttribute(name), fallback);

    public float FontSize => Math.Max(1, GetFloat("fontSize", 14));

    public float BorderWidth => Math.Max(0, GetFloat("borderWidth", 0));

    public UiTextAlignment Alignment => GetAttribute("alignment", "left")
        .Trim().ToLowerInvariant() switch
    {
        "center" or "middle" => UiTextAlignment.Center,
        "right" => UiTextAlignment.Right,
        _ => UiTextAlignment.Left,
    };

    public UiNode? Find(string id)
    {
        if (string.Equals(Id, id, StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        foreach (var child in _children)
        {
            var result = child.Find(id);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    public IEnumerable<UiNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in _children)
        {
            foreach (var descendant in child.DescendantsAndSelf())
            {
                yield return descendant;
            }
        }
    }

    internal void AddChild(UiNode child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    internal void SetAttribute(string name, string value) => _attributes[name] = value;
}

/// <summary>Parsed XML document and the retained tree used by engine UI.</summary>
public sealed class UiDocumentModel
{
    private static readonly HashSet<string> SupportedElements = new(
        new[] { "panel", "row", "column", "text", "button", "input", "image", "spacer" },
        StringComparer.OrdinalIgnoreCase);

    private UiDocumentModel(string sourcePath, string version, UiNode root)
    {
        SourcePath = sourcePath;
        Version = version;
        Root = root;
    }

    public string SourcePath { get; }
    public string Version { get; }
    public UiNode Root { get; }
    public int NodeCount => Root.DescendantsAndSelf().Count(n => n.Kind != UiNodeKind.Root);
    public UiNode? Find(string id) => Root.Find(id);

    public static UiDocumentModel Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("UI document was not found", path);
        }

        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
        return Parse(document, path);
    }

    public static UiDocumentModel Parse(string xml, string sourceName = "<memory>")
    {
        using var reader = XmlReader.Create(new StringReader(xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        return Parse(document, sourceName);
    }

    private static UiDocumentModel Parse(XDocument document, string sourcePath)
    {
        var rootElement = document.Root
            ?? throw new InvalidDataException("UI document has no root element");
        if (!string.Equals(rootElement.Name.LocalName, "ui",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"UI document root must be <ui>, got <{rootElement.Name.LocalName}>");
        }

        var root = new UiNode(UiNodeKind.Root, "__root",
            new Dictionary<string, string>());
        foreach (var element in rootElement.Elements())
        {
            root.AddChild(ParseNode(element));
        }

        if (root.Children.Count == 0)
        {
            throw new InvalidDataException("UI document must contain at least one control");
        }

        var duplicate = root.DescendantsAndSelf()
            .Where(node => node.Kind != UiNodeKind.Root)
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"UI document contains duplicate id '{duplicate.Key}'");
        }

        return new UiDocumentModel(sourcePath,
            (string?)rootElement.Attribute("version") ?? "1", root);
    }

    private static UiNode ParseNode(XElement element)
    {
        var elementName = element.Name.LocalName;
        if (!SupportedElements.Contains(elementName))
        {
            throw new InvalidDataException(
                $"Unsupported UI element <{elementName}> at {Location(element)}");
        }

        var kind = Enum.Parse<UiNodeKind>(elementName, ignoreCase: true);
        var id = (string?)element.Attribute("id") ??
            throw new InvalidDataException(
                $"UI element <{elementName}> requires an id at {Location(element)}");

        if (id.Length == 0 || id.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException(
                $"UI element id must be a non-empty token at {Location(element)}");
        }

        var attributes = element.Attributes().ToDictionary(
            attribute => attribute.Name.LocalName,
            attribute => attribute.Value,
            StringComparer.OrdinalIgnoreCase);
        var node = new UiNode(kind, id, attributes);
        node.Visible = node.GetBool("visible", true);
        node.Enabled = node.GetBool("enabled", true);
        foreach (var child in element.Elements())
        {
            node.AddChild(ParseNode(child));
        }

        return node;
    }

    private static string Location(XElement element)
    {
        var lineInfo = (IXmlLineInfo)element;
        return lineInfo.HasLineInfo()
            ? $"{lineInfo.LineNumber}:{lineInfo.LinePosition}"
            : "unknown location";
    }
}

/// <summary>Mutable values exposed to XML bind attributes (for example Game.Fps).</summary>
public sealed class UiBindingContext
{
    private readonly Dictionary<string, object?> _values =
        new(StringComparer.OrdinalIgnoreCase);

    public void Set(string path, object? value) => _values[path] = value;

    public bool TryGet(string? path, out object? value)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            value = null;
            return false;
        }

        return _values.TryGetValue(path, out value);
    }

    public string ResolveText(UiNode node)
    {
        if (TryGet(node.BindingPath, out var value))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return node.Text ?? string.Empty;
    }
}

public enum UiDrawCommandKind
{
    Panel,
    Text,
    Button,
    Input,
    Image,
}

/// <summary>A renderer-neutral command that can later cross the native boundary.</summary>
public readonly record struct UiDrawCommand(
    UiDrawCommandKind Kind,
    string NodeId,
    UiRect Bounds,
    string Text,
    UiColor Color,
    bool Enabled,
    UiColor Background,
    UiColor Border,
    float BorderWidth,
    float FontSize,
    UiTextAlignment Alignment);

public sealed class UiDrawList
{
    private readonly List<UiDrawCommand> _commands = new();
    public IReadOnlyList<UiDrawCommand> Commands => _commands;
    internal void Add(UiDrawCommand command) => _commands.Add(command);
    public void Clear() => _commands.Clear();
}

/// <summary>Small retained-mode layout and command builder for the native renderer.</summary>
public static class UiLayout
{
    public static UiDrawList BuildDrawList(UiDocumentModel document,
        UiSize viewport, UiBindingContext? bindings = null)
    {
        var list = new UiDrawList();
        Layout(document.Root, new UiRect(0, 0, viewport.Width, viewport.Height));
        foreach (var node in document.Root.DescendantsAndSelf())
        {
            if (node.Kind == UiNodeKind.Root || node.Kind == UiNodeKind.Spacer ||
                !node.Visible)
            {
                continue;
            }

            var defaultBackground = node.Kind switch
            {
                UiNodeKind.Panel or UiNodeKind.Row or UiNodeKind.Column =>
                    new UiColor(35, 42, 54),
                UiNodeKind.Button => new UiColor(52, 105, 170),
                _ => UiColor.Transparent,
            };
            var defaultColor = node.Kind switch
            {
                UiNodeKind.Text => new UiColor(232, 237, 245),
                UiNodeKind.Button => UiColor.White,
                _ => new UiColor(232, 237, 245),
            };
            // XML `color` is the visual fill for containers and the text color
            // for text controls. `background` remains available when a panel
            // needs a separate fill and text color.
            var background = node.GetColor("background",
                node.Kind is UiNodeKind.Panel or UiNodeKind.Row or UiNodeKind.Column
                    ? node.GetColor("color", defaultBackground)
                    : defaultBackground);
            var color = node.GetColor("color", defaultColor);
            var border = node.GetColor("border", UiColor.Transparent);
            var text = bindings?.ResolveText(node) ?? node.Text ?? string.Empty;
            var commandKind = node.Kind switch
            {
                UiNodeKind.Text => UiDrawCommandKind.Text,
                UiNodeKind.Button => UiDrawCommandKind.Button,
                UiNodeKind.Input => UiDrawCommandKind.Input,
                UiNodeKind.Image => UiDrawCommandKind.Image,
                _ => UiDrawCommandKind.Panel,
            };
            list.Add(new UiDrawCommand(commandKind, node.Id, node.Bounds,
                text, color, node.Enabled, background, border,
                node.BorderWidth, node.FontSize, node.Alignment));
        }

        return list;
    }

    private static void Layout(UiNode node, UiRect bounds)
    {
        node.Bounds = bounds;
        if (node.Children.Count == 0)
        {
            return;
        }

        var padding = Math.Max(0, node.GetFloat("padding"));
        var content = new UiRect(bounds.X + padding, bounds.Y + padding,
            Math.Max(0, bounds.Width - padding * 2),
            Math.Max(0, bounds.Height - padding * 2));
        var horizontal = node.Kind == UiNodeKind.Row ||
            string.Equals(node.GetAttribute("layout"), "row",
                StringComparison.OrdinalIgnoreCase);
        var gap = Math.Max(0, node.GetFloat("gap"));
        var mainAttribute = horizontal ? "width" : "height";
        var crossAttribute = horizontal ? "height" : "width";
        var flexible = node.Children.Count(child =>
            IsFlexible(child, mainAttribute));
        var fixedSize = node.Children.Sum(child =>
            TryGetDimension(child, horizontal ? content.Width : content.Height,
                out var size, mainAttribute)
                ? size
                : GetPreferredSize(child, horizontal))
            + Math.Max(0, node.Children.Count - 1) * gap;
        var flexibleSize = flexible > 0
            ? Math.Max(0, (horizontal ? content.Width : content.Height) - fixedSize) / flexible
            : 0;
        var cursor = horizontal ? content.X : content.Y;
        foreach (var child in node.Children)
        {
            var mainSize = TryGetDimension(child,
                horizontal ? content.Width : content.Height, out var explicitSize,
                mainAttribute)
                ? explicitSize
                : IsFlexible(child, mainAttribute)
                    ? flexibleSize
                    : GetPreferredSize(child, horizontal);
            var crossSize = horizontal ? content.Height : content.Width;
            if (TryGetDimension(child, crossSize,
                out var explicitCross, crossAttribute))
            {
                crossSize = explicitCross;
            }

            var childBounds = horizontal
                ? new UiRect(cursor, content.Y, mainSize, crossSize)
                : new UiRect(content.X, cursor, crossSize, mainSize);
            Layout(child, childBounds);
            cursor += mainSize + gap;
        }
    }

    private static bool IsFlexible(UiNode node, string axis)
    {
        var raw = node.GetAttribute(axis);
        if (string.Equals(raw, "fill", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(raw) &&
            node.Kind is UiNodeKind.Panel or UiNodeKind.Row or
                UiNodeKind.Column or UiNodeKind.Spacer;
    }

    private static float GetPreferredSize(UiNode node, bool horizontal)
    {
        var axis = horizontal ? "width" : "height";
        var explicitSize = node.GetFloat(axis, -1);
        if (explicitSize >= 0)
        {
            return explicitSize;
        }

        if (horizontal)
        {
            return node.Kind switch
            {
                UiNodeKind.Text => Math.Max(48, node.GetFloat("fontSize", 13) *
                    Math.Max(4, (node.Text?.Length ?? 4) * 0.65f) + 16),
                UiNodeKind.Button => Math.Max(72, node.GetFloat("fontSize", 13) *
                    Math.Max(5, (node.Text?.Length ?? 5) * 0.72f) + 24),
                _ => 0,
            };
        }

        return node.Kind switch
        {
            UiNodeKind.Text => Math.Max(20, node.GetFloat("fontSize", 13) + 8),
            UiNodeKind.Button => Math.Max(26, node.GetFloat("fontSize", 13) + 12),
            UiNodeKind.Input => 28,
            _ => 0,
        };
    }

    private static bool TryGetDimension(UiNode node, float parentSize,
        out float value, string? axis = null)
    {
        value = 0;
        var attribute = axis ?? "width";
        var raw = node.GetAttribute(attribute);
        if (string.IsNullOrWhiteSpace(raw) ||
            string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "fill", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        raw = raw.Trim();
        if (raw.EndsWith('%') && float.TryParse(raw[..^1],
            NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            value = parentSize * Math.Clamp(percent, 0, 100) / 100;
            return true;
        }

        return float.TryParse(raw, NumberStyles.Float,
            CultureInfo.InvariantCulture, out value) && value >= 0;
    }
}

/// <summary>Action dispatch and hit testing shared by game and editor UI.</summary>
public sealed class UiInteraction
{
    private readonly Dictionary<string, Action<UiNode>> _actions =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string actionName, Action<UiNode> action)
    {
        if (string.IsNullOrWhiteSpace(actionName))
        {
            throw new ArgumentException("Action name is required", nameof(actionName));
        }

        _actions[actionName] = action;
    }

    public bool Activate(UiDocumentModel document, string nodeId)
    {
        var node = document.Find(nodeId);
        if (node is null || !node.Visible || !node.Enabled || !node.IsInteractive ||
            string.IsNullOrWhiteSpace(node.ActionName) ||
            !_actions.TryGetValue(node.ActionName, out var action))
        {
            return false;
        }

        action(node);
        return true;
    }

    public UiNode? HitTest(UiDocumentModel document, float x, float y)
    {
        return document.Root.DescendantsAndSelf()
            .Where(node => node.Visible && node.Enabled && node.IsInteractive)
            .Reverse()
            .FirstOrDefault(node => x >= node.Bounds.X && x <= node.Bounds.Right &&
                y >= node.Bounds.Y && y <= node.Bounds.Bottom);
    }
}
