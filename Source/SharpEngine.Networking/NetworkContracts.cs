using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SharpEngine.Networking;

/// <summary>Runtime services exposed to project modules loaded by a Dedicated Server.</summary>
public interface INetworkServerRuntime : SharpEngine.Components.IComponentRuntime,
    SharpEngine.Subsystems.ISubsystemRuntime
{
    NetworkSchemaCatalog Schema
    {
        get;
    }

    NetworkCommandRouter CommandRouter
    {
        get;
    }

    Func<NetworkCommandEnvelope, bool>? AuthorizeCommand
    {
        get;
        set;
    }

    Func<string, IReadOnlySet<string>?>? RelevantEntities
    {
        get;
        set;
    }

    void RegisterCommand(NetworkRpcDescriptor Descriptor, NetworkCommandHandler Handler);
}

public static class NetworkProtocol
{
    public const int SchemaVersion = 1;
}

public enum NetworkMessageKind
{
    Command,
    Query,
    Event,
}

public enum NetworkReliability
{
    ReliableOrdered,
    ReliableSequenced,
    UnreliableSequenced,
    Unreliable,
}

public enum NetworkAuthority
{
    Server,
    OwningClient,
}

public enum NetworkReplicationCondition
{
    Relevant,
    OwnerOnly,
    InitialOnly,
    Always,
}

public sealed class NetworkPropertyDescriptor
{
    public NetworkPropertyDescriptor(string PropertyId,
        NetworkAuthority Authority = NetworkAuthority.Server,
        NetworkReliability Reliability = NetworkReliability.ReliableSequenced,
        NetworkReplicationCondition Condition = NetworkReplicationCondition.Relevant)
        : this(PropertyId, PropertyId, Authority, Reliability, Condition)
    {
    }

    public NetworkPropertyDescriptor(string PropertyId, string FieldName,
        NetworkAuthority Authority = NetworkAuthority.Server,
        NetworkReliability Reliability = NetworkReliability.ReliableSequenced,
        NetworkReplicationCondition Condition = NetworkReplicationCondition.Relevant)
    {
        if (string.IsNullOrWhiteSpace(PropertyId))
        {
            throw new ArgumentException("A network property id is required", nameof(PropertyId));
        }

        this.PropertyId = PropertyId.Trim();
        if (string.IsNullOrWhiteSpace(FieldName))
        {
            throw new ArgumentException("A component field name is required", nameof(FieldName));
        }

        this.FieldName = FieldName.Trim();
        this.Authority = Authority;
        this.Reliability = Reliability;
        this.Condition = Condition;
    }

    public string PropertyId
    {
        get;
    }

    public string FieldName
    {
        get;
    }

    public NetworkAuthority Authority
    {
        get;
    }

    public NetworkReliability Reliability
    {
        get;
    }

    public NetworkReplicationCondition Condition
    {
        get;
    }

    internal JsonObject ToJson()
    {
        return new JsonObject
        {
            ["propertyId"] = PropertyId,
            ["fieldName"] = FieldName,
            ["authority"] = Authority.ToString(),
            ["reliability"] = Reliability.ToString(),
            ["condition"] = Condition.ToString(),
        };
    }
}

public sealed class NetworkComponentDescriptor
{
    public NetworkComponentDescriptor(string TypeId, int SchemaVersion,
        IEnumerable<NetworkPropertyDescriptor> Properties)
    {
        if (string.IsNullOrWhiteSpace(TypeId))
        {
            throw new ArgumentException("A component type id is required", nameof(TypeId));
        }

        if (SchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SchemaVersion));
        }

        this.TypeId = TypeId.Trim();
        this.SchemaVersion = SchemaVersion;
        var Values = Properties?.ToArray() ?? throw new ArgumentNullException(nameof(Properties));
        if (Values.Select(Property => Property.PropertyId).Distinct(StringComparer.Ordinal).Count() != Values.Length)
        {
            throw new InvalidDataException($"Duplicate network property in component {this.TypeId}");
        }

        this.Properties = Array.AsReadOnly(Values.OrderBy(Property => Property.PropertyId, StringComparer.Ordinal).ToArray());
    }

    public string TypeId
    {
        get;
    }

    public int SchemaVersion
    {
        get;
    }

    public IReadOnlyList<NetworkPropertyDescriptor> Properties
    {
        get;
    }

    internal JsonObject ToJson()
    {
        return new JsonObject
        {
            ["typeId"] = TypeId,
            ["schemaVersion"] = SchemaVersion,
            ["properties"] = new JsonArray(Properties.Select(Property => (JsonNode)Property.ToJson()).ToArray()),
        };
    }
}

