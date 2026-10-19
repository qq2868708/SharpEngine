using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Networking;

/// <summary>Owns a transport; borrows a session. Poll and Tick must run on the World thread.</summary>
public sealed class DedicatedServerNetworkHost : IDisposable
{
    private readonly DedicatedServerSession Session;
    private readonly INetworkTransport Transport;
    private readonly string SchemaFingerprint;
    private readonly Dictionary<string, Connection> Connections = new(StringComparer.Ordinal);
    private readonly Stopwatch Timer = Stopwatch.StartNew();
    private bool Disposed;

    public DedicatedServerNetworkHost(DedicatedServerSession Session, INetworkTransport Transport)
    {
        this.Session = Session ?? throw new ArgumentNullException(nameof(Session));
        this.Transport = Transport ?? throw new ArgumentNullException(nameof(Transport));
        Session.World.CheckThread();
        SchemaFingerprint = Session.Schema.Fingerprint();
    }

    /// <summary>Required for RequiresAuthority commands. Evaluate ownership and permissions here.</summary>
    public Func<NetworkCommandEnvelope, bool>? AuthorizeCommand
    {
        get;
        set;
    }

    public Func<string, IReadOnlySet<string>?>? RelevantEntities
    {
        get;
        set;
    }

    public int LocalPort
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Session.World.CheckThread();
            return Transport.LocalPort;
        }
    }

    public int ReadyConnectionCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Session.World.CheckThread();
            return Connections.Values.Count(Connection => Connection.Ready);
        }
    }

    public void Poll()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Session.World.CheckThread();
        foreach (var Event in Transport.Poll())
        {
            if (Event.Kind == NetworkTransportEventKind.Connected)
            {
                Connections[Event.ConnectionId] = new Connection(Timer.Elapsed.TotalSeconds);
                continue;
            }

            if (Event.Kind is NetworkTransportEventKind.Disconnected or NetworkTransportEventKind.Error)
            {
                Remove(Event.ConnectionId);
                continue;
            }

            var Found = Connections.TryGetValue(Event.ConnectionId, out var Connection);
            if (!Found || Event.Payload is null)
            {
                continue;
            }

            try
            {
                Receive(Event, Connection!);
            }
            catch (Exception Error) when (Error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or OverflowException)
            {
                Close(Event.ConnectionId, "PROTOCOL_INVALID_MESSAGE");
            }
        }

        foreach (var Entry in Connections.ToArray())
        {
            var Connection = Entry.Value;
            var TimedOut = !Connection.Ready && Timer.Elapsed.TotalSeconds - Connection.CreatedSeconds > 5;
            var SnapshotTimedOut = Connection.Pending is not null && Timer.Elapsed.TotalSeconds - Connection.SentSeconds > 10;
            if (TimedOut || SnapshotTimedOut)
            {
                Close(Entry.Key, "PROTOCOL_ACK_TIMEOUT");
            }
        }
    }

    /// <summary>Commands are authorized immediately before execution; network callbacks never mutate the World.</summary>
    public NetworkServerFrame Tick(float DeltaSeconds)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Session.World.CheckThread();
        if (Session.Schema.Fingerprint() != SchemaFingerprint)
        {
            throw new InvalidOperationException("Network Schema cannot change while a host is running");
        }

        Poll();
        foreach (var Entry in Connections.ToArray())
        {
            var Connection = Entry.Value;
            foreach (var Command in Connection.Commands)
            {
                if (!Connections.ContainsKey(Entry.Key))
                {
                    break;
                }

                var Descriptor = Session.Schema.FindRpc(Command.MethodId);
                var RequiresAuthority = Descriptor?.RequiresAuthority ?? false;
                var Authorized = !RequiresAuthority || (AuthorizeCommand?.Invoke(Command) ?? false);
                if (!Authorized)
                {
                    SendResult(Entry.Key, NetworkCommandResult.Rejected(Command.MessageId, "RPC_AUTHORITY_DENIED",
                        "Command authority was not granted").WithServerState(Session.ServerTick, Session.World.Revision, ConnectionId: Entry.Key));
                    continue;
                }

                Session.Enqueue(Command);
            }

            Connection.Commands.Clear();
        }

        var Frame = Session.Tick(DeltaSeconds);
        foreach (var Result in Frame.CommandResults)
        {
            if (Result.ConnectionId is not null && Connections.ContainsKey(Result.ConnectionId))
            {
                SendResult(Result.ConnectionId, Result);
            }
        }

        foreach (var Entry in Connections.ToArray())
        {
            var Connection = Entry.Value;
            if (!Connection.Ready || Connection.Pending is not null)
            {
                continue;
            }

            var Snapshot = Session.BuildSnapshot(Connection.Baseline, RelevantEntities?.Invoke(Entry.Key));
            var SnapshotId = Connection.NextSnapshotId++;
            var Message = new JsonObject
            {
                ["schemaVersion"] = NetworkProtocol.SchemaVersion,
                ["snapshotId"] = SnapshotId,
                ["baselineId"] = Connection.BaselineId,
                ["serverTick"] = Frame.ServerTick,
                ["snapshot"] = Snapshot.ToJson(),
            };
            var Sent = TrySend(Entry.Key, NetworkTransportMessageKind.Snapshot, Message, 2);
            if (Sent)
            {
                Connection.Pending = Snapshot;
                Connection.PendingId = SnapshotId;
                Connection.SentSeconds = Timer.Elapsed.TotalSeconds;
            }
        }

        return Frame;
    }

    private void Receive(NetworkTransportEvent Event, Connection Connection)
    {
        var Value = Event.Payload!;
        NetworkWire.RequireVersion(Value);
        if (Event.Reliability != NetworkReliability.ReliableOrdered)
        {
            throw new InvalidDataException("Control and command messages require reliable ordered delivery");
        }

        if (Event.MessageKind == NetworkTransportMessageKind.Hello && Event.Channel == 0 && !Connection.Accepted)
        {
            var Fingerprint = NetworkWire.String(Value, "schemaFingerprint");
            if (Fingerprint != SchemaFingerprint)
            {
                Close(Event.ConnectionId, "SCHEMA_MISMATCH");
                return;
            }

            Connection.Accepted = true;
            TrySend(Event.ConnectionId, NetworkTransportMessageKind.HelloAccepted, new JsonObject
            {
                ["schemaVersion"] = NetworkProtocol.SchemaVersion,
                ["connectionId"] = Event.ConnectionId,
                ["schemaFingerprint"] = SchemaFingerprint,
            }, 0);
            return;
        }

        if (!Connection.Accepted)
        {
            throw new InvalidDataException("Handshake is required");
        }

        if (Event.MessageKind == NetworkTransportMessageKind.Ready && Event.Channel == 0 && !Connection.Ready)
        {
            if (NetworkWire.String(Value, "connectionId") != Event.ConnectionId)
            {
                throw new InvalidDataException("Ready acknowledgement belongs to another connection");
            }

            Connection.Ready = true;
            return;
        }

        if (Event.MessageKind == NetworkTransportMessageKind.Command && Event.Channel == 1)
        {
            var Command = NetworkCommandEnvelope.FromJson(Value);
            if (Command.ConnectionId != Event.ConnectionId)
            {
                Close(Event.ConnectionId, "CONNECTION_ID_MISMATCH");
                return;
            }

            if (Connection.Commands.Count >= 128 || Connection.MessageIds.Count >= 16384)
            {
                Close(Event.ConnectionId, "COMMAND_LIMIT");
                return;
            }

            Connection.MessageIds.Add(Command.MessageId);
            Connection.Commands.Add(Command);
            return;
        }

        if (Event.MessageKind == NetworkTransportMessageKind.SnapshotAcknowledged && Event.Channel == 0)
        {
            var SnapshotId = NetworkWire.UInt64(Value, "snapshotId");
            if (SnapshotId == Connection.BaselineId)
            {
                return;
            }

            if (Connection.Pending is null || SnapshotId != Connection.PendingId)
            {
                throw new InvalidDataException("Snapshot acknowledgement has no matching sent snapshot");
            }

            Connection.Baseline = Connection.Pending.CreateBaseline();
            Connection.BaselineId = SnapshotId;
            Connection.Pending = null;
            return;
        }

        throw new InvalidDataException("Unexpected message kind or channel");
    }

    private void SendResult(string ConnectionId, NetworkCommandResult Result)
    {
        TrySend(ConnectionId, NetworkTransportMessageKind.CommandResult, Result.ToJson(), 1);
    }

    private bool TrySend(string ConnectionId, NetworkTransportMessageKind Kind, JsonObject Value, byte Channel)
    {
        try
        {
            Transport.Send(ConnectionId, Kind, Value, Channel);
            return true;
        }
        catch (Exception Error) when (Error is InvalidOperationException or InvalidDataException)
        {
            Close(ConnectionId, "TRANSPORT_SEND_FAILED");
            return false;
        }
    }

    private void Close(string ConnectionId, string Code)
    {
        Transport.Disconnect(ConnectionId, Code);
        Remove(ConnectionId);
    }

    private void Remove(string ConnectionId)
    {
        Connections.Remove(ConnectionId);
        Session.ForgetConnection(ConnectionId);
    }

    public JsonObject Describe()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Session.World.CheckThread();
        return new JsonObject
        {
            ["schemaVersion"] = NetworkProtocol.SchemaVersion,
            ["serverTick"] = Session.ServerTick,
            ["schemaFingerprint"] = SchemaFingerprint,
            ["connections"] = new JsonArray(Connections.OrderBy(Entry => Entry.Key, StringComparer.Ordinal).Select(Entry => (JsonNode)new JsonObject
            {
                ["connectionId"] = Entry.Key,
                ["ready"] = Entry.Value.Ready,
                ["baselineId"] = Entry.Value.BaselineId,
                ["pendingSnapshotId"] = Entry.Value.Pending is null ? null : JsonValue.Create(Entry.Value.PendingId),
                ["queuedCommands"] = Entry.Value.Commands.Count,
            }).ToArray()),
        };
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Session.World.CheckThread();
        foreach (var ConnectionId in Connections.Keys.ToArray())
        {
            Session.ForgetConnection(ConnectionId);
        }

        Connections.Clear();
        Transport.Dispose();
        Disposed = true;
    }

    private sealed class Connection(double CreatedSeconds)
    {
        public readonly double CreatedSeconds = CreatedSeconds;
        public bool Ready;
        public bool Accepted;
        public readonly List<NetworkCommandEnvelope> Commands = [];
        public readonly HashSet<string> MessageIds = new(StringComparer.Ordinal);
        public NetworkReplicationBaseline? Baseline;
        public ulong BaselineId;
        public NetworkSnapshot? Pending;
        public ulong PendingId;
        public ulong NextSnapshotId = 1;
        public double SentSeconds;
    }
}

internal static class NetworkWire
{
    public static void RequireVersion(JsonObject Value)
    {
        if (Value["schemaVersion"]?.GetValue<int>() != NetworkProtocol.SchemaVersion)
        {
            throw new InvalidDataException("Unsupported network schema version");
        }
    }

    public static string String(JsonObject Value, string Field)
    {
        var Text = Value[Field]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(Text) || Text.Length > 256)
        {
            throw new InvalidDataException($"Invalid network field {Field}");
        }

        return Text;
    }

    public static ulong UInt64(JsonObject Value, string Field)
    {
        var Node = Value[Field] ?? throw new InvalidDataException($"Missing network field {Field}");
        var Token = Node.ToJsonString();
        var Parsed = ulong.TryParse(Token, NumberStyles.None, CultureInfo.InvariantCulture, out var Result);
        if (!Parsed)
        {
            throw new InvalidDataException($"Invalid network field {Field}");
        }

        return Result;
    }
}
