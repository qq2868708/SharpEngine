using SharpEngine.Modules;

#if SHARPENGINE_SERVER
using System.Text.Json.Nodes;
using SharpEngine.Components;
using SharpEngine.Networking;
#endif

[assembly: SharpEngineModule("NetworkDedicatedServer", "Runtime")]

namespace NetworkDedicatedServer;

public sealed class NetworkDedicatedServerModule : ISharpEngineModule
{
    public void Startup(ModuleContext Context)
    {
#if SHARPENGINE_SERVER
        if (Context.Components is not INetworkServerRuntime Server)
        {
            throw new InvalidOperationException("NetworkDedicatedServer requires a Dedicated Server context");
        }

        Server.Schema.RegisterComponent(new NetworkComponentDescriptor("engine.transform", 1,
        [
            new NetworkPropertyDescriptor("transform.position", "Position"),
            new NetworkPropertyDescriptor("transform.rotation", "Rotation"),
            new NetworkPropertyDescriptor("transform.scale", "Scale"),
        ]));
        var SetPosition = new NetworkRpcDescriptor("sample.set-position",
            NetworkMessageKind.Command, NetworkReliability.ReliableOrdered);
        Server.RegisterCommand(SetPosition, SetPositionCommand);
        Server.AuthorizeCommand = Command => Command.Payload["entityId"]?.GetValue<string>() is { } EntityId &&
            (EntityId is "player-a" or "player-b") && Server.RuntimeWorld?.FindEntity(EntityId) is not null;
#endif
    }

    public void Shutdown()
    {
    }

#if SHARPENGINE_SERVER
    private static NetworkCommandResult SetPositionCommand(NetworkCommandEnvelope Command,
        RuntimeWorld World, ulong ServerTick)
    {
        var EntityId = Command.Payload["entityId"]?.GetValue<string>() ??
            throw new InvalidDataException("entityId is required");
        var Position = Command.Payload["position"]?.AsArray() ??
            throw new InvalidDataException("position is required");
        if (Position.Count != 3)
        {
            throw new InvalidDataException("position must contain three values");
        }

        var Entity = World.FindEntity(EntityId) ??
            throw new InvalidDataException("entityId does not exist");
        var Transform = Entity.GetComponent<TransformComponent>() ??
            throw new InvalidDataException("entity has no transform");
        _ = World.Patch(Transform.ComponentId, new JsonObject
        {
            ["Position"] = Position.DeepClone(),
        }, World.Revision).Value;
        return NetworkCommandResult.AcceptedResult(Command.MessageId);
    }
#endif
}
