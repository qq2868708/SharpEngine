using System.Text.Json.Nodes;
using SharpEngine.UI;

internal static class UiContextMenuRoutingValidation
{
    internal static void Run(Action<bool, string> Check)
    {
        var Routing = new UiContextMenuRouter();
        var Calls = new List<string>();
        var ChildConsumes = false;
        using var Parent = Routing.Register("parent", Event =>
        {
            Calls.Add("parent");
            return true;
        });
        var Child = Routing.Register("child", Event =>
        {
            Calls.Add("child");
            Check(Event.HostId == "window-a" && Event.Payload == "domain-owned",
                "Generic routing preserves host and opaque feature payload");
            return ChildConsumes;
        });
        var Event = new UiContextMenuEvent("window-a", 10, 20, "domain-owned");
        Check(Routing.Bubble(["child", "parent"], Event) && Calls.SequenceEqual(["child", "parent"]),
            "Unconsumed context events bubble from the nearest layout scope to its parent");
        Calls.Clear();
        ChildConsumes = true;
        Check(Routing.Bubble(["child", "parent"], Event) && Calls.SequenceEqual(["child"]),
            "A consuming child stops parent handling");
        Calls.Clear();
        Child.Dispose();
        Check(Routing.Bubble(["child", "parent"], Event) && Calls.SequenceEqual(["parent"]),
            "Disposed feature scopes cannot receive events");
        using var Replacement = Routing.Register("child", Request => false);
        Child.Dispose();
        Check(Routing.Describe()["scopes"]!.AsArray().Count == 2,
            "An old registration cannot remove a replacement scope");
        Check(Routing.TryHandleAction("window-a", "ui.context-menu:invalid-json") &&
            Routing.Describe()["diagnostic"]!.GetValue<string>() == "UI_CONTEXT_REQUEST_INVALID",
            "Malformed transport payloads produce a stable routing diagnostic");
        var Request = new JsonObject
        {
            ["route"] = new JsonArray("child", "parent"),
            ["x"] = 10,
            ["y"] = 20,
            ["payload"] = "domain-owned",
        };
        Calls.Clear();
        Check(Routing.TryHandleAction("window-a", "ui.context-menu:" + Request.ToJsonString()) &&
            Calls.SequenceEqual(["parent"]), "The wire adapter uses the same bubbling path");
        var DuplicateRejected = false;
        try
        {
            Routing.Bubble(["parent", "parent"], Event);
        }
        catch (ArgumentException)
        {
            DuplicateRejected = true;
        }
        Check(DuplicateRejected, "Malformed scope chains cannot invoke one owner twice");
        using var EmptyPayload = Routing.Register("empty", Request => Request.Payload.Length == 0);
        Check(Routing.Bubble(["empty"], new UiContextMenuEvent("window-a", 1, 2, "")),
            "A layout scope can receive a context gesture without domain-specific target metadata");
    }
}
