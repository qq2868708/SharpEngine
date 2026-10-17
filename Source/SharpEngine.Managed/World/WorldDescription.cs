using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Xml.Linq;
using SharpEngine.UI;

namespace SharpEngine.World;

/// <summary>A small renderer-neutral world description used by the editor viewport.
/// The format is deliberately data-only so it can later be consumed by Vulkan or a
/// runtime scene loader without changing the C# game API.</summary>
public enum WorldEntityKind
{
    Mesh,
    Camera,
    Light,
    Empty,
}

public sealed record WorldEntityDescription(
    string Id, string Name, string Type, string Parent,
    string Asset, string Material, WorldVector3 Position,
    WorldVector3 Rotation, WorldVector3 Scale, WorldVector3 WorldPosition,
    float[] WorldMatrix, UiColor Color, float Intensity,
    float FieldOfView, float NearPlane, float FarPlane);

public sealed record WorldSceneDescription(
    int SchemaVersion, string Name, string ActiveCamera,
    IReadOnlyList<WorldEntityDescription> Entities);

public readonly record struct WorldVector3(float X, float Y, float Z)
{
    public static WorldVector3 Zero => new(0, 0, 0);
    public static WorldVector3 One => new(1, 1, 1);

    public static WorldVector3 Parse(string? value, WorldVector3 fallback)
    {
        if (value is null) return fallback;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) throw new InvalidDataException(
            $"World vector must contain three finite numbers, got '{value}'");
        if (float.TryParse(parts[0], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var x) &&
            float.TryParse(parts[1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var y) &&
            float.TryParse(parts[2], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var z) &&
            float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z))
        {
            return new WorldVector3(x, y, z);
        }
        throw new InvalidDataException(
            $"World vector must contain three finite numbers, got '{value}'");
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X:R},{Y:R},{Z:R}");
}

