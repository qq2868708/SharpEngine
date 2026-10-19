using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Networking;

internal static class TransportValidation
{
    public static void Run(Action<bool, string> Check)
    {
        ValidateDatagrams(Check);
        ValidateProtocol(Check);
        ValidateReplica(Check);
        ValidateProcesses(Check);
    }

    private static void Pump(Func<bool> Completed, Action Step, double TimeoutSeconds = 12)
    {
        var Timer = Stopwatch.StartNew();
        while (!Completed())
        {
            if (Timer.Elapsed.TotalSeconds > TimeoutSeconds)
            {
                throw new TimeoutException("Network validation exceeded its deadline");
            }

            Step();
            Thread.Sleep(2);
        }
    }

    private static void ValidateDatagrams(Action<bool, string> Check)
    {
        using var Server = new UdpNetworkTransport(new NetworkTransportOptions
        {
            BindAddress = IPAddress.Loopback,
            DisconnectTimeoutMilliseconds = 2000,
        });
        using var Client = new UdpNetworkTransport(new NetworkTransportOptions
        {
            BindAddress = IPAddress.Loopback,
            DisconnectTimeoutMilliseconds = 2000,
        });
        using var Proxy = new DatagramFaultProxy(new IPEndPoint(IPAddress.Loopback, Server.LocalPort));
        var ClientId = Client.Connect(Proxy.EndPoint);
        string? ServerId = null;
        var ClientConnected = false;
        Pump(() => ClientConnected && ServerId is not null, () =>
        {
            foreach (var Event in Client.Poll())
            {
                ClientConnected |= Event.Kind == NetworkTransportEventKind.Connected;
            }

            foreach (var Event in Server.Poll())
            {
                if (Event.Kind == NetworkTransportEventKind.Connected)
                {
                    ServerId = Event.ConnectionId;
                }
            }
        });
        Check(ClientConnected, "UDP connection handshake must survive dropped, reordered, duplicated and corrupt datagrams");
        var Large = new string('x', 64 * 1024);
        for (var Index = 0; Index < 16; Index++)
        {
            Client.Send(ClientId, NetworkTransportMessageKind.Event, new JsonObject
            {
                ["index"] = Index,
                ["data"] = Index == 3 ? Large : new string('z', 2048),
            }, 1);
            Server.Send(ServerId!, NetworkTransportMessageKind.Event, new JsonObject
            {
                ["index"] = Index,
            }, 2);
        }

        var Received = new List<int>();
        var Returned = new List<int>();
        var FragmentReassembled = false;
        Pump(() => Received.Count == 16 && Returned.Count == 16, () =>
        {
            foreach (var Event in Server.Poll().Where(Event => Event.Kind == NetworkTransportEventKind.Message))
            {
                Received.Add(Event.Payload!["index"]!.GetValue<int>());
                FragmentReassembled |= Event.Payload["data"]!.GetValue<string>() == Large;
            }

            foreach (var Event in Client.Poll().Where(Event => Event.Kind == NetworkTransportEventKind.Message))
            {
                Returned.Add(Event.Payload!["index"]!.GetValue<int>());
            }
        });
        Check(Received.SequenceEqual(Enumerable.Range(0, 16)) && Returned.SequenceEqual(Enumerable.Range(0, 16)),
            "Reliable ordered messages must arrive exactly once and in order in both directions under datagram faults");
        Check(FragmentReassembled, "A 64 KiB JSON payload must be fragmented below MTU and reassembled without loss");
        Check(Proxy.Dropped > 0 && Proxy.Reordered > 0 && Proxy.Duplicated > 0 && Proxy.Corrupted > 0,
            "Fault test must actually drop, reorder, duplicate and corrupt real UDP datagrams");

        var TooLargeRejected = false;
        try
        {
            Client.Send(ClientId, NetworkTransportMessageKind.Event, new JsonObject
            {
                ["data"] = Large,
            }, 3, NetworkReliability.UnreliableSequenced);
        }
        catch (InvalidDataException)
        {
            TooLargeRejected = true;
        }

        Check(TooLargeRejected, "Nonfragmenting channels must reject oversized payloads before sending");
        var BudgetRejected = false;
        try
        {
            Client.Send(ClientId, NetworkTransportMessageKind.Event, new JsonObject
            {
                ["data"] = new string('x', 300 * 1024),
            });
        }
        catch (InvalidDataException)
        {
            BudgetRejected = true;
        }

        Check(BudgetRejected, "Payloads above the configured application budget must be rejected");

        var WrongThreadRejected = Task.Run(() =>
        {
            try
            {
                Client.Poll();
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }).GetAwaiter().GetResult();
        Check(WrongThreadRejected, "Transport polling from another thread must be rejected");

        Proxy.DropAll = true;
        var TimedOut = false;
        Pump(() => TimedOut, () =>
        {
            TimedOut |= Client.Poll().Any(Event => Event.Kind == NetworkTransportEventKind.Disconnected);
            Server.Poll();
        }, 5);
        Check(TimedOut, "A lost endpoint must produce a bounded connection timeout");
        Console.WriteLine($"[UDP faults] drop={Proxy.Dropped}, reorder={Proxy.Reordered}, duplicate={Proxy.Duplicated}, corrupt={Proxy.Corrupted}");
    }

    private static void ValidateProtocol(Action<bool, string> Check)
    {
        var Registry = ComponentRegistry.CreateBuiltin();
        var Schema = NetworkSample.CreateSchema();
        using var Session = new DedicatedServerSession(NetworkSample.CreateScene(), Registry, Schema);
        var Executions = 0;
        Session.CommandRouter.Register(Schema.FindRpc("sample.set-position")!, (Command, World, Tick) =>
        {
            Executions++;
            return NetworkCommandResult.AcceptedResult(Command.MessageId);
        });
        using var ServerTransport = new UdpNetworkTransport();
        using var Host = new DedicatedServerNetworkHost(Session, ServerTransport);
        using var First = new NetworkClientSession(Schema, new UdpNetworkTransport());
        using var Second = new NetworkClientSession(Schema, new UdpNetworkTransport());
        var EndPoint = new IPEndPoint(IPAddress.Loopback, ServerTransport.LocalPort);
        First.Connect(EndPoint);
        Second.Connect(EndPoint);
        Pump(() => First.IsReady && Second.IsReady && First.Replica.SnapshotId > 0 && Second.Replica.SnapshotId > 0, () =>
        {
            Host.Tick(RuntimeWorld.FixedDeltaSeconds);
            First.Poll();
            Second.Poll();
        });
        Check(First.ConnectionId != Second.ConnectionId && Host.ReadyConnectionCount == 2,
            "Concurrent clients must receive distinct server connection identities and independent snapshots");

        First.SendCommand("authority-1", "sample.set-position", 1, new JsonObject());
        JsonObject? Denial = null;
        Pump(() => Denial is not null, () =>
        {
            Host.Tick(RuntimeWorld.FixedDeltaSeconds);
            Denial = First.Poll().FirstOrDefault(Event => Event.MessageKind == NetworkTransportMessageKind.CommandResult)?.Payload;
            Second.Poll();
        });
        Check(Executions == 0 && Denial!["code"]!.GetValue<string>() == "RPC_AUTHORITY_DENIED",
            "An authority-required command must be denied when the host has no ownership policy");

        Host.AuthorizeCommand = _ => true;
        First.SendCommand("same-id", "sample.set-position", 2, new JsonObject());
        First.SendCommand("same-id", "sample.set-position", 2, new JsonObject());
        Second.SendCommand("same-id", "sample.set-position", 2, new JsonObject());
        var FirstResults = new List<JsonObject>();
        var SecondResults = new List<JsonObject>();
        Pump(() => FirstResults.Count == 2 && SecondResults.Count == 1, () =>
        {
            Host.Tick(RuntimeWorld.FixedDeltaSeconds);
            FirstResults.AddRange(First.Poll().Where(Event => Event.MessageKind == NetworkTransportMessageKind.CommandResult).Select(Event => Event.Payload!));
            SecondResults.AddRange(Second.Poll().Where(Event => Event.MessageKind == NetworkTransportMessageKind.CommandResult).Select(Event => Event.Payload!));
        });
        Check(Executions == 2 && FirstResults.Count(Result => Result["duplicate"]!.GetValue<bool>()) == 1,
            "Duplicate MessageId must execute once per connection without colliding with another client");
        Check(FirstResults.All(Result => Result["connectionId"]!.GetValue<string>() == First.ConnectionId) &&
            SecondResults.All(Result => Result["connectionId"]!.GetValue<string>() == Second.ConnectionId),
            "Command results must be routed only to their originating connection");

        var BadSchema = new NetworkSchemaCatalog();
        using var Mismatch = new NetworkClientSession(BadSchema, new UdpNetworkTransport());
        Mismatch.Connect(EndPoint);
        Pump(() => Mismatch.LastCode == "SCHEMA_MISMATCH", () =>
        {
            Host.Poll();
            First.Poll();
            Second.Poll();
            Mismatch.Poll();
        });
        Check(!Mismatch.IsReady && Host.ReadyConnectionCount == 2, "Schema mismatch must be rejected before accepting commands");

        using var Raw = new UdpNetworkTransport();
        var RawId = Raw.Connect(EndPoint);
        var RawConnected = false;
        Pump(() => RawConnected, () =>
        {
            Host.Poll();
            RawConnected |= Raw.Poll().Any(Event => Event.Kind == NetworkTransportEventKind.Connected);
        });
        Raw.Send(RawId, NetworkTransportMessageKind.Command,
            new NetworkCommandEnvelope(First.ConnectionId!, "spoof", "sample.set-position", 1, 0, new JsonObject()).ToJson(), 1);
        var Rejected = false;
        Pump(() => Rejected, () =>
        {
            Host.Tick(RuntimeWorld.FixedDeltaSeconds);
            First.Poll();
            Second.Poll();
            Rejected |= Raw.Poll().Any(Event => Event.Kind == NetworkTransportEventKind.Disconnected);
        });
        Check(Rejected && Executions == 2, "A command sent before the Schema handshake cannot mutate the World or impersonate a client");

        First.Disconnect();
        Second.Disconnect();
        Pump(() => Host.ReadyConnectionCount == 0, () =>
        {
            Host.Poll();
            First.Poll();
            Second.Poll();
        });
        Check(Host.Describe()["connections"]!.AsArray().Count == 0, "Disconnect must release connection baselines, commands and router caches");
    }

    private static void ValidateReplica(Action<bool, string> Check)
    {
        var Schema = NetworkSample.CreateSchema();
        using var Session = new DedicatedServerSession(NetworkSample.CreateScene(), ComponentRegistry.CreateBuiltin(), Schema);
        var Full = Session.BuildSnapshot();
        var Message = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["snapshotId"] = 1UL,
            ["baselineId"] = 0UL,
            ["serverTick"] = 1UL,
            ["snapshot"] = Full.ToJson(),
        };
        var Replica = new NetworkReplicaState(Schema);
        Replica.Apply(Message);
        var Before = Replica.ToJson();
        var Invalid = (JsonObject)Message.DeepClone();
        Invalid["snapshotId"] = 2UL;
        Invalid["snapshot"]!["entities"]![0]!["components"]![0]!["typeId"] = "unknown.type";
        var Rejected = false;
        try
        {
            Replica.Apply(Invalid);
        }
        catch (InvalidDataException)
        {
            Rejected = true;
        }

        Check(Rejected && Replica.SnapshotId == 1 && JsonNode.DeepEquals(Before, Replica.ToJson()),
            "Invalid snapshots must leave the committed replica and application ACK identity unchanged");
        var Delta = Session.BuildSnapshot(Full.CreateBaseline());
        Message["snapshotId"] = 2UL;
        Message["baselineId"] = 900UL;
        Message["snapshot"] = Delta.ToJson();
        Rejected = false;
        try
        {
            Replica.Apply(Message);
        }
        catch (InvalidDataException)
        {
            Rejected = true;
        }

        Check(Rejected && Replica.SnapshotId == 1, "A delta referencing an unacknowledged baseline must be rejected atomically");
    }

