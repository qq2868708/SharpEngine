using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpEngine.Networking;

public enum NetworkTransportMessageKind : byte
{
    Hello = 1,
    HelloAccepted = 2,
    Command = 3,
    CommandResult = 4,
    Snapshot = 5,
    SnapshotAcknowledged = 6,
    Event = 7,
    Ready = 8,
}

public enum NetworkTransportEventKind
{
    Connected,
    Disconnected,
    Message,
    Error,
}

public sealed record NetworkTransportEvent(NetworkTransportEventKind Kind, string ConnectionId,
    NetworkTransportMessageKind? MessageKind = null, JsonObject? Payload = null,
    byte Channel = 0, NetworkReliability Reliability = NetworkReliability.ReliableOrdered,
    string Code = "OK");

/// <summary>Poll, Send and lifecycle calls belong to one owning thread. Send queues delivery.</summary>
public interface INetworkTransport : IDisposable
{
    int LocalPort
    {
        get;
    }

    string Connect(IPEndPoint RemoteEndPoint);
    IReadOnlyList<NetworkTransportEvent> Poll();
    void Send(string ConnectionId, NetworkTransportMessageKind Kind, JsonObject Payload,
        byte Channel = 0, NetworkReliability Reliability = NetworkReliability.ReliableOrdered);
    void Disconnect(string ConnectionId, string Code = "DISCONNECTED");
}

public sealed class NetworkTransportOptions
{
    public IPAddress BindAddress
    {
        get;
        init;
    } = IPAddress.Any;

    public int LocalPort
    {
        get;
        init;
    }

    public int MaxPayloadBytes
    {
        get;
        init;
    } = 256 * 1024;

    public int MaxConnections
    {
        get;
        init;
    } = 64;

    public int MaxQueuedEvents
    {
        get;
        init;
    } = 1024;

    public int MaxPendingReliablePackets
    {
        get;
        init;
    } = 1024;

    public int DisconnectTimeoutMilliseconds
    {
        get;
        init;
    } = 5000;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BindAddress);
        if (BindAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || LocalPort is < 0 or > 65535)
        {
            throw new ArgumentException("UDP transport requires an IPv4 bind address and a valid port");
        }

        if (MaxPayloadBytes is < 256 or > 1024 * 1024 || MaxConnections is < 1 or > 4096 ||
            MaxQueuedEvents < 8 || MaxPendingReliablePackets < 8 || DisconnectTimeoutMilliseconds < 100)
        {
            throw new ArgumentOutOfRangeException(nameof(NetworkTransportOptions));
        }
    }
}

internal static class NetworkTransportCodec
{
    public const int HeaderSize = 12;
    public const ushort WireVersion = 1;
    private const uint Magic = 0x53454E31;

    public static byte[] Encode(NetworkTransportMessageKind Kind, JsonObject Payload, int MaxPayloadBytes)
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new InvalidDataException("Unknown network message kind");
        }

        var Body = JsonSerializer.SerializeToUtf8Bytes(Payload);
        if (Body.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException("TRANSPORT_PAYLOAD_TOO_LARGE");
        }

        var Bytes = new byte[HeaderSize + Body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan(4, 2), WireVersion);
        Bytes[6] = (byte)Kind;
        BinaryPrimitives.WriteInt32BigEndian(Bytes.AsSpan(8, 4), Body.Length);
        Body.CopyTo(Bytes, HeaderSize);
        return Bytes;
    }

    public static (NetworkTransportMessageKind Kind, JsonObject Payload) Decode(ReadOnlySpan<byte> Bytes, int MaxPayloadBytes)
    {
        if (Bytes.Length < HeaderSize || Bytes.Length > HeaderSize + MaxPayloadBytes)
        {
            throw new InvalidDataException("TRANSPORT_INVALID_LENGTH");
        }

        var Length = BinaryPrimitives.ReadInt32BigEndian(Bytes.Slice(8, 4));
        var Kind = (NetworkTransportMessageKind)Bytes[6];
        if (BinaryPrimitives.ReadUInt32BigEndian(Bytes[..4]) != Magic ||
            BinaryPrimitives.ReadUInt16BigEndian(Bytes.Slice(4, 2)) != WireVersion ||
            Bytes[7] != 0 || Length != Bytes.Length - HeaderSize || !Enum.IsDefined(Kind))
        {
            throw new InvalidDataException("TRANSPORT_INVALID_HEADER");
        }

        var Payload = JsonNode.Parse(Bytes[HeaderSize..]) as JsonObject;
        if (Payload is null)
        {
            throw new InvalidDataException("TRANSPORT_INVALID_JSON");
        }

        return (Kind, Payload);
    }
}
