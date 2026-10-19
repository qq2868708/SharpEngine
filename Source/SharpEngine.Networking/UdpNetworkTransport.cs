using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LiteNetLib;
using LiteNetLib.Layers;

namespace SharpEngine.Networking;

/// <summary>C# UDP with LiteNetLib reliability, fragmentation, per-peer channels and CRC32C.</summary>
public sealed class UdpNetworkTransport : INetworkTransport
{
    public const byte ChannelCount = 4;
    private const string ProtocolKey = "SharpEngine.Networking/udp/1";
    private readonly int OwnerThread = Environment.CurrentManagedThreadId;
    private readonly NetworkTransportOptions Options;
    private readonly NetManager Manager;
    private readonly Dictionary<string, NetPeer> Peers = new(StringComparer.Ordinal);
    private readonly Dictionary<NetPeer, string> Ids = new();
    private readonly Queue<NetworkTransportEvent> Events = new();
    private readonly HashSet<string> OverflowConnections = new(StringComparer.Ordinal);
    private readonly Stopwatch Timer = Stopwatch.StartNew();
    private double LastPollMilliseconds;
    private bool Disposed;

    public UdpNetworkTransport(NetworkTransportOptions? Options = null)
    {
        this.Options = Options ?? new NetworkTransportOptions();
        this.Options.Validate();
        var Listener = new EventBasedNetListener();
        Manager = new NetManager(Listener, new Crc32cLayer())
        {
            ChannelsCount = ChannelCount,
            AutoRecycle = true,
            IPv6Enabled = false,
            UseNativeSockets = false,
            MtuOverride = 1200,
            DisconnectTimeout = this.Options.DisconnectTimeoutMilliseconds,
            ReconnectDelay = 200,
            MaxConnectAttempts = 10,
            PingInterval = 500,
        };
        Listener.ConnectionRequestEvent += Request =>
        {
            if (Peers.Count >= this.Options.MaxConnections)
            {
                Request.Reject(Encoding.UTF8.GetBytes("TRANSPORT_CONNECTION_LIMIT"));
                return;
            }

            var Peer = Request.AcceptIfKey(ProtocolKey);
            if (Peer is not null)
            {
                Register(Peer);
            }
        };
        Listener.PeerConnectedEvent += Peer => Queue(new NetworkTransportEvent(NetworkTransportEventKind.Connected, Register(Peer)));
        Listener.PeerDisconnectedEvent += (Peer, Info) =>
        {
            var Found = Ids.Remove(Peer, out var ConnectionId);
            if (Found)
            {
                Peers.Remove(ConnectionId!);
                var Code = Info.Reason.ToString();
                if (Info.AdditionalData is not null && Info.AdditionalData.AvailableBytes is > 0 and <= 128)
                {
                    Code = Encoding.UTF8.GetString(Info.AdditionalData.GetRemainingBytesSpan());
                }

                Queue(new NetworkTransportEvent(NetworkTransportEventKind.Disconnected, ConnectionId!, Code: Code));
            }
        };
        Listener.NetworkErrorEvent += (EndPoint, Error) => Queue(new NetworkTransportEvent(
            NetworkTransportEventKind.Error, "", Code: "SOCKET_" + Error));
        Listener.NetworkReceiveEvent += (Peer, Reader, Channel, Delivery) =>
        {
            var Found = Ids.TryGetValue(Peer, out var ConnectionId);
            if (!Found)
            {
                return;
            }

            try
            {
                var Decoded = NetworkTransportCodec.Decode(Reader.GetRemainingBytesSpan(), this.Options.MaxPayloadBytes);
                Queue(new NetworkTransportEvent(NetworkTransportEventKind.Message, ConnectionId!, Decoded.Kind,
                    Decoded.Payload, Channel, FromDeliveryMethod(Delivery)));
            }
            catch (Exception Error) when (Error is InvalidDataException or JsonException)
            {
                Queue(new NetworkTransportEvent(NetworkTransportEventKind.Error, ConnectionId!, Code: "TRANSPORT_INVALID_PACKET"));
                Disconnect(ConnectionId!, "TRANSPORT_INVALID_PACKET");
            }
        };

        var Started = Manager.StartInManualMode(this.Options.BindAddress, IPAddress.IPv6Any, this.Options.LocalPort);
        if (!Started)
        {
            Manager.Stop();
            throw new IOException($"Unable to bind UDP {this.Options.BindAddress}:{this.Options.LocalPort}");
        }
    }

    public int LocalPort
    {
        get
        {
            CheckAccess();
            return Manager.LocalPort;
        }
    }

    public string Connect(IPEndPoint RemoteEndPoint)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(RemoteEndPoint);
        if (Peers.Count >= Options.MaxConnections)
        {
            throw new InvalidOperationException("TRANSPORT_CONNECTION_LIMIT");
        }

