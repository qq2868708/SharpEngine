using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Components;

namespace SharpEngine.Spatial;

public sealed record NavPathResult(bool Success, string Code, IReadOnlyList<Vector3> Points);

public sealed record NavAgentObservation(string EntityId, string ComponentId, NavAgentStatus Status,
    string Code, Vector3 Position, Vector3 Velocity, float RemainingDistance, IReadOnlyList<Vector3> Path);

/// <summary>Planar XZ navigation. Configuration is authoritative; paths are derived.</summary>
public sealed class NavigationSnapshot
{
    private readonly SpatialScene Scene;
    private readonly CollisionSnapshot Collision;
    public ulong Revision
    {
        get;
    }
    public IReadOnlyList<NavAgentObservation> Agents
    {
        get;
    }

    internal NavigationSnapshot(SpatialScene Scene, ulong Revision, IReadOnlyList<NavAgentObservation> Agents)
    {
        this.Scene = Scene;
        this.Revision = Revision;
        this.Agents = Agents;
        Collision = new(Scene, Revision);
    }

    public NavPathResult FindPath(string SurfaceComponentId, Vector3 Start, Vector3 Destination,
        float Radius = 0.3f, float Height = 1.8f, string? IgnoreEntityId = null)
    {
        var Planner = CreatePlanner(SurfaceComponentId, Radius, Height, IgnoreEntityId);
        return Planner.FindPath(Start, Destination);
    }

    internal NavPlanner CreatePlanner(string SurfaceComponentId, float Radius, float Height, string? IgnoreEntityId)
    {
        return new(Scene, Collision, SurfaceComponentId, Radius, Height, IgnoreEntityId);
    }

    public JsonObject ToJson()
    {
        return new()
        {
            ["schemaVersion"] = 1,
            ["context"] = "runtime",
            ["revision"] = Revision,
            ["agents"] = new JsonArray(Agents.Select(Agent => (JsonNode)new JsonObject
            {
                ["entityId"] = Agent.EntityId,
                ["componentId"] = Agent.ComponentId,
                ["status"] = Agent.Status.ToString(),
                ["code"] = Agent.Code,
                ["position"] = SpatialScene.Json(Agent.Position),
                ["velocity"] = SpatialScene.Json(Agent.Velocity),
                ["remainingDistance"] = Agent.RemainingDistance,
                ["path"] = new JsonArray(Agent.Path.Select(Point => (JsonNode)SpatialScene.Json(Point)).ToArray()),
            }).ToArray()),
        };
    }
}

internal sealed class NavPlanner
{
    private readonly Matrix4x4 Matrix;
    private readonly Matrix4x4 Inverse;
    private readonly Vector2 Size;
    private readonly Vector2 Margin;
    private readonly int Columns;
    private readonly int Rows;
    private readonly ShapeGeometry[] Obstacles = [];
    private readonly float Radius;
    private readonly float Height;
    private readonly string? Error;
    internal readonly string Fingerprint = "";

