using System.Text.Json.Nodes;
using System.Net;
using SharpEngine.Components;
using SharpEngine.Networking;

if (args.Length > 0)
{
    Environment.ExitCode = NetworkSample.Run(args);
    return;
}

var Assertions = 0;
void Check(bool Condition, string Message)
{
    Assertions++;
    if (!Condition)
    {
        throw new InvalidOperationException(Message);
    }
}

var Registry = ComponentRegistry.CreateBuiltin();
var Schema = new NetworkSchemaCatalog();
Schema.RegisterComponent(new NetworkComponentDescriptor("engine.transform", 1,
[
    new NetworkPropertyDescriptor("transform.position", "Position", Reliability: NetworkReliability.UnreliableSequenced),
    new NetworkPropertyDescriptor("transform.rotation", "Rotation", Reliability: NetworkReliability.UnreliableSequenced),
    new NetworkPropertyDescriptor("transform.scale", "Scale"),
]));
var SetPosition = new NetworkRpcDescriptor("game.set-position", NetworkMessageKind.Command,
    NetworkReliability.ReliableOrdered);
Schema.RegisterRpc(SetPosition);
var Fingerprint = Schema.Fingerprint();
Check(Fingerprint.Length == 64 && Fingerprint == Schema.Fingerprint(), "Schema fingerprint must be stable");

