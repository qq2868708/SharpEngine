using System.Text.Json.Nodes;
using SharpEngine.Components;

namespace SharpEngine.Networking;

public sealed class NetworkCommandEnvelope
{
    public NetworkCommandEnvelope(string ConnectionId, string MessageId, string MethodId,
        ulong InputSequence, ulong BaseTick, JsonObject Payload)
    {
        if (string.IsNullOrWhiteSpace(ConnectionId))
        {
            throw new ArgumentException("A connection id is required", nameof(ConnectionId));
        }

        if (string.IsNullOrWhiteSpace(MessageId))
        {
            throw new ArgumentException("A message id is required", nameof(MessageId));
        }

        if (string.IsNullOrWhiteSpace(MethodId))
        {
            throw new ArgumentException("A method id is required", nameof(MethodId));
        }

        this.ConnectionId = ConnectionId.Trim();
        this.MessageId = MessageId.Trim();
        this.MethodId = MethodId.Trim();
        this.InputSequence = InputSequence;
        this.BaseTick = BaseTick;
        this.Payload = (JsonObject)(Payload?.DeepClone() ?? throw new ArgumentNullException(nameof(Payload)));
    }

    public string ConnectionId
    {
        get;
    }

    public string MessageId
    {
        get;
    }

    public string MethodId
    {
        get;
    }

    public ulong InputSequence
    {
        get;
    }

    public ulong BaseTick
    {
        get;
    }

    public JsonObject Payload
    {
        get;
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["schemaVersion"] = NetworkProtocol.SchemaVersion,
            ["connectionId"] = ConnectionId,
            ["messageId"] = MessageId,
            ["methodId"] = MethodId,
            ["inputSequence"] = InputSequence,
            ["baseTick"] = BaseTick,
            ["payload"] = Payload.DeepClone(),
        };
    }

    public static NetworkCommandEnvelope FromJson(JsonObject Value)
    {
        ArgumentNullException.ThrowIfNull(Value);
        var SchemaVersion = Value["schemaVersion"]?.GetValue<int>() ?? throw new InvalidDataException("Command schemaVersion is required");
        if (SchemaVersion != NetworkProtocol.SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported command schema version {SchemaVersion}");
        }

        var ConnectionId = NetworkWire.String(Value, "connectionId");
        var MessageId = NetworkWire.String(Value, "messageId");
        var MethodId = NetworkWire.String(Value, "methodId");
        var InputSequence = Value["inputSequence"]?.GetValue<ulong>() ?? throw new InvalidDataException("Command inputSequence is required");
        var BaseTick = Value["baseTick"]?.GetValue<ulong>() ?? throw new InvalidDataException("Command baseTick is required");
        var Payload = Value["payload"]?.AsObject() ?? throw new InvalidDataException("Command payload is required");
        return new NetworkCommandEnvelope(ConnectionId, MessageId, MethodId, InputSequence, BaseTick, Payload);
    }
}

public sealed class NetworkCommandResult
{
    private NetworkCommandResult(string MessageId, bool Accepted, string Code, string Message,
        ulong ServerTick, ulong StateRevision, bool Retryable, bool Duplicate, string? ConnectionId)
    {
        this.MessageId = MessageId;
        this.Accepted = Accepted;
        this.Code = Code;
        this.Message = Message;
        this.ServerTick = ServerTick;
        this.StateRevision = StateRevision;
        this.Retryable = Retryable;
        this.Duplicate = Duplicate;
        this.ConnectionId = ConnectionId;
    }

    public string MessageId
    {
        get;
    }

    public bool Accepted
    {
        get;
    }

    public string Code
    {
        get;
    }

    public string Message
    {
        get;
    }

    public ulong ServerTick
    {
        get;
    }

    public ulong StateRevision
    {
        get;
    }

    public bool Retryable
    {
        get;
    }

    public bool Duplicate
    {
        get;
    }

    public string? ConnectionId
    {
        get;
    }

    public static NetworkCommandResult AcceptedResult(string MessageId, string Message = "Accepted")
    {
        return new NetworkCommandResult(MessageId, true, "OK", Message, 0, 0, false, false, null);
    }

    public static NetworkCommandResult Rejected(string MessageId, string Code, string Message, bool Retryable = false)
    {
        return new NetworkCommandResult(MessageId, false, Code, Message, 0, 0, Retryable, false, null);
    }

