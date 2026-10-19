using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Spatial;
using SharpEngine.Mathematics;

namespace SharpEngine.Components;

public sealed partial class RuntimeWorld
{
    // Reconstructable path caches belong to this runtime world and never enter scene data.
    private readonly Dictionary<string, AgentPathState> AgentPaths = new(StringComparer.Ordinal);

    public CollisionSnapshot Collision
    {
        get
        {
            CheckThread();
            return new(new SpatialScene(Snapshot(), Registry), Revision);
        }
    }

    public NavigationSnapshot Navigation
    {
        get
        {
            CheckThread();
            var Scene = new SpatialScene(Snapshot(), Registry);
            var Observations = new List<NavAgentObservation>();
            foreach (var Entity in Scene.Data.Entities)
            {
                foreach (var Component in Entity.Components.Where(Component => Component.TypeId == "engine.nav-agent"))
                {
                    var Position = Scene.Matrices[Entity.EntityId].Translation;
                    var Active = Component.Enabled && Scene.Active.Contains(Entity.EntityId) && Component.State["HasDestination"]!.GetValue<bool>();
                    var Found = AgentPaths.TryGetValue(Component.ComponentId, out var State);
                    if (Active && Found && State!.CommittedConfiguration == Configuration(Component.State) && Vector3.DistanceSquared(State.Position, Position) <= 1e-8f)
                    {
                        Observations.Add(State.Observation(Entity.EntityId, Component.ComponentId, Position));
                    }
                    else
                    {
                        Observations.Add(new(Entity.EntityId, Component.ComponentId,
                            Active ? NavAgentStatus.Unavailable : NavAgentStatus.Idle,
                            Active ? "NAV_PATH_PENDING" : "NAV_IDLE", Position, Vector3.Zero, 0, Array.Empty<Vector3>()));
                    }
                }
            }
            return new(Scene, Revision, Array.AsReadOnly(Observations.OrderBy(Agent => Agent.ComponentId, StringComparer.Ordinal).ToArray()));
        }
    }

    private static string Configuration(JsonObject State) { return State.ToJsonString(); }