var Scene = new SceneData { WorldId = "network-fixture", Name = "Network Fixture" };
var Entity = new EntityRecord { EntityId = "player", Name = "Player" };
Entity.Components.Add(new ComponentRecord
{
    ComponentId = "player-transform",
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
Scene.Validate(Registry);

using var Server = new DedicatedServerSession(Scene, Registry, Schema);
Server.CommandRouter.Register(SetPosition, (Command, World, ServerTick) =>
{
    var EntityId = Command.Payload["entityId"]?.GetValue<string>() ?? throw new InvalidDataException("entityId is required");
    var Position = Command.Payload["position"]?.DeepClone() ?? throw new InvalidDataException("position is required");
    var Target = World.FindEntity(EntityId) ?? throw new InvalidDataException("entity does not exist");
    var Transform = Target.GetComponent<TransformComponent>() ?? throw new InvalidDataException("transform does not exist");
    var Mutation = World.Patch(Transform.ComponentId, new JsonObject { ["Position"] = Position }, World.Revision);
    if (!Mutation.IsCompleted)
    {
        return NetworkCommandResult.Rejected(Command.MessageId, "COMMAND_PENDING", "The command crossed a runtime phase boundary", true);
    }

    _ = Mutation.Value;
    return NetworkCommandResult.AcceptedResult(Command.MessageId);
});

var Initial = Server.BuildSnapshot();
Check(Initial.IsFull && Initial.ToJson()["entities"]!.AsArray().Count == 1,
    "The first network snapshot must be a full snapshot");
var Baseline = Initial.CreateBaseline();
var Command = new NetworkCommandEnvelope("player-connection", "move-1", "game.set-position", 1, 1,
    new JsonObject { ["entityId"] = "player", ["position"] = new JsonArray(5, 0, 0) });
Server.Enqueue(Command);
var FirstFrame = Server.Tick(RuntimeWorld.FixedDeltaSeconds);
Check(FirstFrame.ServerTick == 1 && FirstFrame.CommandResults.Single().Accepted,
    "The server must process a command at the next authoritative tick");
var Delta = Server.BuildSnapshot(Baseline);
var DeltaJson = Delta.ToJson();
Check(!Delta.IsFull && DeltaJson["updated"]!.AsArray().Count == 1,
    "A changed component must produce one entity delta");
var ChangedEntity = DeltaJson["updated"]![0]!.AsObject();
var ChangedComponent = ChangedEntity["changedComponents"]![0]!.AsObject();
Check(ChangedComponent["properties"]!["transform.position"]!.ToJsonString() == "[5,0,0]",
    "Property deltas must use stable property ids and preserve values");

Server.Enqueue(Command);
var DuplicateFrame = Server.Tick(RuntimeWorld.FixedDeltaSeconds);
Check(DuplicateFrame.CommandResults.Single().Duplicate && DuplicateFrame.CommandResults.Single().Accepted,
    "A duplicate message id must return the cached result without reapplying the command");

var Unknown = new NetworkCommandEnvelope("player-connection", "unknown-1", "game.missing", 2, 2, new JsonObject());
Server.Enqueue(Unknown);
var UnknownFrame = Server.Tick(RuntimeWorld.FixedDeltaSeconds);
Check(!UnknownFrame.CommandResults.Single().Accepted && UnknownFrame.CommandResults.Single().Code == "RPC_UNKNOWN",
    "Unknown commands must be rejected with a structured code");

var Filtered = Server.BuildSnapshot(null, new HashSet<string>(StringComparer.Ordinal));
Check(Filtered.ToJson()["entities"]!.AsArray().Count == 0,
    "Relevancy filtering must be applied before the snapshot is built");

using var ServerTransport = new UdpNetworkTransport(new NetworkTransportOptions
{
    BindAddress = IPAddress.Loopback,
    LocalPort = 0,
});
using var ClientTransport = new UdpNetworkTransport(new NetworkTransportOptions
{
    BindAddress = IPAddress.Loopback,
    LocalPort = 0,
});
using var NetworkHost = new DedicatedServerNetworkHost(Server, ServerTransport)
{
    AuthorizeCommand = _ => true,
};
using var Client = new NetworkClientSession(Schema, ClientTransport);
Client.Connect(new IPEndPoint(IPAddress.Loopback, ServerTransport.LocalPort));
var Connected = false;
for (var Iteration = 0; Iteration < 200 && !Connected; Iteration++)
{
    NetworkHost.Tick(RuntimeWorld.FixedDeltaSeconds);
    Client.Poll();
    Connected = Client.IsReady && NetworkHost.ReadyConnectionCount == 1;
    Thread.Sleep(1);
}

Check(Connected && NetworkHost.ReadyConnectionCount == 1,
    "A C# client must complete the UDP Schema handshake with the Dedicated Server host");
for (var Iteration = 0; Iteration < 200 && Client.Replica.SnapshotId == 0; Iteration++)
{
    NetworkHost.Tick(RuntimeWorld.FixedDeltaSeconds);
    Client.Poll();
    Thread.Sleep(1);
}

Check(Client.Replica.SnapshotId == 1 && Client.Replica.ToJson()["entities"]!.AsArray().Count == 1,
    "The client must apply and acknowledge the first authoritative snapshot");
Client.SendCommand("move-transport", "game.set-position", 3,
    new JsonObject { ["entityId"] = "player", ["position"] = new JsonArray(9, 0, 0) });
var CommandResultReceived = false;
for (var Iteration = 0; Iteration < 200 && !CommandResultReceived; Iteration++)
{
    NetworkHost.Tick(RuntimeWorld.FixedDeltaSeconds);
    var ClientEvents = Client.Poll();
    CommandResultReceived = ClientEvents.Any(Event => Event.MessageKind == NetworkTransportMessageKind.CommandResult);
    Thread.Sleep(1);
}

Check(CommandResultReceived, "The client command must travel over UDP and return a CommandResult");
for (var Iteration = 0; Iteration < 200; Iteration++)
{
    NetworkHost.Tick(RuntimeWorld.FixedDeltaSeconds);
    Client.Poll();
    var CurrentPosition = Client.Replica.ToJson()["entities"]![0]!["components"]![0]!["state"]!["transform.position"]![0]!.GetValue<int>();
    if (CurrentPosition == 9)
    {
        break;
    }
    Thread.Sleep(1);
}

Check(Client.Replica.ToJson()["entities"]![0]!["components"]![0]!["state"]!["transform.position"]![0]!.GetValue<int>() == 9,
    "The client replica must receive the changed authoritative property over UDP");

TransportValidation.Run(Check);

Console.WriteLine($"[NetworkValidation] PASS: {Assertions} assertions; schema={Fingerprint}; revision={FirstFrame.RuntimeFrame.Revision}");