        var Peer = Manager.Connect(RemoteEndPoint, ProtocolKey);
        return Register(Peer ?? throw new InvalidOperationException("TRANSPORT_CONNECT_PENDING"));
    }

    public IReadOnlyList<NetworkTransportEvent> Poll()
    {
        CheckAccess();
        Manager.PollEvents(Options.MaxQueuedEvents);
        var Now = Timer.Elapsed.TotalMilliseconds;
        Manager.ManualUpdate((float)(Now - LastPollMilliseconds));
        LastPollMilliseconds = Now;
        var Result = Events.Concat(OverflowConnections.Select(ConnectionId => new NetworkTransportEvent(
            NetworkTransportEventKind.Disconnected, ConnectionId, Code: "TRANSPORT_EVENT_LIMIT"))).ToArray();
        Events.Clear();
        OverflowConnections.Clear();
        return Result;
    }

    public void Send(string ConnectionId, NetworkTransportMessageKind Kind, JsonObject Payload,
        byte Channel = 0, NetworkReliability Reliability = NetworkReliability.ReliableOrdered)
    {
        CheckAccess();
        ArgumentNullException.ThrowIfNull(Payload);
        var Found = Peers.TryGetValue(ConnectionId, out var Peer);
        if (!Found || Peer!.ConnectionState != ConnectionState.Connected)
        {
            throw new InvalidOperationException("TRANSPORT_NOT_CONNECTED");
        }

        if (Channel >= ChannelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(Channel));
        }

        var Bytes = NetworkTransportCodec.Encode(Kind, Payload, Options.MaxPayloadBytes);
        var Delivery = ToDeliveryMethod(Reliability);
        var CanFragment = Delivery == DeliveryMethod.ReliableOrdered;
        if (!CanFragment && Bytes.Length > Peer.GetMaxSinglePacketSize(Delivery))
        {
            throw new InvalidDataException("TRANSPORT_UNFRAGMENTED_PAYLOAD_TOO_LARGE");
        }

        var Pending = 0;
        for (byte Index = 0; Index < ChannelCount; Index++)
        {
            Pending += Peer.GetPacketsCountInReliableQueue(Index, true);
            Pending += Peer.GetPacketsCountInReliableQueue(Index, false);
        }

        var FragmentBudget = (Bytes.Length + 999) / 1000;
        if (Pending + FragmentBudget > Options.MaxPendingReliablePackets)
        {
            throw new InvalidOperationException("TRANSPORT_BACKPRESSURE");
        }

        Peer.Send(Bytes, Channel, Delivery);
    }

    public void Disconnect(string ConnectionId, string Code = "DISCONNECTED")
    {
        CheckAccess();
        var Found = Peers.TryGetValue(ConnectionId, out var Peer);
        if (Found)
        {
            Manager.DisconnectPeer(Peer!, Encoding.UTF8.GetBytes(Code[..Math.Min(Code.Length, 128)]));
        }
    }

    private string Register(NetPeer Peer)
    {
        var Found = Ids.TryGetValue(Peer, out var ConnectionId);
        if (Found)
        {
            return ConnectionId!;
        }

        ConnectionId = Guid.NewGuid().ToString("N");
        Ids.Add(Peer, ConnectionId);
        Peers.Add(ConnectionId, Peer);
        return ConnectionId;
    }

    private void Queue(NetworkTransportEvent Event)
    {
        if (Events.Count >= Options.MaxQueuedEvents)
        {
            OverflowConnections.Add(Event.ConnectionId);
            // End the overloaded connection; queued application messages are never partially executed.
            var Found = Peers.TryGetValue(Event.ConnectionId, out var Peer);
            if (Found)
            {
                Manager.DisconnectPeerForce(Peer!);
            }

            return;
        }

        Events.Enqueue(Event);
    }

    private static DeliveryMethod ToDeliveryMethod(NetworkReliability Reliability)
    {
        return Reliability switch
        {
            NetworkReliability.ReliableOrdered => DeliveryMethod.ReliableOrdered,
            NetworkReliability.ReliableSequenced => DeliveryMethod.ReliableSequenced,
            NetworkReliability.UnreliableSequenced => DeliveryMethod.Sequenced,
            NetworkReliability.Unreliable => DeliveryMethod.Unreliable,
            _ => throw new ArgumentOutOfRangeException(nameof(Reliability)),
        };
    }

    private static NetworkReliability FromDeliveryMethod(DeliveryMethod Delivery)
    {
        return Delivery switch
        {
            DeliveryMethod.ReliableOrdered => NetworkReliability.ReliableOrdered,
            DeliveryMethod.ReliableSequenced => NetworkReliability.ReliableSequenced,
            DeliveryMethod.Sequenced => NetworkReliability.UnreliableSequenced,
            DeliveryMethod.Unreliable => NetworkReliability.Unreliable,
            _ => throw new InvalidDataException("Unsupported delivery method"),
        };
    }

    private void CheckAccess()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (OwnerThread != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("Transport operations require the owning thread");
        }
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        CheckAccess();
        Manager.Stop();
        Peers.Clear();
        Ids.Clear();
        Events.Clear();
        Disposed = true;
    }
}
