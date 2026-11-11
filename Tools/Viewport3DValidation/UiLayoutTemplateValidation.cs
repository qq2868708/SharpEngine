using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using SharpEngine.UI;

internal static class UiLayoutTemplateValidation
{
    internal static void Run(Action<bool, string> Check)
    {
        var Definition = UiLayoutTemplate.Parse("""
            <template><div title="${Title}"><ui-slot name="Child" title="${Title}" width="${Width}" height="${Height}" />
            <ui-if test="Show"><span>${Title}</span></ui-if><ui-repeat items="Items"><button ui-disabled="Disabled">${Name}</button></ui-repeat></div></template>
            """);
        JsonObject Values(string Title, bool Disabled)
        {
            return new JsonObject
            {
                ["Title"] = Title,
                ["Width"] = 300,
                ["Height"] = 200,
                ["Show"] = true,
                ["Items"] = new JsonArray(new JsonObject
                {
                    ["Name"] = "Child <&>",
                    ["Disabled"] = Disabled,
                }),
            };
        }
        var Child = new ChildLayout();
        var Parts = new Dictionary<string, IUiLayoutPart>
        {
            ["Child"] = Child,
        };
        var First = Definition.Render(new(640, 480), Values("First <&>\"", true), Parts);
        var Second = Definition.Render(new(640, 480), Values("Second", false), Parts);
        Check(First.Contains("&lt;&amp;&gt;") && First.Contains("disabled=\"disabled\"") &&
            !Second.Contains("disabled="), "Layout values are escaped and per-use flags never leak between renders");
        Check(Child.Context.Width == 300 && Child.Context.Height == 200 && Child.Context.Title == "Second",
            "A typed UI child receives only its own layout context");
        Check(!Second.Contains("First") && !Second.Contains("ui-slot") && !Second.Contains("ui-repeat"),
            "Child layouts compose without exposing directives to the native frontend");
        var Third = Definition.Render(new(640, 480), Values("First <&>\"", true), Parts);
        Check(First == Third, "Rendering cannot mutate the shared layout definition");
        var Disabled = UiLayoutTemplate.Parse("""
            <template><input disabled="authored" ui-disabled="Flag" /><input ui-disabled="Flag" disabled="authored" /></template>
            """);
        var DisabledTrue = XElement.Parse("<root>" + Disabled.Render(default, new JsonObject
        {
            ["Flag"] = true,
        }) + "</root>");
        var DisabledFalse = XElement.Parse("<root>" + Disabled.Render(default, new JsonObject
        {
            ["Flag"] = false,
        }) + "</root>");
        Check(DisabledTrue.Elements().Select(Element => (string?)Element.Attribute("disabled"))
                .SequenceEqual(new[] { "disabled", "authored" }) &&
            DisabledFalse.Elements().All(Element => (string?)Element.Attribute("disabled") == "authored"),
            "Synthesized attributes retain the original last-write-wins order without duplicate attributes");
        const string LineBreaks = "First\r\nSecond\rThird";
        var Escaped = XElement.Parse(UiLayoutTemplate.Parse("<template><span title='${Text}'>${Text}</span></template>")
            .Render(default, new JsonObject
            {
                ["Text"] = LineBreaks,
            }));
        Check(Escaped.Value == LineBreaks && Escaped.Attribute("title")!.Value == LineBreaks,
            "Streaming rendering preserves carriage returns in both text and attribute values");
        var Namespaced = XElement.Parse(UiLayoutTemplate.Parse("""
            <template xmlns:p="urn:properties"><p:panel xmlns:q="urn:values" q:caption="${Text}"><child xmlns="urn:child" /></p:panel></template>
            """).Render(default, new JsonObject
        {
            ["Text"] = "<&>",
        }));
        Check(Namespaced.Name == XName.Get("panel", "urn:properties") &&
            Namespaced.Attribute(XName.Get("caption", "urn:values"))!.Value == "<&>" &&
            Namespaced.Elements().Single().Name == XName.Get("child", "urn:child"),
            "Rendering preserves inherited namespaces, namespace declarations and namespaced attributes");
        var Slot = UiLayoutTemplate.Parse("<template><div><ui-slot name='Child' /></div></template>");
        var Literal = XElement.Parse(Slot.Render(default, new(), new Dictionary<string, IUiLayoutPart>
        {
            ["Child"] = new LiteralLayout("<!--top--><span ui-disabled='Literal'>${Literal}<!--nested--></span><b />"),
        }));
        Check(Literal.Elements().Count() == 2 && Literal.Element("span")!.Value == "${Literal}" &&
            Literal.Element("span")!.Attribute("ui-disabled")!.Value == "Literal" &&
            Literal.Nodes().OfType<XComment>().Count() == 0 &&
            Literal.Element("span")!.Nodes().OfType<XComment>().Count() == 1,
            "Slots preserve literal bindings, multiple roots and nested comments without interpreting child directives");
        foreach (var Invalid in new[]
        {
            "<span>", "<!DOCTYPE span [<!ENTITY content 'expanded'>]><span>&content;</span>",
        })
        {
            var Rejected = false;
            try
            {
                Slot.Render(default, new(), new Dictionary<string, IUiLayoutPart>
                {
                    ["Child"] = new LiteralLayout(Invalid),
                });
            }
            catch (XmlException)
            {
                Rejected = true;
            }
            Check(Rejected, "Slots still reject malformed XML and DTD entities before returning markup");
        }
        void Reject(Action Operation, string Code)
        {
            var Rejected = false;
            try
            {
                Operation();
            }
            catch (InvalidDataException Error)
            {
                Rejected = Error.Message.Contains(Code, StringComparison.Ordinal);
            }
            Check(Rejected, Code + " is explicit");
        }
        Reject(() => Definition.Render(new(1, 1), Values("Missing", false)), "UILAYOUT_SLOT_UNBOUND");
        Reject(() => UiLayoutTemplate.Parse("<template><span>${Unknown}</span></template>").Render(new(1, 1), new()), "UILAYOUT_VALUE_UNBOUND");
        Reject(() => UiLayoutTemplate.Parse("<template><ui-if test=\"Wrong\"><span /></ui-if></template>")
            .Render(new(1, 1), new JsonObject
            {
                ["Wrong"] = "true",
            }), "UILAYOUT_BOOLEAN_REQUIRED");
        Reject(() => UiLayoutTemplate.Parse("<template><div ui-unknown=\"X\" /></template>").Render(new(1, 1), new()), "UILAYOUT_DIRECTIVE_UNKNOWN");
    }

    private sealed record LiteralLayout(string Markup) : IUiLayoutPart
    {
        public string BuildLayout(UiLayoutContext Context) { return Markup; }
    }

    private sealed class ChildLayout : IUiLayoutPart
    {
        internal UiLayoutContext Context
        {
            get;
            private set;
        }
        public string BuildLayout(UiLayoutContext Context)
        {
            this.Context = Context;
            return "<span class=\"child\">child layout</span>";
        }
    }
}