    private static Process StartChild(params string[] Arguments)
    {
        var Start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var Argument in Arguments)
        {
            Start.ArgumentList.Add(Argument);
        }

        return Process.Start(Start) ?? throw new InvalidOperationException("Could not start network child process");
    }

    private static void ValidateProcesses(Action<bool, string> Check)
    {
        using var Server = StartChild("server", "0", "8");
        try
        {
            var ReadyLine = Server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var Ready = JsonNode.Parse(ReadyLine ?? throw new IOException("Server closed before listening"))!.AsObject();
            var Port = Ready["port"]!.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var First = StartChild("client", Port, "player-a", "7");
            using var Second = StartChild("client", Port, "player-b", "11");
            try
            {
                var Completed = First.WaitForExit(12000) && Second.WaitForExit(12000);
                Check(Completed && First.ExitCode == 0 && Second.ExitCode == 0,
                    $"Independent client processes must complete UDP commands and property replication: {First.StandardError.ReadToEnd()} {Second.StandardError.ReadToEnd()}");
                var FirstOutput = JsonNode.Parse(First.StandardOutput.ReadToEnd())!.AsObject();
                var SecondOutput = JsonNode.Parse(Second.StandardOutput.ReadToEnd())!.AsObject();
                Check(FirstOutput["status"]!.GetValue<string>() == "pass" && FirstOutput["position"]!.GetValue<int>() == 7 &&
                    SecondOutput["status"]!.GetValue<string>() == "pass" && SecondOutput["position"]!.GetValue<int>() == 11,
                    "Both separate client processes must observe their exact authoritative position and duplicate result");
                Check(FirstOutput["connectionId"]!.GetValue<string>() != SecondOutput["connectionId"]!.GetValue<string>(),
                    "Cross-process clients must have distinct identities even when they use the same MessageId");
                Check(Server.WaitForExit(12000) && Server.ExitCode == 0, "The headless server must terminate cleanly");
                var Stopped = JsonNode.Parse(Server.StandardOutput.ReadToEnd())!.AsObject();
                Check(Stopped["executions"]!.GetValue<int>() == 2, "The headless server must execute only two unique commands from four received requests");
                Console.WriteLine($"[Cross-process] server={Server.Id}; clients={First.Id},{Second.Id}; executions=2; positions=7,11");
            }
            finally
            {
                if (!First.HasExited)
                {
                    First.Kill(true);
                }

                if (!Second.HasExited)
                {
                    Second.Kill(true);
                }
            }
        }
        finally
        {
            if (!Server.HasExited)
            {
                Server.Kill(true);
            }
        }
    }
}

