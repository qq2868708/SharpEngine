using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Networking;

/// <summary>Polling client with a committed replica and application acknowledgements. Owns its transport.</summary>
public sealed class NetworkClientSession : IDisposable
{
    private readonly INetworkTransport Transport;
    private readonly NetworkSchemaCatalog Schema;
    private readonly string Fingerprint;
    private string? TransportId;
    private bool Disposed;

    public NetworkClientSession(NetworkSchemaCatalog Schema, INetworkTransport Transport)
    {
        this.Schema = Schema ?? throw new ArgumentNullException(nameof(Schema));
        this.Transport = Transport ?? throw new ArgumentNullException(nameof(Transport));
        Fingerprint = Schema.Fingerprint();
        Replica = new NetworkReplicaState(Schema);
    }

    public bool IsReady
    {
        get;
        private set;
    }

    public string? ConnectionId
    {
        get;
        private set;
    }

    public string LastCode
    {
        get;
        private set;
    } = "NOT_CONNECTED";

    public NetworkReplicaState Replica
    {
        get;
        private set;
    }

    public void Connect(IPEndPoint Server)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (TransportId is not null)
        {
            throw new InvalidOperationException("Client already has a connection");
        }

        TransportId = Transport.Connect(Server);
        ConnectionId = null;
        IsReady = false;
        Replica = new NetworkReplicaState(Schema);
        LastCode = "CONNECTING";
    }

    public IReadOnlyList<NetworkTransportEvent> Poll()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        var Results = new List<NetworkTransportEvent>();
        foreach (var Event in Transport.Poll())
        {
            if (Event.ConnectionId != TransportId)
            {
                continue;
            }

            if (Event.Kind is NetworkTransportEventKind.Disconnected or NetworkTransportEventKind.Error)
            {
                IsReady = false;
                ConnectionId = null;
                TransportId = null;
                LastCode = Event.Code;
                Results.Add(Event);
                continue;
            }

            if (Event.Kind == NetworkTransportEventKind.Connected)
            {
                Transport.Send(TransportId!, NetworkTransportMessageKind.Hello, new JsonObject
                {
                    ["schemaVersion"] = NetworkProtocol.SchemaVersion,
                    ["schemaFingerprint"] = Fingerprint,
                });
                continue;
            }

            try
            {
                Receive(Event);
                Results.Add(Event);
            }
            catch (Exception Error) when (Error is JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NullReferenceException or OverflowException)
            {
                LastCode = "PROTOCOL_INVALID_MESSAGE";
                IsReady = false;
                Transport.Disconnect(TransportId!, LastCode);
                Results.Add(new NetworkTransportEvent(NetworkTransportEventKind.Error, TransportId!, Code: LastCode));
            }
        }

        return Results;
    }

    private void Receive(NetworkTransportEvent Event)
    {
        var Value = Event.Payload ?? throw new InvalidDataException("Server payload is required");
        NetworkWire.RequireVersion(Value);
        if (Event.Reliability != NetworkReliability.ReliableOrdered)
        {
            throw new InvalidDataException("Server protocol requires reliable ordered delivery");
        }

        if (Event.MessageKind == NetworkTransportMessageKind.HelloAccepted && Event.Channel == 0 && !IsReady)
        {
            if (NetworkWire.String(Value, "schemaFingerprint") != Fingerprint)
            {
                throw new InvalidDataException("Server Schema does not match the client");
            }

            ConnectionId = NetworkWire.String(Value, "connectionId");
            Transport.Send(TransportId!, NetworkTransportMessageKind.Ready, new JsonObject
            {
                ["schemaVersion"] = NetworkProtocol.SchemaVersion,
                ["connectionId"] = ConnectionId,
            });
            LastCode = "OK";
            IsReady = true;
            return;
        }

        if (!IsReady)
        {
            throw new InvalidDataException("Handshake has not completed");
        }

        if (Event.MessageKind == NetworkTransportMessageKind.CommandResult && Event.Channel == 1)
        {
            if (NetworkWire.String(Value, "connectionId") != ConnectionId)
            {
                throw new InvalidDataException("Result belongs to another connection");
            }

            return;
        }

        if (Event.MessageKind == NetworkTransportMessageKind.Snapshot && Event.Channel == 2)
        {
            Replica.Apply(Value);
            Transport.Send(TransportId!, NetworkTransportMessageKind.SnapshotAcknowledged, new JsonObject
            {
                ["schemaVersion"] = NetworkProtocol.SchemaVersion,
                ["snapshotId"] = Replica.SnapshotId,
            });
            return;
        }

        throw new InvalidDataException("Unexpected server message");
    }

    public void SendCommand(string MessageId, string MethodId, ulong InputSequence, JsonObject Payload)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (!IsReady)
        {
            throw new InvalidOperationException("Client handshake has not completed");
        }

        var Command = new NetworkCommandEnvelope(ConnectionId!, MessageId, MethodId, InputSequence, Replica.ServerTick, Payload);
        Transport.Send(TransportId!, NetworkTransportMessageKind.Command, Command.ToJson(), 1);
    }

    public void Disconnect()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (TransportId is not null)
        {
            Transport.Disconnect(TransportId);
        }

        IsReady = false;
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Transport.Dispose();
        Disposed = true;
        IsReady = false;
    }
}
