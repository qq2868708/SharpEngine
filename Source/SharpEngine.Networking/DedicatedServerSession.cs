using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.Subsystems;

namespace SharpEngine.Networking;

public sealed class NetworkServerFrame
{
    internal NetworkServerFrame(ulong ServerTick, RuntimeFrame RuntimeFrame,
        IReadOnlyList<NetworkCommandResult> CommandResults)
    {
        this.ServerTick = ServerTick;
        this.RuntimeFrame = RuntimeFrame;
        this.CommandResults = Array.AsReadOnly(CommandResults.ToArray());
    }

    public ulong ServerTick
    {
        get;
    }

    public RuntimeFrame RuntimeFrame
    {
        get;
    }

    public IReadOnlyList<NetworkCommandResult> CommandResults
    {
        get;
    }
}

/// <summary>Transport-neutral authoritative host for one RuntimeInstance.</summary>
public sealed class DedicatedServerSession : IDisposable
{
    private readonly List<NetworkCommandEnvelope> PendingCommands = [];
    private bool Disposed;
    private readonly bool OwnsEngine;
    private RuntimeWorld? CurrentWorld;

    public DedicatedServerSession(SceneData Scene, ComponentRegistry Registry,
        NetworkSchemaCatalog Schema, AssetRegistry? Assets = null, EngineLifetime? Engine = null)
        : this(Schema, Assets, Engine)
    {
        try
        {
            StartWorld(Scene, Registry);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Creates the instance scope so modules can register before StartWorld initializes components.</summary>
    public DedicatedServerSession(NetworkSchemaCatalog Schema, AssetRegistry? Assets = null, EngineLifetime? Engine = null)
    {
        ArgumentNullException.ThrowIfNull(Schema);
        this.Schema = Schema;
        CommandRouter = new NetworkCommandRouter(Schema);
        Replication = new NetworkReplicationFrameBuilder(Schema);
        OwnsEngine = Engine is null;
        Instance = new RuntimeInstance(Assets, Engine);
    }

    public void StartWorld(SceneData Scene, ComponentRegistry Registry)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (CurrentWorld is not null)
        {
            throw new InvalidOperationException("SERVER_WORLD_ALREADY_STARTED: A session starts one World.");
        }
        CurrentWorld = Instance.CreateWorld(Scene, Registry);
    }

    public NetworkSchemaCatalog Schema
    {
        get;
    }

    public NetworkCommandRouter CommandRouter
    {
        get;
    }

    public NetworkReplicationFrameBuilder Replication
    {
        get;
    }

    public RuntimeInstance Instance
    {
        get;
    }

    public RuntimeWorld World
    {
        get
        {
            return CurrentWorld ?? throw new InvalidOperationException("SERVER_WORLD_NOT_STARTED: Call StartWorld after module registration.");
        }
    }

    public RuntimeWorld? ActiveWorld
    {
        get
        {
            return CurrentWorld;
        }
    }

    public ulong ServerTick
    {
        get;
        private set;
    }

    public void Enqueue(NetworkCommandEnvelope Command)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        World.CheckThread();
        ArgumentNullException.ThrowIfNull(Command);
        PendingCommands.Add(Command);
    }

    public NetworkServerFrame Tick(float DeltaSeconds)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        World.CheckThread();
        if (!float.IsFinite(DeltaSeconds) || DeltaSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DeltaSeconds));
        }
        if (OwnsEngine)
        {
            Instance.Engine.Update(DeltaSeconds);
        }
        var Tick = ServerTick + 1;
        var Results = new List<NetworkCommandResult>();
        foreach (var Command in PendingCommands.OrderBy(Command => Command.InputSequence)
                     .ThenBy(Command => Command.ConnectionId, StringComparer.Ordinal).ToArray())
        {
            Results.Add(CommandRouter.Execute(Command, World, Tick));
        }

        PendingCommands.Clear();
        Instance.Tick(DeltaSeconds);
        ServerTick = Tick;
        return new NetworkServerFrame(ServerTick, World.Frame, Results);
    }

    public void ForgetConnection(string ConnectionId)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        World.CheckThread();
        PendingCommands.RemoveAll(Command => Command.ConnectionId == ConnectionId);
        CommandRouter.ForgetConnection(ConnectionId);
    }

    public NetworkSnapshot BuildSnapshot(NetworkReplicationBaseline? Baseline = null,
        IReadOnlySet<string>? RelevantEntityIds = null)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return Replication.Build(World.Frame, Baseline, RelevantEntityIds);
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Instance.Dispose();
        CurrentWorld = null;
        Disposed = true;
    }
}