public sealed class NetworkRpcDescriptor
{
    public NetworkRpcDescriptor(string MethodId, NetworkMessageKind Kind,
        NetworkReliability Reliability, bool RequiresAuthority = true)
    {
        if (string.IsNullOrWhiteSpace(MethodId))
        {
            throw new ArgumentException("A network method id is required", nameof(MethodId));
        }

        this.MethodId = MethodId.Trim();
        this.Kind = Kind;
        this.Reliability = Reliability;
        this.RequiresAuthority = RequiresAuthority;
    }

    public string MethodId
    {
        get;
    }

    public NetworkMessageKind Kind
    {
        get;
    }

    public NetworkReliability Reliability
    {
        get;
    }

    public bool RequiresAuthority
    {
        get;
    }

    internal JsonObject ToJson()
    {
        return new JsonObject
        {
            ["methodId"] = MethodId,
            ["kind"] = Kind.ToString(),
            ["reliability"] = Reliability.ToString(),
            ["requiresAuthority"] = RequiresAuthority,
        };
    }
}

public sealed class NetworkSchemaCatalog
{
    private readonly Dictionary<string, NetworkComponentDescriptor> ComponentsByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NetworkRpcDescriptor> RpcsByMethod = new(StringComparer.Ordinal);

    public IReadOnlyCollection<NetworkComponentDescriptor> Components
    {
        get
        {
            return ComponentsByType.Values.ToArray();
        }
    }

    public IReadOnlyCollection<NetworkRpcDescriptor> Rpcs
    {
        get
        {
            return RpcsByMethod.Values.ToArray();
        }
    }

    public void RegisterComponent(NetworkComponentDescriptor Descriptor)
    {
        ArgumentNullException.ThrowIfNull(Descriptor);
        if (!ComponentsByType.TryAdd(Descriptor.TypeId, Descriptor))
        {
            throw new InvalidDataException($"Duplicate network component {Descriptor.TypeId}");
        }
    }

    public void RegisterRpc(NetworkRpcDescriptor Descriptor)
    {
        ArgumentNullException.ThrowIfNull(Descriptor);
        if (!RpcsByMethod.TryAdd(Descriptor.MethodId, Descriptor))
        {
            throw new InvalidDataException($"Duplicate network method {Descriptor.MethodId}");
        }
    }

    public NetworkComponentDescriptor? FindComponent(string TypeId)
    {
        ComponentsByType.TryGetValue(TypeId, out var Descriptor);
        return Descriptor;
    }

    public NetworkRpcDescriptor? FindRpc(string MethodId)
    {
        RpcsByMethod.TryGetValue(MethodId, out var Descriptor);
        return Descriptor;
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["schemaVersion"] = NetworkProtocol.SchemaVersion,
            ["components"] = new JsonArray(ComponentsByType.OrderBy(Entry => Entry.Key, StringComparer.Ordinal)
                .Select(Entry => (JsonNode)Entry.Value.ToJson()).ToArray()),
            ["rpcs"] = new JsonArray(RpcsByMethod.OrderBy(Entry => Entry.Key, StringComparer.Ordinal)
                .Select(Entry => (JsonNode)Entry.Value.ToJson()).ToArray()),
        };
    }

    public string Fingerprint()
    {
        var Bytes = Encoding.UTF8.GetBytes(ToJson().ToJsonString());
        return Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
    }

}
