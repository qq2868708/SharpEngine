using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace SharpEngine.Assets;

/// <summary>
/// Canonical source format for a project UI screen: a versioned JSON package whose
/// <c>rml</c> member holds the authoritative RML. The package is the asset; the RML is
/// not a second registered asset, so one screen has one identity and one atomic save
/// boundary.
///
/// <c>rml</c> may be either a complete <c>&lt;rml&gt;</c> document or a
/// <c>&lt;template&gt;</c> definition. A template is a reusable composition definition:
/// it is rendered per use with its own values and UI parts, so one definition can back
/// several independent instances. Optional <c>styleSheets</c> lists RCSS files resolved
/// relative to the package.
/// </summary>
public sealed class UiDocumentAsset
{
    public const int CurrentSchemaVersion = 1;
    public const string Importer = "SharpEngine.UiDocumentJson";
    public const string FileSuffix = ".sui";

    private UiDocumentAsset(string name, string rml,
        IReadOnlyList<string> styleSheets)
    {
        Name = RequireText(name, nameof(name));
        Rml = RequireText(rml, nameof(rml));
        StyleSheets = styleSheets;
        IsTemplate = ValidateRml(Rml);
    }

    public string Name { get; }

    /// <summary>Authoritative RML source, exactly as stored in the package.</summary>
    public string Rml { get; }

    /// <summary>RCSS files this definition links, relative to the package.</summary>
    public IReadOnlyList<string> StyleSheets { get; }

    /// <summary>True when the RML is a reusable <c>&lt;template&gt;</c> definition that
    /// must be rendered with values and parts before it can be presented.</summary>
    public bool IsTemplate { get; }

    public static UiDocumentAsset Create(string name, string rml,
        IReadOnlyList<string>? styleSheets = null) =>
        new(name, rml, styleSheets?.ToArray() ?? []);

    public string Serialize(bool indented = true) => ToJson().ToJsonString(
        new JsonSerializerOptions
        {
            WriteIndented = indented,
        });

    public JsonObject ToJson()
    {
        var Result = new JsonObject
        {
            ["$schema"] = "https://sharpengine.dev/schemas/ui-document-v1.json",
            ["schemaVersion"] = CurrentSchemaVersion,
            ["name"] = Name,
            ["rml"] = Rml,
        };
        if (StyleSheets.Count != 0)
        {
            Result["styleSheets"] = new JsonArray(
                StyleSheets.Select(Value => (JsonNode)Value).ToArray());
        }
        return Result;
    }

    public static UiDocumentAsset Parse(string json, string source = "<memory>")
    {
        JsonNode? Node;
        try
        {
            Node = JsonNode.Parse(json);
        }
        catch (JsonException Exception)
        {
            throw Error("SEUI_JSON_INVALID", source, Exception.Message);
        }
        if (Node is not JsonObject Root)
        {
            throw Error("SEUI_ROOT_INVALID", source, "The package root must be a JSON object");
        }
        RequireProperties(Root, source, "$schema", "schemaVersion", "name", "rml",
            "styleSheets");
        if (Root["schemaVersion"] is not JsonValue Version ||
            !Version.TryGetValue(out int ParsedVersion))
        {
            throw Error("SEUI_FIELD_INVALID", source, "'schemaVersion' requires an integer");
        }
        if (ParsedVersion != CurrentSchemaVersion)
        {
            throw Error("SEUI_SCHEMA_UNSUPPORTED", source,
                $"UI document schemaVersion must be {CurrentSchemaVersion}, got {ParsedVersion}");
        }
        var StyleSheets = new List<string>();
        if (Root["styleSheets"] is { } Sheets)
        {
            if (Sheets is not JsonArray Array)
            {
                throw Error("SEUI_FIELD_INVALID", source,
                    "'styleSheets' requires an array of relative RCSS paths");
            }
            foreach (var Entry in Array)
            {
                if (Entry is not JsonValue Value || !Value.TryGetValue(out string? Href) ||
                    string.IsNullOrWhiteSpace(Href))
                {
                    throw Error("SEUI_FIELD_INVALID", source,
                        "Every 'styleSheets' entry requires a non-empty path");
                }
                if (System.IO.Path.IsPathRooted(Href) ||
                    Href.Split('/', '\\').Any(Part => Part is ".."))
                {
                    throw Error("SEUI_FIELD_INVALID", source,
                        $"Stylesheet '{Href}' must stay inside the package folder");
                }
                StyleSheets.Add(Href);
            }
        }
        return new UiDocumentAsset(RequiredString(Root, "name", source),
            RequiredString(Root, "rml", source), StyleSheets);

    }

    public static UiDocumentAsset Load(string path)
    {
        var Full = System.IO.Path.GetFullPath(path);
        return Parse(File.ReadAllText(Full), Full);
    }

    /// <summary>Returns true when the RML is a reusable template definition.</summary>
    private static bool ValidateRml(string Rml)
    {
        XDocument Document;
        try
        {
            using var Reader = XmlReader.Create(new StringReader(Rml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            Document = XDocument.Load(Reader);
        }
        catch (XmlException Exception)
        {
            throw Error("SEUI_RML_INVALID", "rml", Exception.Message);
        }
        var Root = Document.Root;
        if (Root is null)
        {
            throw Error("SEUI_RML_INVALID", "rml", "The RML root element is missing");
        }
        if (Root.Name.LocalName == "template")
        {
            return true;
        }
        if (Root.Name.LocalName != "rml")
        {
            throw Error("SEUI_RML_INVALID", "rml",
                "The RML root element must be <rml> or <template>");
        }
        if (Root.Elements().Count(Element => Element.Name.LocalName == "body") != 1)
        {
            throw Error("SEUI_RML_INVALID", "rml", "The RML document requires exactly one <body>");
        }
        return false;
    }

    private static void RequireProperties(JsonObject Value, string Source,
        params string[] Allowed)
    {
        var Set = Allowed.ToHashSet(StringComparer.Ordinal);
        var Seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Pair in Value)
        {
            if (!Seen.Add(Pair.Key))
            {
                throw Error("SEUI_FIELD_DUPLICATE", Source, Pair.Key);
            }
            if (!Set.Contains(Pair.Key))
            {
                throw Error("SEUI_FIELD_UNKNOWN", Source, $"Unknown field '{Pair.Key}'");
            }
        }
    }

    private static string RequiredString(JsonObject Value, string Name, string Source)
    {
        if (Value[Name] is JsonValue Node && Node.TryGetValue(out string? Text) &&
            !string.IsNullOrWhiteSpace(Text))
        {
            return Text;
        }
        throw Error("SEUI_FIELD_INVALID", Source,
            $"'{Name}' requires a non-empty string");
    }

    private static string RequireText(string Value, string Name) =>
        string.IsNullOrWhiteSpace(Value) ? throw new InvalidDataException(
            $"SEUI_FIELD_INVALID [{Name}] requires non-empty text") : Value;

    private static InvalidDataException Error(string Code, string Source, string Message) =>
        new($"{Code} [{Source}] {Message}");
}
