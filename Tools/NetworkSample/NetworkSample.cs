using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Networking;

internal static class NetworkSample
{
    public static NetworkSchemaCatalog CreateSchema()
    {
        var Schema = new NetworkSchemaCatalog();
        Schema.RegisterComponent(new NetworkComponentDescriptor("engine.transform", 1,
        [
            new NetworkPropertyDescriptor("transform.position", "Position"),
            new NetworkPropertyDescriptor("transform.rotation", "Rotation"),
            new NetworkPropertyDescriptor("transform.scale", "Scale"),
        ]));
        Schema.RegisterRpc(new NetworkRpcDescriptor("sample.set-position", NetworkMessageKind.Command, NetworkReliability.ReliableOrdered));
        return Schema;
    }

    public static SceneData CreateScene()
    {
        var Scene = new SceneData
        {
            WorldId = "network-sample",
            Name = "Network Sample",
        };
        // Large enough to require multiple UDP datagrams for the initial snapshot.
        foreach (var EntityId in new[] { "player-a", "player-b" }.Concat(Enumerable.Range(0, 96).Select(Index => $"fixture-{Index}")))
        {
            var Entity = new EntityRecord
            {
                EntityId = EntityId,
                Name = EntityId,
            };
            Entity.Components.Add(new ComponentRecord
            {
                ComponentId = EntityId + ":transform",
                TypeId = "engine.transform",
                SchemaVersion = 1,
                State = new JsonObject
                {
                    ["Position"] = new JsonArray(0, 0, 0),
                    ["Rotation"] = new JsonArray(0, 0, 0),
                    ["Scale"] = new JsonArray(1, 1, 1),
                },
            });
            Scene.Entities.Add(Entity);
        }

        return Scene;
    }

    public static int Run(string[] Arguments)
    {
        try
        {
            if (Arguments.Length < 2)
            {
                Console.Error.WriteLine("server <port> [duration-seconds] | client <port> [player-a|player-b] [position-x] [IPv4-address]");
                return 2;
            }

            var Port = int.Parse(Arguments[1], CultureInfo.InvariantCulture);
            if (Arguments[0] == "server")
            {
                var Duration = Arguments.Length > 2 ? double.Parse(Arguments[2], CultureInfo.InvariantCulture) : 30;
                return RunServer(Port, Duration);
            }

            if (Arguments[0] == "client")
            {
                var EntityId = Arguments.Length > 2 ? Arguments[2] : "player-a";
                var Position = Arguments.Length > 3 ? int.Parse(Arguments[3], CultureInfo.InvariantCulture) : 7;
                var Address = Arguments.Length > 4 ? IPAddress.Parse(Arguments[4]) : IPAddress.Loopback;
                return RunClient(new IPEndPoint(Address, Port), EntityId, Position);
            }

            throw new ArgumentException("Expected server or client mode");
        }
        catch (Exception Error)
        {
            Console.Error.WriteLine(Error);
            return 1;
        }
    }

    private static int RunServer(int Port, double Duration)
    {
        var Registry = ComponentRegistry.CreateBuiltin();
        var Schema = CreateSchema();
        using var Session = new DedicatedServerSession(CreateScene(), Registry, Schema);
        var Executions = 0;
        Session.CommandRouter.Register(Schema.FindRpc("sample.set-position")!, (Command, World, Tick) =>
        {
            var EntityId = Command.Payload["entityId"]!.GetValue<string>();
            var Target = World.FindEntity(EntityId)!.GetComponent<TransformComponent>()!;
            var Mutation = World.Patch(Target.ComponentId, new JsonObject
            {
                ["Position"] = Command.Payload["position"]!.DeepClone(),
            }, World.Revision);
            _ = Mutation.Value;
            Executions++;
            return NetworkCommandResult.AcceptedResult(Command.MessageId);
        });
        using var Transport = new UdpNetworkTransport(new NetworkTransportOptions
        {
            LocalPort = Port,
        });
        using var Host = new DedicatedServerNetworkHost(Session, Transport)
        {
            // Fixture policy only. A game must resolve authenticated player ownership here.
            AuthorizeCommand = Command => Command.Payload["entityId"]?.GetValue<string>() is "player-a" or "player-b",
        };
        Console.WriteLine(new JsonObject
        {
            ["status"] = "listening",
            ["port"] = Transport.LocalPort,
        }.ToJsonString());
        Console.Out.Flush();
        var Timer = Stopwatch.StartNew();
        var NextTick = 0.0;
        while (Timer.Elapsed.TotalSeconds < Duration)
        {
            Host.Poll();
            if (Timer.Elapsed.TotalSeconds >= NextTick)
            {
                Host.Tick(RuntimeWorld.FixedDeltaSeconds);
                NextTick += 1.0 / 60;
            }

            Thread.Sleep(1);
        }

        Console.WriteLine(new JsonObject
        {
            ["status"] = "stopped",
            ["executions"] = Executions,
            ["serverTick"] = Session.ServerTick,
        }.ToJsonString());
        return 0;
    }

    private static int RunClient(IPEndPoint Server, string EntityId, int Position)
    {
        using var Transport = new UdpNetworkTransport();
        using var Client = new NetworkClientSession(CreateSchema(), Transport);
        Client.Connect(Server);
        var Timer = Stopwatch.StartNew();
        var Sent = false;
        var Results = 0;
        var Duplicate = false;
        while (Timer.Elapsed.TotalSeconds < 10)
        {
            foreach (var Event in Client.Poll())
            {
                if (Event.MessageKind == NetworkTransportMessageKind.CommandResult)
                {
                    var Value = Event.Payload!;
                    if (Value["accepted"]?.GetValue<bool>() != true)
                    {
                        throw new InvalidOperationException(Value.ToJsonString());
                    }

                    Results++;
                    Duplicate |= Value["duplicate"]!.GetValue<bool>();
                }
            }

            if (Client.IsReady && Client.Replica.SnapshotId > 0 && !Sent)
            {
                var Payload = new JsonObject
                {
                    ["entityId"] = EntityId,
                    ["position"] = new JsonArray(Position, 0, 0),
                };
                Client.SendCommand("same-message-id", "sample.set-position", 1, Payload);
                Client.SendCommand("same-message-id", "sample.set-position", 1, Payload);
                Sent = true;
            }

            var Entity = Client.Replica.ToJson()["entities"]!.AsArray()
                .Select(Node => Node!.AsObject()).SingleOrDefault(Entity => Entity["entityId"]!.GetValue<string>() == EntityId);
            var Actual = Entity?["components"]?[0]?["state"]?["transform.position"]?[0]?.GetValue<int>();
            if (Results == 2 && Duplicate && Actual == Position)
            {
                Console.WriteLine(new JsonObject
                {
                    ["status"] = "pass",
                    ["connectionId"] = Client.ConnectionId,
                    ["entityId"] = EntityId,
                    ["position"] = Actual,
                    ["snapshotId"] = Client.Replica.SnapshotId,
                    ["duplicate"] = Duplicate,
                }.ToJsonString());
                // Flush the queued application ACK before closing the transport.
                Client.Poll();
                return 0;
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException($"Client did not receive its result and replicated property: {Client.LastCode}");
    }
}