    internal NetworkCommandResult WithServerState(ulong ServerTick, ulong StateRevision,
        bool Duplicate = false, string? ConnectionId = null)
    {
        return new NetworkCommandResult(MessageId, Accepted, Code, Message, ServerTick, StateRevision,
            Retryable, Duplicate, ConnectionId ?? this.ConnectionId);
    }

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["schemaVersion"] = NetworkProtocol.SchemaVersion,
            ["messageId"] = MessageId,
            ["accepted"] = Accepted,
            ["code"] = Code,
            ["message"] = Message,
            ["serverTick"] = ServerTick,
            ["stateRevision"] = StateRevision,
            ["retryable"] = Retryable,
            ["duplicate"] = Duplicate,
            ["connectionId"] = ConnectionId,
        };
    }
}

public delegate NetworkCommandResult NetworkCommandHandler(
    NetworkCommandEnvelope Command, RuntimeWorld World, ulong ServerTick);

public sealed class NetworkCommandRouter
{
    private readonly NetworkSchemaCatalog Catalog;
    private readonly Dictionary<string, NetworkCommandHandler> Handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ConnectionId, string MessageId), NetworkCommandResult> ResultsByMessage = new();

    public NetworkCommandRouter(NetworkSchemaCatalog Catalog)
    {
        this.Catalog = Catalog ?? throw new ArgumentNullException(nameof(Catalog));
    }

    public void Register(NetworkRpcDescriptor Descriptor, NetworkCommandHandler Handler)
    {
        ArgumentNullException.ThrowIfNull(Descriptor);
        ArgumentNullException.ThrowIfNull(Handler);
        if (Descriptor.Kind != NetworkMessageKind.Command)
        {
            throw new InvalidDataException($"Only command RPCs can be registered with the command router: {Descriptor.MethodId}");
        }

        if (Catalog.FindRpc(Descriptor.MethodId) is null)
        {
            Catalog.RegisterRpc(Descriptor);
        }

        if (!Handlers.TryAdd(Descriptor.MethodId, Handler))
        {
            throw new InvalidDataException($"Duplicate command handler {Descriptor.MethodId}");
        }
    }

    public NetworkCommandResult Execute(NetworkCommandEnvelope Command, RuntimeWorld World, ulong ServerTick)
    {
        ArgumentNullException.ThrowIfNull(Command);
        ArgumentNullException.ThrowIfNull(World);
        var ResultKey = (Command.ConnectionId, Command.MessageId);
        var IsDuplicate = ResultsByMessage.TryGetValue(ResultKey, out var Previous);
        if (IsDuplicate)
        {
            return Previous!.WithServerState(ServerTick, World.Revision, true, Command.ConnectionId);
        }

        var Descriptor = Catalog.FindRpc(Command.MethodId);
        if (Descriptor is null || Descriptor.Kind != NetworkMessageKind.Command)
        {
            return Store(ResultKey, NetworkCommandResult.Rejected(Command.MessageId, "RPC_UNKNOWN", $"Unknown command {Command.MethodId}", false), ServerTick, World, Command.ConnectionId);
        }

        if (Command.BaseTick > ServerTick + 1)
        {
            return Store(ResultKey, NetworkCommandResult.Rejected(Command.MessageId, "COMMAND_FUTURE_TICK", "Command targets a future server tick", true), ServerTick, World, Command.ConnectionId);
        }

        var HasHandler = Handlers.TryGetValue(Command.MethodId, out var Handler);
        if (!HasHandler)
        {
            return Store(ResultKey, NetworkCommandResult.Rejected(Command.MessageId, "RPC_UNAVAILABLE", $"No handler is registered for {Command.MethodId}", true), ServerTick, World, Command.ConnectionId);
        }

        NetworkCommandResult Result;
        try
        {
            Result = Handler!(Command, World, ServerTick) ?? NetworkCommandResult.Rejected(Command.MessageId, "RPC_NULL_RESULT", "Command handler returned no result");
        }
        catch (Exception Error)
        {
            Result = NetworkCommandResult.Rejected(Command.MessageId, "RPC_HANDLER_FAILED", Error.Message, true);
        }

        return Store(ResultKey, Result, ServerTick, World, Command.ConnectionId);
    }

    public void ForgetConnection(string ConnectionId)
    {
        foreach (var Key in ResultsByMessage.Keys.Where(Key => Key.ConnectionId == ConnectionId).ToArray())
        {
            ResultsByMessage.Remove(Key);
        }
    }

    private NetworkCommandResult Store((string ConnectionId, string MessageId) ResultKey, NetworkCommandResult Result, ulong ServerTick,
        RuntimeWorld World, string ConnectionId)
    {
        var Completed = Result.WithServerState(ServerTick, World.Revision, false, ConnectionId);
        ResultsByMessage[ResultKey] = Completed;
        return Completed;
    }
}