    private void AdvanceNavigation(float DeltaSeconds, IEnumerable<NavAgentComponent> Eligible)
    {
        var Agents = Eligible.OrderBy(Agent => Agent.ComponentId, StringComparer.Ordinal).ToArray();
        if (Agents.Length == 0)
        {
            AgentPaths.Clear();
            return;
        }
        var Scene = new SpatialScene(Capture(), Registry);
        NavigationSnapshot Navigation;
        try
        {
            Navigation = new(Scene, Revision, Array.Empty<NavAgentObservation>());
        }
        catch (Exception Error)
        {
            Report("NAVIGATION_GEOMETRY_INVALID", "", "Navigation", Error);
            AgentPaths.Clear();
            return;
        }
        var Changes = new List<(SharpEntity Entity, Vector3 Position)>();
        var LiveIds = Agents.Select(Agent => Agent.ComponentId).ToHashSet(StringComparer.Ordinal);
        foreach (var Id in AgentPaths.Keys.Where(Id => !LiveIds.Contains(Id)).ToArray())
        {
            AgentPaths.Remove(Id);
        }
        foreach (var Agent in Agents)
        {
            var Position = Scene.Matrices[Agent.Entity.EntityId].Translation;
            var State = AgentPaths.GetValueOrDefault(Agent.ComponentId) ?? new AgentPathState();
            AgentPaths[Agent.ComponentId] = State;
            try
            {
                var ConfigurationState = Registry.Read(Agent);
                Registry.ValidateState(Registry.Get(typeof(NavAgentComponent)), ConfigurationState);
                State.CommittedConfiguration = Configuration(ConfigurationState);
                if (!Agent.HasDestination)
                {
                    State.Reset(Position, NavAgentStatus.Idle, "NAV_IDLE");
                    continue;
                }
                var Planner = Navigation.CreatePlanner(Agent.Surface?.ComponentId ?? "", Agent.Radius, Agent.Height, Agent.Entity.EntityId);
                var Key = State.CommittedConfiguration + Planner.Fingerprint;
                if (State.Key != Key || Vector3.DistanceSquared(State.Position, Position) > 1e-8f)
                {
                    var Path = Planner.FindPath(Position, Agent.Destination);
                    var Status = Path.Success ? NavAgentStatus.Moving :
                        Path.Code is "NAV_SURFACE_UNAVAILABLE" or "NAV_SURFACE_MUST_BE_HORIZONTAL" ? NavAgentStatus.Unavailable : NavAgentStatus.Blocked;
                    State.Reset(Position, Status, Path.Code);
                    State.Key = Key;
                    State.Path = Path.Points.ToArray();
                    State.Next = 1;
                }
                if (State.Path.Length < 2)
                {
                    continue;
                }
                var Remaining = State.Distance(Position);
                var Budget = MathF.Min(Agent.Speed * DeltaSeconds, MathF.Max(0, Remaining - Agent.StoppingDistance));
                var NextPosition = Position;
                while (Budget > 0 && State.Next < State.Path.Length)
                {
                    var Target = State.Path[State.Next];
                    var Distance = Vector3.Distance(NextPosition, Target);
                    if (Distance <= Budget)
                    {
                        NextPosition = Target;
                        State.Next++;
                        // Stop at the corner for this step, so the committed
                        // straight movement segment cannot cut across a bend.
                        Budget = 0;
                    }
                    else
                    {
                        NextPosition += (Target - NextPosition) * (Budget / Distance);
                        Budget = 0;
                    }
                }
                State.Position = NextPosition;
                State.Velocity = (NextPosition - Position) / DeltaSeconds;
                State.RemainingDistance = State.Distance(NextPosition);
                State.Status = State.RemainingDistance <= Agent.StoppingDistance + 0.0001f ? NavAgentStatus.Arrived : NavAgentStatus.Moving;
                State.Code = State.Status == NavAgentStatus.Arrived ? "NAV_ARRIVED" : "NAV_MOVING";
                Changes.Add((Agent.Entity, NextPosition));
            }
            catch (Exception Error)
            {
                State.Reset(Position, NavAgentStatus.Unavailable, "NAVIGATION_FAILED");
                Report("NAVIGATION_FAILED", Agent.ComponentId, "Navigation", Error);
            }
        }
        // Every agent observes the same phase input; commit moves only after planning all agents.
        foreach (var Change in Changes.OrderBy(Change => Depth(Change.Entity)))
        {
            var ParentMatrix = Change.Entity.Parent is null ? Matrix4x4.Identity : LiveMatrix(Change.Entity.Parent);
            var Invertible = Matrix4x4.Invert(ParentMatrix, out var InverseParent);
            if (!Invertible)
            {
                throw new InvalidOperationException("NAV_PARENT_TRANSFORM: parent matrix is singular.");
            }
            Change.Entity.GetComponent<TransformComponent>()!.Position = Vector3.Transform(Change.Position, InverseParent);
        }
    }

    private static int Depth(SharpEntity Entity)
    {
        return Entity.Parent is null ? 0 : 1 + Depth(Entity.Parent);
    }

    private static Matrix4x4 LiveMatrix(SharpEntity Entity)
    {
        var Transform = Entity.GetComponent<TransformComponent>()!;
        var Local = TransformMath.Trs(Transform.Position, Transform.Rotation, Transform.Scale);
        return Entity.Parent is null ? Local : Local * LiveMatrix(Entity.Parent);
    }

    private sealed class AgentPathState
    {
        internal string Key = "";
        internal string CommittedConfiguration = "";
        internal Vector3 Position;
        internal Vector3 Velocity;
        internal float RemainingDistance;
        internal NavAgentStatus Status;
        internal string Code = "NAV_IDLE";
        internal Vector3[] Path = [];
        internal int Next;

        internal void Reset(Vector3 Position, NavAgentStatus Status, string Code)
        {
            this.Position = Position;
            this.Status = Status;
            this.Code = Code;
            Key = "";
            Velocity = Vector3.Zero;
            RemainingDistance = 0;
            Path = [];
            Next = 0;
        }

        internal float Distance(Vector3 Position)
        {
            var Result = Next < Path.Length ? Vector3.Distance(Position, Path[Next]) : 0;
            for (var Index = Next; Index < Path.Length - 1; Index++)
            {
                Result += Vector3.Distance(Path[Index], Path[Index + 1]);
            }
            return Result;
        }

        internal NavAgentObservation Observation(string EntityId, string ComponentId, Vector3 Position)
        {
            return new(EntityId, ComponentId, Status, Code, Position, Velocity, Distance(Position), Array.AsReadOnly(Path.ToArray()));
        }
    }
}
