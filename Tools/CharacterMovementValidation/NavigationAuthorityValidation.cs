using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Components;

namespace CharacterMovementValidation;

internal static class NavigationAuthorityValidation
{
    internal static void Run(ComponentRegistry Registry, Action<bool, string> Check)
    {
        var Scene = new SceneData();
        var Surface = Scene.CreateEntity(Registry, "Surface");
        var SurfaceComponent = Scene.AddComponent(Registry, Surface.EntityId, "engine.nav-surface").Single();
        var Agent = Scene.CreateEntity(Registry, "Navigation authority probe");
        Scene.AddComponent(Registry, Agent.EntityId, "engine.nav-agent", new JsonObject
        {
            ["Surface"] = SurfaceComponent.ComponentId,
            ["Destination"] = new JsonArray(4f, 0f, 0f),
            ["HasDestination"] = true,
        });
        Scene.AddComponent(Registry, Agent.EntityId, Registry.Get(typeof(ProbeMovementAuthorityComponent)).TypeId);
        using var World = new RuntimeWorld(Scene, Registry);
        var Entity = World.FindEntity(Agent.EntityId)!;
        var Authority = Entity.GetComponent<ProbeMovementAuthorityComponent>()!;
        var Transform = Entity.GetComponent<TransformComponent>()!;
        World.Step();
        Check(Transform.Position == Vector3.Zero && World.Navigation.Agents.Single().Code == "NAV_MOVEMENT_AUTHORITY",
            "Navigation cannot move an entity owned by an active movement authority");
        World.Step();
        Check(Transform.Position == Vector3.Zero && World.Diagnostics.Count == 0,
            "Suspended navigation stays stable without recurring error diagnostics");
        Authority.Enabled = false;
        World.Step();
        Check(Transform.Position.X > 0 && World.Navigation.Agents.Single().Status == NavAgentStatus.Moving,
            "Disabling the authority lets navigation resume and replan");
        var Before = Transform.Position;
        Authority.Enabled = true;
        Check(World.Navigation.Agents.Single().Code == "NAV_MOVEMENT_AUTHORITY",
            "Published activation changes immediately expose navigation ownership");
        World.Step();
        Check(Transform.Position == Before, "Re-enabling the authority stops navigation writes again");
        World.RemoveComponent(Entity.EntityId, Authority.ComponentId);
        World.Step();
        Check(Transform.Position.X > Before.X, "Removing the authority restores standalone NavAgent movement");
    }
}