public sealed class WorldEntity
{
    private WorldVector3 _position = WorldVector3.Zero;
    private WorldVector3 _rotation = WorldVector3.Zero;
    private WorldVector3 _scale = WorldVector3.One;
    private float _intensity = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public WorldEntityKind Kind { get; init; } = WorldEntityKind.Empty;
    /// <summary>Optional parent entity id. Position/rotation/scale are local to it.</summary>
    public string ParentId { get; internal set; } = string.Empty;
    public string Asset { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public WorldVector3 Position
    {
        get => _position;
        set { ValidateVector(value); _position = value; }
    }
    public WorldVector3 Rotation
    {
        get => _rotation;
        set { ValidateVector(value); _rotation = value; }
    }
    public WorldVector3 Scale
    {
        get => _scale;
        set
        {
            ValidateVector(value);
            if (value.X <= 0 || value.Y <= 0 || value.Z <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Scale must be positive");
            _scale = value;
        }
    }
    public UiColor Color { get; set; } = new(110, 160, 225);
    public float Intensity
    {
        get => _intensity;
        set
        {
            if (!float.IsFinite(value) || value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Intensity must be finite and nonnegative");
            _intensity = value;
        }
    }
    public float FieldOfView { get; init; } = 60;
    public float NearPlane { get; init; } = 0.1f;
    public float FarPlane { get; init; } = 1000;

    private static void ValidateVector(WorldVector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(nameof(value), "Transform values must be finite");
    }
}

public sealed class WorldDocument
{
    public const int CurrentSchemaVersion = 2;

    private readonly List<WorldEntity> _entities;
    private readonly ReadOnlyCollection<WorldEntity> _readOnlyEntities;
    private readonly XDocument _sourceDocument;

    private WorldDocument(string sourcePath, string name, string activeCamera,
        List<WorldEntity> entities, XDocument sourceDocument)
    {
        SourcePath = sourcePath;
        Name = name;
        ActiveCameraId = activeCamera;
        _entities = entities;
        _readOnlyEntities = entities.AsReadOnly();
        _sourceDocument = new XDocument(sourceDocument);
    }

    public string SourcePath { get; private set; }
    public string Name { get; }
    public string ActiveCameraId { get; }
    public WorldEntity? ActiveCamera => Entities.FirstOrDefault(e =>
        e.Kind == WorldEntityKind.Camera && (ActiveCameraId.Length == 0 ||
            string.Equals(e.Id, ActiveCameraId, StringComparison.OrdinalIgnoreCase)));
    public IReadOnlyList<WorldEntity> Entities => _readOnlyEntities;
    public int MeshCount => Entities.Count(e => e.Kind == WorldEntityKind.Mesh);

    internal void SetSourcePath(string path) => SourcePath = Path.GetFullPath(path);

    internal void SetEntityOrder(IReadOnlyList<WorldEntity> entities)
    {
        if (entities.Count != _entities.Count ||
            entities.Select(entity => entity.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _entities.Count ||
            entities.Any(entity => !_entities.Contains(entity)))
            throw new InvalidOperationException("Entity order must contain every world entity exactly once");
        _entities.Clear();
        _entities.AddRange(entities);
    }

    /// <summary>Returns the row-vector local-to-world matrix, including every parent.</summary>
    public Matrix4x4 WorldMatrix(WorldEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return WorldMatrix(entity, visiting);
    }

    private Matrix4x4 WorldMatrix(WorldEntity entity, HashSet<string> visiting)
    {
        if (!visiting.Add(entity.Id))
            throw new InvalidDataException($"World parent cycle includes '{entity.Id}'");
        var local = Matrix4x4.CreateScale(entity.Scale.X, entity.Scale.Y, entity.Scale.Z) *
            Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromYawPitchRoll(
                entity.Rotation.Y * MathF.PI / 180,
                entity.Rotation.X * MathF.PI / 180,
                entity.Rotation.Z * MathF.PI / 180)) *
            Matrix4x4.CreateTranslation(entity.Position.X, entity.Position.Y, entity.Position.Z);
        if (entity.ParentId.Length == 0)
        {
            visiting.Remove(entity.Id);
            return local;
        }
        var parent = Entities.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, entity.ParentId, StringComparison.OrdinalIgnoreCase));
        if (parent is null)
            throw new InvalidDataException($"World entity '{entity.Id}' references missing parent '{entity.ParentId}'");
        var result = local * WorldMatrix(parent, visiting);
        visiting.Remove(entity.Id);
        return result;
    }

    public static WorldDocument Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(
            "World document was not found", path);
        return Parse(XDocument.Load(path), path);
    }

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var document = ToXml();
            document.Save(temporaryPath);
            _ = Load(temporaryPath);
            if (File.Exists(fullPath)) File.Move(temporaryPath, fullPath, true);
            else File.Move(temporaryPath, fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public XDocument ToXml()
    {
        var document = new XDocument(_sourceDocument);
        var root = document.Root ?? throw new InvalidDataException("World document has no root element");
        root.SetAttributeValue("version", CurrentSchemaVersion);
        root.SetAttributeValue("name", Name);
        root.SetAttributeValue("activeCamera", ActiveCameraId);

        var originalNodes = root.Nodes().ToList();
        var firstEntityIndex = originalNodes.FindIndex(IsEntityNode);
        if (firstEntityIndex < 0) firstEntityIndex = originalNodes.Count;
        var insertionIndex = originalNodes.Take(firstEntityIndex).Count(node => !IsEntityNode(node));
        var templates = root.Elements("entity")
            .Where(element => element.Attribute("id") is not null)
            .GroupBy(element => (string)element.Attribute("id")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new XElement(group.First()),
                StringComparer.OrdinalIgnoreCase);
        var preservedNodes = originalNodes.Where(node => !IsEntityNode(node)).ToList();
        var serializedEntities = Entities.Select(entity =>
        {
            var element = templates.TryGetValue(entity.Id, out var template)
                ? new XElement(template) : new XElement("entity");
            WriteEntity(element, entity);
            return (XNode)element;
        }).ToList();
        preservedNodes.InsertRange(insertionIndex, serializedEntities);
        root.ReplaceNodes(preservedNodes);
        return document;
    }

    private static bool IsEntityNode(XNode node) =>
        node is XElement element && element.Name == "entity";

    private static void WriteEntity(XElement element, WorldEntity entity)
    {
        element.Name = "entity";
        element.SetAttributeValue("id", entity.Id);
        element.SetAttributeValue("name", entity.Name);
        element.SetAttributeValue("type", KindToString(entity.Kind));
        element.SetAttributeValue("parent", entity.ParentId.Length == 0 ? null : entity.ParentId);
        element.SetAttributeValue("asset", entity.Asset);
        element.SetAttributeValue("material", entity.Material);
        var transform = element.Element("transform");
        var transformOwner = transform ?? element;
        transformOwner.SetAttributeValue("position", entity.Position);
        transformOwner.SetAttributeValue("rotation", entity.Rotation);
        transformOwner.SetAttributeValue("scale", entity.Scale);
        if (transform is not null)
        {
            element.SetAttributeValue("position", null);
            element.SetAttributeValue("rotation", null);
            element.SetAttributeValue("scale", null);
        }
        var color = entity.Color.A == 255
            ? $"#{entity.Color.R:X2}{entity.Color.G:X2}{entity.Color.B:X2}"
            : $"#{entity.Color.R:X2}{entity.Color.G:X2}{entity.Color.B:X2}{entity.Color.A:X2}";
        element.SetAttributeValue("color", color);
        element.SetAttributeValue("intensity", entity.Intensity.ToString("R", CultureInfo.InvariantCulture));
        element.SetAttributeValue("fieldOfView", entity.FieldOfView.ToString("R", CultureInfo.InvariantCulture));
        element.SetAttributeValue("nearPlane", entity.NearPlane.ToString("R", CultureInfo.InvariantCulture));
        element.SetAttributeValue("farPlane", entity.FarPlane.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Complete AI-facing scene description, including hierarchy and resolved transforms.</summary>
    public WorldSceneDescription Describe()
    {
        var descriptions = Entities.Select(entity =>
        {
            var matrix = WorldMatrix(entity);
            var worldPosition = new WorldVector3(matrix.M41, matrix.M42, matrix.M43);
            return new WorldEntityDescription(entity.Id, entity.Name, entity.Kind.ToString(),
                entity.ParentId, entity.Asset, entity.Material, entity.Position,
                entity.Rotation, entity.Scale, worldPosition, new[]
                {
                    matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                    matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                    matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                    matrix.M41, matrix.M42, matrix.M43, matrix.M44,
                }, entity.Color, entity.Intensity, entity.FieldOfView,
                entity.NearPlane, entity.FarPlane);
        }).ToArray();
        return new WorldSceneDescription(CurrentSchemaVersion, Name, ActiveCameraId, descriptions);
    }

    public string ToJson(JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(Describe(), options ?? new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    public static WorldDocument Parse(string xml, string sourceName = "<memory>") =>
        Parse(XDocument.Parse(xml), sourceName);

    private static WorldDocument Parse(XDocument document, string sourcePath)
    {
        var root = document.Root ?? throw new InvalidDataException(
            "World document has no root element");
        if (!string.Equals(root.Name.LocalName, "world",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("World document root must be <world>");
        }
        var version = (string?)root.Attribute("version");
        if (version != CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException($"Unsupported world format version '{version}'");

        var entities = new List<WorldEntity>();
        foreach (var element in root.Elements("entity"))
        {
            var id = (string?)element.Attribute("id");
            if (string.IsNullOrWhiteSpace(id) || id.Any(char.IsWhiteSpace)) throw new InvalidDataException(
                "World entity requires an id");
            var name = (string?)element.Attribute("name") ?? id;
            var kindText = ((string?)element.Attribute("type") ?? "empty").Trim();
            var kind = kindText.ToLowerInvariant() switch
            {
                "mesh" or "model" or "cube" => WorldEntityKind.Mesh,
                "camera" => WorldEntityKind.Camera,
                "light" => WorldEntityKind.Light,
                "empty" => WorldEntityKind.Empty,
                _ => throw new InvalidDataException($"Unknown world entity type '{kindText}'"),
            };
            var transform = element.Element("transform");
            var position = WorldVector3.Parse((string?)transform?.Attribute("position") ??
                (string?)element.Attribute("position"), WorldVector3.Zero);
            var rotation = WorldVector3.Parse((string?)transform?.Attribute("rotation") ??
                (string?)element.Attribute("rotation"), WorldVector3.Zero);
            var scale = WorldVector3.Parse((string?)transform?.Attribute("scale") ??
                (string?)element.Attribute("scale"), WorldVector3.One);
            if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0)
                throw new InvalidDataException($"World entity '{id}' scale must be positive");
            var colorText = (string?)element.Attribute("color");
            if (kind == WorldEntityKind.Light && string.IsNullOrWhiteSpace(colorText))
                throw new InvalidDataException($"World light '{id}' requires an explicit color");
            var color = ReadColor(colorText,
                kind == WorldEntityKind.Light ? new UiColor(255, 210, 86) :
                kind == WorldEntityKind.Camera ? new UiColor(109, 214, 164) :
                new UiColor(92, 145, 220));
            var intensity = ReadFloat(element, "intensity", 1);
            var fieldOfView = ReadFloat(element, "fieldOfView", 60);
            var nearPlane = ReadFloat(element, "nearPlane", 0.1f);
            var farPlane = ReadFloat(element, "farPlane", 1000);
            if (intensity < 0 || fieldOfView < 1 || fieldOfView > 179 ||
                nearPlane <= 0 || farPlane <= nearPlane)
                throw new InvalidDataException($"World entity '{id}' has invalid camera/light values");
            entities.Add(new WorldEntity
            {
                Id = id,
                Name = name,
                Kind = kind,
                ParentId = ((string?)element.Attribute("parent") ?? string.Empty).Trim(),
                Asset = (string?)element.Attribute("asset") ?? string.Empty,
                Material = (string?)element.Attribute("material") ?? string.Empty,
                Position = position,
                Rotation = rotation,
                Scale = scale,
                Color = color,
                Intensity = intensity,
                FieldOfView = fieldOfView,
                NearPlane = nearPlane,
                FarPlane = farPlane,
            });
        }

        var duplicate = entities.GroupBy(e => e.Id,
            StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException(
            $"World contains duplicate entity id '{duplicate.Key}'");
        foreach (var entity in entities)
        {
            if (entity.ParentId.Length == 0) continue;
            if (string.Equals(entity.ParentId, entity.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"World entity '{entity.Id}' cannot parent itself");
            if (!entities.Any(candidate => string.Equals(candidate.Id, entity.ParentId,
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"World entity '{entity.Id}' references missing parent '{entity.ParentId}'");
        }
        foreach (var entity in entities)
        {
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = entity;
            while (current.ParentId.Length > 0)
            {
                if (!visiting.Add(current.Id))
                    throw new InvalidDataException($"World parent cycle includes '{current.Id}'");
                current = entities.First(candidate => string.Equals(candidate.Id, current.ParentId,
                    StringComparison.OrdinalIgnoreCase));
            }
        }
        var activeCamera = (string?)root.Attribute("activeCamera") ?? string.Empty;
        if (activeCamera.Length > 0 && !entities.Any(e =>
            e.Kind == WorldEntityKind.Camera && string.Equals(e.Id, activeCamera,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Active camera '{activeCamera}' does not exist");
        return new WorldDocument(sourcePath,
            (string?)root.Attribute("name") ?? Path.GetFileNameWithoutExtension(sourcePath),
            activeCamera, entities, document);
    }

    private static UiColor ReadColor(string? raw, UiColor fallback)
    {
        if (raw is null) return fallback;
        var text = raw.Trim();
        var named = text.Equals("white", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("black", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("transparent", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("gray", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("grey", StringComparison.OrdinalIgnoreCase);
        var hex = text.StartsWith('#') && (text.Length == 7 || text.Length == 9) &&
            uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        if (!named && !hex)
            throw new InvalidDataException($"World color '{raw}' is invalid");
        return UiColor.Parse(text, fallback);
    }

    private static float ReadFloat(XElement element, string attribute, float fallback)
    {
        var raw = (string?)element.Attribute(attribute);
        if (raw is null) return fallback;
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
            out var value) || !float.IsFinite(value))
            throw new InvalidDataException($"'{attribute}' requires a finite number");
        return value;
    }

    private static string KindToString(WorldEntityKind kind) => kind switch
    {
        WorldEntityKind.Mesh => "mesh",
        WorldEntityKind.Camera => "camera",
        WorldEntityKind.Light => "light",
        _ => "empty",
    };

}