    internal NavPlanner(SpatialScene Scene, CollisionSnapshot Collision, string SurfaceComponentId,
        float Radius, float Height, string? IgnoreEntityId)
    {
        this.Radius = Radius;
        this.Height = Height;
        if (!float.IsFinite(Radius) || !float.IsFinite(Height) || Radius <= 0 || Height < Radius * 2)
        {
            Error = "NAV_AGENT_SHAPE";
            return;
        }
        var Owner = Scene.Data.Entities.FirstOrDefault(Entity => Entity.Components.Any(Component => Component.ComponentId == SurfaceComponentId));
        var Surface = Owner?.Components.Single(Component => Component.ComponentId == SurfaceComponentId);
        if (Owner is null || Surface is null || Surface.TypeId != "engine.nav-surface" || !Surface.Enabled || !Scene.Active.Contains(Owner.EntityId))
        {
            Error = "NAV_SURFACE_UNAVAILABLE";
            return;
        }
        Scene.Registry.ValidateState(Scene.Registry.Get(typeof(NavSurfaceComponent)), Surface.State);
        Matrix = Scene.Matrices[Owner.EntityId];
        var Invertible = Matrix4x4.Invert(Matrix, out Inverse);
        if (!Invertible || !SpatialScene.IsFinite(Inverse) || MathF.Abs(Matrix.M12) > 0.0001f || MathF.Abs(Matrix.M32) > 0.0001f || Matrix.M22 <= 0)
        {
            Error = "NAV_SURFACE_MUST_BE_HORIZONTAL";
            return;
        }
        var Values = Surface.State["Size"]!.AsArray();
        Size = new(Values[0]!.GetValue<float>(), Values[1]!.GetValue<float>());
        var CellSize = Surface.State["CellSize"]!.GetValue<float>();
        Columns = (int)MathF.Ceiling(Size.X / CellSize);
        Rows = (int)MathF.Ceiling(Size.Y / CellSize);
        Margin = Radius * new Vector2(new Vector3(Inverse.M11, Inverse.M21, Inverse.M31).Length(),
            new Vector3(Inverse.M13, Inverse.M23, Inverse.M33).Length());
        Obstacles = Collision.Obstacles(Surface.State["ObstacleMask"]!.GetValue<uint>(), IgnoreEntityId);
        Fingerprint = Surface.ToJson().ToJsonString() + SpatialScene.Json(Matrix).ToJsonString() +
            string.Join('|', Obstacles.Select(Shape => Shape.Fingerprint));
    }

    internal NavPathResult FindPath(Vector3 Start, Vector3 Destination)
    {
        if (Error is not null)
        {
            return Failure(Error);
        }
        SpatialScene.RequireFinite(Start);
        SpatialScene.RequireFinite(Destination);
        var LocalStart = Vector3.Transform(Start, Inverse);
        var LocalDestination = Vector3.Transform(Destination, Inverse);
        if (MathF.Abs(Start.Y - Matrix.M42) > 0.01f || MathF.Abs(Destination.Y - Matrix.M42) > 0.01f)
        {
            return Failure("NAV_ENDPOINT_OFF_PLANE");
        }
        if (!Inside(LocalStart) || !Inside(LocalDestination))
        {
            return Failure("NAV_ENDPOINT_OUTSIDE_SURFACE");
        }
        if (!Clear(Start, Start) || !Clear(Destination, Destination))
        {
            return Failure("NAV_ENDPOINT_BLOCKED");
        }
        if (Clear(Start, Destination))
        {
            return Success([Start, Destination]);
        }
        var StartIndex = Cell(LocalStart);
        var GoalIndex = Cell(LocalDestination);
        if (!Inside(Vector3.Transform(Center(StartIndex), Inverse)) || !Inside(Vector3.Transform(Center(GoalIndex), Inverse)) ||
            !Clear(Start, Center(StartIndex)) || !Clear(Center(GoalIndex), Destination))
        {
            return Failure("NAV_CELL_CONNECTION_BLOCKED");
        }
        var Costs = Enumerable.Repeat(float.PositiveInfinity, Columns * Rows).ToArray();
        var Previous = Enumerable.Repeat(-1, Costs.Length).ToArray();
        var Closed = new bool[Costs.Length];
        var Queue = new PriorityQueue<int, (float Score, int Index)>();
        var Edges = new Dictionary<(int, int), bool>();
        Costs[StartIndex] = 0;
        Queue.Enqueue(StartIndex, (0, StartIndex));
        while (Queue.Count > 0)
        {
            var Current = Queue.Dequeue();
            if (Closed[Current])
            {
                continue;
            }
            if (Current == GoalIndex)
            {
                var Indices = new List<int>();
                for (var Index = GoalIndex; Index != -1; Index = Previous[Index])
                {
                    Indices.Add(Index);
                }
                Indices.Reverse();
                var Points = new List<Vector3>
                {
                    Start,
                };
                Points.AddRange(Indices.Select(Center));
                Points.Add(Destination);
                return Success(Simplify(Points));
            }
            Closed[Current] = true;
            foreach (var Next in Neighbors(Current))
            {
                if (Closed[Next])
                {
                    continue;
                }
                var Key = (Math.Min(Current, Next), Math.Max(Current, Next));
                var Known = Edges.TryGetValue(Key, out var Passable);
                if (!Known)
                {
                    Passable = Clear(Center(Current), Center(Next));
                    Edges.Add(Key, Passable);
                }
                if (!Passable)
                {
                    continue;
                }
                var Cost = Costs[Current] + Vector3.Distance(Center(Current), Center(Next));
                if (Cost >= Costs[Next])
                {
                    continue;
                }
                Costs[Next] = Cost;
                Previous[Next] = Current;
                Queue.Enqueue(Next, (Cost + Vector3.Distance(Center(Next), Center(GoalIndex)), Next));
            }
        }
        return Failure("NAV_NO_PATH");
    }

