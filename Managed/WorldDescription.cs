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
    string Name, string ActiveCamera, IReadOnlyList<WorldEntityDescription> Entities);

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
    public string ParentId { get; init; } = string.Empty;
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
    private WorldDocument(string sourcePath, string name, string activeCamera,
        IReadOnlyList<WorldEntity> entities)
    {
        SourcePath = sourcePath;
        Name = name;
        ActiveCameraId = activeCamera;
        Entities = entities;
    }

    public string SourcePath { get; }
    public string Name { get; }
    public string ActiveCameraId { get; }
    public WorldEntity? ActiveCamera => Entities.FirstOrDefault(e =>
        e.Kind == WorldEntityKind.Camera && (ActiveCameraId.Length == 0 ||
            string.Equals(e.Id, ActiveCameraId, StringComparison.OrdinalIgnoreCase)));
    public IReadOnlyList<WorldEntity> Entities { get; }
    public int MeshCount => Entities.Count(e => e.Kind == WorldEntityKind.Mesh);

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

    public static WorldDocument LoadOrDefault(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return Load(candidate);
        }

        return Parse(XDocument.Parse(BuiltInDocument), "built-in:Main.world.xml");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        ToXml().Save(path);
    }

    public XDocument ToXml()
    {
        var root = new XElement("world", new XAttribute("version", "1"),
            new XAttribute("name", Name),
            new XAttribute("activeCamera", ActiveCameraId));
        foreach (var entity in Entities)
        {
            root.Add(new XElement("entity",
                new XAttribute("id", entity.Id),
                new XAttribute("name", entity.Name),
                new XAttribute("type", KindToString(entity.Kind)),
                entity.ParentId.Length == 0 ? null : new XAttribute("parent", entity.ParentId),
                new XAttribute("asset", entity.Asset),
                new XAttribute("material", entity.Material),
                new XAttribute("position", entity.Position),
                new XAttribute("rotation", entity.Rotation),
                new XAttribute("scale", entity.Scale),
                new XAttribute("color", $"#{entity.Color.R:X2}{entity.Color.G:X2}{entity.Color.B:X2}"),
                new XAttribute("intensity", entity.Intensity.ToString("R",
                    CultureInfo.InvariantCulture)),
                new XAttribute("fieldOfView", entity.FieldOfView.ToString("R",
                    CultureInfo.InvariantCulture)),
                new XAttribute("nearPlane", entity.NearPlane.ToString("R",
                    CultureInfo.InvariantCulture)),
                new XAttribute("farPlane", entity.FarPlane.ToString("R",
                    CultureInfo.InvariantCulture))));
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
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
        return new WorldSceneDescription(Name, ActiveCameraId, descriptions);
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
        if ((string?)root.Attribute("version") is { } version && version != "1")
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
            var color = UiColor.Parse((string?)element.Attribute("color"),
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
            activeCamera, entities);
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

    private const string BuiltInDocument = """
        <?xml version="1.0" encoding="utf-8"?>
        <world version="1" name="Main Scene" activeCamera="mainCamera">
          <entity id="ground" name="Ground" type="mesh" asset="engine://meshes/plane" position="0,0,0" scale="10,1,10" color="#38506F" />
          <entity id="player" name="Player" type="mesh" asset="engine://meshes/cube" position="-2,0.75,0" rotation="0,25,0" scale="1.5,1.5,1.5" color="#4C91E8" />
          <entity id="crate" name="Crate" type="mesh" asset="engine://meshes/cube" position="2,0.5,1" rotation="0,-15,0" scale="1,1,1" color="#B56D46" />
          <entity id="mainCamera" name="Main Camera" type="camera" position="8,7,11" rotation="-28,36,0" fieldOfView="55" nearPlane="0.1" farPlane="100" />
          <entity id="sun" name="Directional Light" type="light" rotation="-50,-30,0" intensity="1.2" />
        </world>
        """;
}

