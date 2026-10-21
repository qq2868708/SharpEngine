using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.UI;

internal static class UiLayoutInstanceValidation
{
    internal static void Run(Action<bool, string> Check)
    {
        const string Source = "<template><div id='panel'><span id='original'>${Caption}</span><button id='authored' data-action='app.action'>Authored</button></div></template>";
        var Definition = UiLayoutTemplate.Parse(Source);
        using var First = Definition.CreateInstance("first");
        using var Second = Definition.CreateInstance("second");
        var Context = new UiLayoutContext(640, 480);
        var Values = new JsonObject
        {
            ["Caption"] = "Original",
        };
        var Panel = First.Find("panel")!;
        var Count = 0;
        var Button = new UiButton("extra", "Code ${Literal} <&>");
        var Label = new UiLabel("count", "0");
        Button.Clicked += () =>
        {
            Count++;
            Label.Text = Count.ToString();
        };
        Panel.Add(Button);
        Panel.Add(Label);
        var Input = new UiTextField("input");
        Input.ValueChanged += Value => Label.Text = Value;
        Panel.Add(Input);
        var Markup = First.Render(Context, Values);
        var Xml = XElement.Parse("<root>" + Markup + "</root>");
        var NativeButton = Xml.Descendants("button").Single(Element => Element.Attribute("id")?.Value == Button.ElementId);
        Check(NativeButton.Value == "Code ${Literal} <&>", "Runtime text is literal and escaped, never interpreted as template bindings");
        var Click = NativeButton.Attribute("data-action")!.Value;
        Check(First.TryHandleAction(Click) && Count == 1, "Runtime click binding dispatches to its owner instance");
        Check(!Second.TryHandleAction(Click), "Another instance cannot consume the event");
        var Refresh = First.Render(Context, Values);
        Check(Refresh.Contains(Button.ElementId) && Refresh.Contains(">1</span>"), "Dynamic widgets and changed state survive layout refresh");
        Check(!Second.Render(Context, Values).Contains("Code ${Literal}"), "Shared definitions do not share added nodes");
        Button.Enabled = false;
        First.Render(Context, Values);
        First.TryHandleAction(Click);
        Check(Count == 1, "Disabled runtime widgets do not execute callbacks");
        Button.Enabled = true;
        var InputMarkup = XElement.Parse("<root>" + First.Render(Context, Values) + "</root>");
        var Change = InputMarkup.Descendants("input").Single().Attribute("data-change-action")!.Value;
        First.TryHandleAction(Change + "text:with:<&>");
        Check(Input.Value == "text:with:<&>" && Label.Text == Input.Value, "Input events retain complete values and update instance state");
        var BeforeInvalid = First.Describe().ToJsonString();
        var Broken = new UiContainer("broken");
        Broken.Add(new UiLabel("duplicate", "a"));
        Broken.Add(new UiLabel("duplicate", "b"));
        var Rejected = false;
        try
        {
            Panel.Add(Broken);
        }
        catch (InvalidOperationException)
        {
            Rejected = true;
        }
        Check(Rejected && First.Describe().ToJsonString() == BeforeInvalid, "Invalid subtree attachment is atomic");
        var OldElementId = Button.ElementId;
        Check(Button.Remove(), "Runtime nodes can be removed");
        First.TryHandleAction(Click);
        Check(Count == 1 && !First.Render(Context, Values).Contains(OldElementId), "Removed nodes and queued old events cannot affect the instance");
        Rejected = false;
        try
        {
            Button.Text = "stale";
        }
        catch (InvalidOperationException)
        {
            Rejected = true;
        }
        Check(Rejected, "Removed handles reject mutations");
        var NewButton = new UiButton("extra", "New");
        Panel.Add(NewButton);
        Check(NewButton.ElementId != NativeButton.Attribute("id")!.Value, "Recreated logical IDs get a new event identity");
        using var Child = UiLayoutTemplate.Parse("<template><span id='child-only'>Child</span></template>").CreateInstance("child");
        var Mount = Panel.AddLayout("child-mount", Child);
        Check(First.Render(Context, Values).Contains("child-only") && First.Find("child-only") is null,
            "Child layouts can mount while their internal tree remains encapsulated");
        var ChildClicks = 0;
        var ChildButton = new UiButton("child-button", "Child action");
        ChildButton.Clicked += () => ChildClicks++;
        Child.Root.Add(ChildButton);
        var ChildMarkup = XElement.Parse("<root>" + First.Render(Context, Values) + "</root>");
        var ChildAction = ChildMarkup.Descendants("button").Single(Element => Element.Attribute("id")?.Value == ChildButton.ElementId)
            .Attribute("data-action")!.Value;
        First.TryHandleAction(ChildAction);
        Check(ChildClicks == 1, "Mounted child events route through the UI-domain contract");
        Panel.Visible = false;
        First.Render(Context, Values);
        First.TryHandleAction(ChildAction);
        Check(ChildClicks == 1, "Hidden mount ancestors block queued child-layout events");
        Panel.Visible = true;
        Rejected = false;
        try
        {
            Child.Root.AddLayout("cycle", First);
        }
        catch (InvalidOperationException)
        {
            Rejected = true;
        }
        Check(Rejected, "Known layout-instance cycles are rejected");
        Mount.Remove();
        Check(Child.Render(Context, new()).Contains("Child"), "Removing a mount never disposes a borrowed child");
        Check(Definition.Render(Context, Values).Contains("original") && !Definition.Render(Context, Values).Contains("New"),
            "The source definition is unchanged by instance mutations");
        First.Dispose();
        Check(!First.TryHandleAction(Click), "Disposed instances release event routing");
    }
}