    private List<Vector3> Simplify(List<Vector3> Points)
    {
        var Result = new List<Vector3>
        {
            Points[0],
        };
        var Current = 0;
        while (Current < Points.Count - 1)
        {
            var Next = Points.Count - 1;
            while (Next > Current + 1 && !Clear(Points[Current], Points[Next]))
            {
                Next--;
            }
            Result.Add(Points[Next]);
            Current = Next;
        }
        return Result;
    }

    internal bool Clear(Vector3 Start, Vector3 End)
    {
        Vector3 Support(Vector3 Direction)
        {
            var Base = Vector3.Dot(Start, Direction) >= Vector3.Dot(End, Direction) ? Start : End;
            var Cap = Direction.Y >= 0 ? Height - Radius : Radius;
            // Keep a small ground clearance so a collider ending at the walk plane
            // does not block the agent solely through numerical contact.
            return Base + Vector3.UnitY * (Cap + 0.002f) + Vector3.Normalize(Direction) * Radius;
        }
        return Obstacles.All(Shape => !Shape.Intersects(Support));
    }

    private bool Inside(Vector3 Point)
    {
        return MathF.Abs(Point.X) <= Size.X / 2 - Margin.X && MathF.Abs(Point.Z) <= Size.Y / 2 - Margin.Y;
    }

    private int Cell(Vector3 Point)
    {
        var X = Math.Clamp((int)((Point.X / Size.X + 0.5f) * Columns), 0, Columns - 1);
        var Z = Math.Clamp((int)((Point.Z / Size.Y + 0.5f) * Rows), 0, Rows - 1);
        return Z * Columns + X;
    }

    private Vector3 Center(int Index)
    {
        return Vector3.Transform(new Vector3(((Index % Columns + 0.5f) / Columns - 0.5f) * Size.X,
            0, ((Index / Columns + 0.5f) / Rows - 0.5f) * Size.Y), Matrix);
    }

    private IEnumerable<int> Neighbors(int Index)
    {
        var X = Index % Columns;
        var Z = Index / Columns;
        foreach (var Offset in new[]
        {
            (-1, 0),
            (1, 0),
            (0, -1),
            (0, 1),
        })
        {
            var NextX = X + Offset.Item1;
            var NextZ = Z + Offset.Item2;
            if (NextX < 0 || NextX >= Columns || NextZ < 0 || NextZ >= Rows)
            {
                continue;
            }
            var Next = NextZ * Columns + NextX;
            var Local = Vector3.Transform(Center(Next), Inverse);
            if (Inside(Local))
            {
                yield return Next;
            }
        }
    }

    private static NavPathResult Failure(string Code) { return new(false, Code, Array.Empty<Vector3>()); }
    private static NavPathResult Success(IEnumerable<Vector3> Points) { return new(true, "NAV_PATH_COMPLETE", Array.AsReadOnly(Points.ToArray())); }
}
