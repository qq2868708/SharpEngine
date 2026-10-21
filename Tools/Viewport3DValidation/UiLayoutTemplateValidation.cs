using System.Text.Json.Nodes;
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