/// <summary>Real UDP proxy: deterministic loss, corruption, duplicate datagrams and delayed reordering.</summary>
internal sealed class DatagramFaultProxy : IDisposable
{
    private readonly UdpClient Socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly IPEndPoint Server;
    private readonly CancellationTokenSource Lifetime = new();
    private readonly Task Worker;
    private int DropCount;
    private int ReorderCount;
    private int DuplicateCount;
    private int CorruptCount;
    public volatile bool DropAll;

    public DatagramFaultProxy(IPEndPoint Server)
    {
        this.Server = Server;
        Worker = Task.Run(Run);
    }

    public IPEndPoint EndPoint
    {
        get
        {
            return (IPEndPoint)Socket.Client.LocalEndPoint!;
        }
    }

    public int Dropped
    {
        get
        {
            return Volatile.Read(ref DropCount);
        }
    }

    public int Reordered
    {
        get
        {
            return Volatile.Read(ref ReorderCount);
        }
    }

    public int Duplicated
    {
        get
        {
            return Volatile.Read(ref DuplicateCount);
        }
    }

    public int Corrupted
    {
        get
        {
            return Volatile.Read(ref CorruptCount);
        }
    }

    private void Run()
    {
        IPEndPoint? Client = null;
        var PacketIndex = 0;
        var Timer = Stopwatch.StartNew();
        var Pending = new List<(byte[] Bytes, IPEndPoint Target, long Due)>();
        while (!Lifetime.IsCancellationRequested)
        {
            while (Socket.Available > 0)
            {
                var Source = new IPEndPoint(IPAddress.Any, 0);
                var Bytes = Socket.Receive(ref Source);
                var FromServer = Source.Equals(Server);
                if (!FromServer)
                {
                    Client = Source;
                }

                var Target = FromServer ? Client : Server;
                PacketIndex++;
                if (Target is null || DropAll || PacketIndex % 7 == 0)
                {
                    Interlocked.Increment(ref DropCount);
                    continue;
                }

                if (PacketIndex % 13 == 0 && Bytes.Length > 4)
                {
                    Bytes[^1] ^= 0x80;
                    Interlocked.Increment(ref CorruptCount);
                }

                if (PacketIndex % 5 == 0)
                {
                    Pending.Add((Bytes, Target, Timer.ElapsedMilliseconds + 25));
                    Interlocked.Increment(ref ReorderCount);
                    continue;
                }

                Socket.Send(Bytes, Bytes.Length, Target);
                if (PacketIndex % 11 == 0)
                {
                    Socket.Send(Bytes, Bytes.Length, Target);
                    Interlocked.Increment(ref DuplicateCount);
                }
            }

            foreach (var Packet in Pending.Where(Packet => Packet.Due <= Timer.ElapsedMilliseconds).ToArray())
            {
                if (!DropAll)
                {
                    Socket.Send(Packet.Bytes, Packet.Bytes.Length, Packet.Target);
                }

                Pending.Remove(Packet);
            }

            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        Lifetime.Cancel();
        Worker.GetAwaiter().GetResult();
        Socket.Dispose();
        Lifetime.Dispose();
    }
}
