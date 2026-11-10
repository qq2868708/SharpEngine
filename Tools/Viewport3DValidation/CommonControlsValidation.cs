using System.Xml.Linq;
using SharpEngine.UI;

internal static class CommonControlsValidation
{
    internal static void Run(Action<bool, string> Check)
    {
        var CollectionRows = new[]
        {
            new UiCollectionItem("collection-first", UiValueEditors.Number("Value",
                new UiValueInput("first-value", "3", "value:")), "remove:0", "up:0", "down:0"),
            new UiCollectionItem("collection-second", UiValueEditors.Text("Value",
                new UiValueInput("second-value", "<safe>", "value:")), "remove:1", "up:1", "down:1"),
        };
        var Collection = XElement.Parse(UiCollectionEditor.Render("collection", "Items <A>", CollectionRows, "fold", "add"));
        Check(Collection.Descendants("input").Count() == 2 && Collection.Value.Contains("Items <A>", StringComparison.Ordinal),
            "Collection composes existing primitive editors and escapes presentation labels without World or Editor");
        Check(Collection.Descendants("button").Single(Element => Element.Attribute("id")?.Value == "collection-first-up")
            .Attribute("disabled") is not null && Collection.Descendants("button").Single(Element =>
                Element.Attribute("id")?.Value == "collection-second-down").Attribute("disabled") is not null,
            "Collection boundaries disable moves outside the ordered values");
        var DisabledCollection = XElement.Parse(UiCollectionEditor.Render("disabled", "Items", CollectionRows,
            "readonly-fold", "add", ReadOnly: true));
        Check(DisabledCollection.Descendants("button").Where(Element => Element.Attribute("id")?.Value != "disabled-toggle")
            .All(Element => Element.Attribute("disabled") is not null && Element.Attribute("data-action")?.Value == string.Empty),
            "Read-only collection mutation buttons are disabled while folding remains available");
        Check(DisabledCollection.Descendants("input").All(Element => Element.Attribute("disabled") is not null &&
            Element.Attribute("data-value-commit") is null), "A read-only collection enforces disabled children even when its consumer supplies editable values");
        var CollapsedCollection = XElement.Parse(UiCollectionEditor.Render("collapsed", "Items", CollectionRows, "fold", "add", Expanded: false));
        Check(!CollapsedCollection.Descendants("input").Any() &&
            Collection.Descendants("input").Count() == 2, "Two collection uses retain independent expansion and child instances");
        var MapRows = new[]
        {
            new UiMapEntry("map-first", UiValueEditors.Input(new UiValueInput(
                "map-first-key", "alpha", "rename:")),
                UiValueEditors.Vector("Offset", new[]
                {
                    new UiValueInput("map-x", "1", "axis-x:"),
                    new UiValueInput("map-y", "2", "axis-y:"),
                    new UiValueInput("map-z", "3", "axis-z:"),
                }), "remove:", "", "move-down:"),
            new UiMapEntry("map-second", UiValueEditors.Input(new UiValueInput(
                "map-second-key", "zeta", "rename:")),
                UiValueEditors.Text("Value", new UiValueInput("map-text", "text", "value:")),
                "remove:", "move-up:", ""),
        };
        var Map = XElement.Parse(UiMapEditor.Render("map", "Offsets", MapRows,
            "fold-map", "add-map"));
        Check(Map.Descendants("input").Count() == 6 &&
            Map.Descendants("button").Single(Button =>
                Button.Attribute("id")?.Value == "map-first-up")
                .Attribute("disabled") is not null &&
            Map.Descendants("button").Single(Button =>
                Button.Attribute("id")?.Value == "map-second-down")
                .Attribute("disabled") is not null,
            "Map rows reuse basic value controls and disable only visual-order boundaries");
        var ReadOnlyChild = UiMapEditor.Render("child-map", "Child Map", MapRows,
            "child-fold", "", ReadOnly: true);
        var NestedReadOnly = XElement.Parse(UiMapEditor.Render("parent-map", "Parent Map",
            new[]
            {
                new UiMapEntry("parent-row", "<span>Parent</span>", ReadOnlyChild,
                    "", "", ""),
            }, "parent-fold", "", ReadOnly: true));
        Check(NestedReadOnly.Descendants("input").All(Input =>
                Input.Attribute("disabled") is not null) &&
            NestedReadOnly.Descendants("button").Single(Button =>
                Button.Attribute("id")?.Value == "map-second-up")
                .Attribute("data-action")?.Value == "move-up:" &&
            NestedReadOnly.Descendants("button").Single(Button =>
                Button.Attribute("id")?.Value == "map-second-remove")
                .Attribute("data-action")?.Value == string.Empty,
            "Read-only nested Maps disable data edits while preserving transient child ordering");
        var Nodes = new UiTreeNode[]
        {
            new("child", "root"), new("root", null), new("leaf", "child"),
        };
        var Collapsed = new HashSet<string>(StringComparer.Ordinal);
        var View = UiTreeProjection.Visible(Nodes, Collapsed);
        Check(View.Select(Node => Node.Key).SequenceEqual(new[]
                {
                    "root", "child", "leaf"
                }) && View[2].Depth == 2,
            "Generic tree traversal orders parent references independently of domain or source order");
        Collapsed.Add("root");
        Check(UiTreeProjection.Visible(Nodes, Collapsed).Count == 1 && UiTreeProjection.Visible(Nodes, new HashSet<string>()).Count == 3,
            "Expansion state belongs to the owning view and does not leak to a second consumer");
        var Included = new HashSet<string>(new[]
                {
                    "root", "child", "leaf"
                }, StringComparer.Ordinal);
        Check(UiTreeProjection.Visible(Nodes, Collapsed, Included).Count == 3, "A filtered tree reveals included branches without changing stored expansion");
        var Markup = new UiTreeView(new[]
        {
            new UiTreeRow("root", "<Mesh>${Literal}", "Untrusted & text", "icon", 0, true, true, true,
                "test.select", "test.toggle", "test-node", "test-toggle"),
        }).BuildLayout(default);
        var Tree = XElement.Parse(Markup);
        Check(Tree.Value.Contains("<Mesh>${Literal}", StringComparison.Ordinal) && !Tree.Descendants("Mesh").Any(),
            "The shared authored tree layout escapes caller content and preserves literal template-like text");
        Check(Tree.Descendants("button").Count() == 2 && Tree.Descendants("button").Single(Node => (string?)Node.Attribute("id") == "test-toggle")
            .Attribute("data-action")!.Value == "test.toggle", "Generic rows preserve distinct branch and selection input targets");
        var MutableAttributes = new Dictionary<string, string>
        {
            ["data-context-id"] = "first",
        };
        var OwnedRows = new[]
        {
            new UiTreeRow("node", "First", "Node", "icon", 0, true, false, false, "select", string.Empty,
                "first-node", string.Empty, Attributes: MutableAttributes),
        };
        var OwnedTree = new UiTreeView(OwnedRows);
        var FirstMarkup = OwnedTree.BuildLayout(default);
        MutableAttributes["data-context-id"] = "second";
        OwnedRows[0] = OwnedRows[0] with
        {
            Name = "Second",
        };
        Check(OwnedTree.BuildLayout(default) == FirstMarkup, "A generic tree snapshots caller-owned arrays and metadata instead of exposing mutable shared state");
        OwnedTree.SetRows(OwnedRows);
        Check(OwnedTree.BuildLayout(default) != FirstMarkup && OwnedTree.Describe()["rows"]![0]!["key"]!.GetValue<string>() == "node",
            "Explicit row updates invalidate presentation and immediately publish current semantic identity");
        var Inputs = new[]
        {
            new UiValueInput("axis-X", "1", "set-x:"), new UiValueInput("axis-Y", "2", "set-y:"), new UiValueInput("axis-Z", "3", "set-z:"),
        };
        var Transform = XElement.Parse(UiValueEditors.Transform("Transform", Inputs, Inputs.Select(Input => Input with
        {
            Id = "rotation-" + Input.Id,
        }).ToArray(), Inputs.Select(Input => Input with
        {
            Id = "scale-" + Input.Id,
        }).ToArray()));
        Check(Transform.Descendants("input").Count() == 9 && Transform.Descendants().Count(Node => (string?)Node.Attribute("class") == "se-vector ") == 2 &&
            Transform.Descendants().Count(Node => (string?)Node.Attribute("class") == "se-vector se-rotator") == 1,
            "Transform composes two Vector editors and the same three-axis editor with Rotator semantics");
        Check(Transform.Descendants("input").All(Node => Node.Attribute("data-value-commit") is not null && Node.Attribute("data-change-action") is null),
            "Shared scalar inputs declare commit-only bindings and leave intermediate text out of authoring commands");
        var ReadOnly = XElement.Parse(UiValueEditors.Input(Inputs[0] with
        {
            ReadOnly = true,
        }));
        Check(ReadOnly.Attribute("disabled") is not null && ReadOnly.Attribute("data-original-value")!.Value == "1" &&
            ReadOnly.Attribute("data-value-commit") is null && ReadOnly.Attribute("tabindex")!.Value == "-1",
            "Read-only fields preserve displayed values, disable native input and expose no mutation binding");
        var ReadOnlyChoice = XElement.Parse(UiValueEditors.ChoiceInput("readonly-choice", "b", new[]
        {
            ("a", "Alpha"), ("b", "Beta"),
        }, "test.choice:", true));
        Check(ReadOnlyChoice.Attribute("disabled") is not null && ReadOnlyChoice.Attribute("data-change-action") is null &&
            ReadOnlyChoice.Elements("option").Single(Node => Node.Attribute("selected") is not null).Value == "Beta",
            "Read-only choices retain their selected label while disabling selection and mutation actions");
        var ReadOnlyBoolean = XElement.Parse(UiValueEditors.BooleanInput("readonly-boolean", true, "test.bool", true));
        Check(ReadOnlyBoolean.Attribute("disabled") is not null && ReadOnlyBoolean.Attribute("checked") is not null &&
            ReadOnlyBoolean.Attribute("data-action") is null, "Read-only booleans retain their checked value without an enabled toggle action");
        var ReadOnlyAxes = Inputs.Select(Input => Input with
        {
            ReadOnly = true,
        }).ToArray();
        var ReadOnlyTransform = XElement.Parse(UiValueEditors.Transform("Transform", ReadOnlyAxes, ReadOnlyAxes, ReadOnlyAxes));
        Check(ReadOnlyTransform.Descendants("input").Count() == 9 &&
            ReadOnlyTransform.Descendants("input").All(Node => Node.Attribute("disabled") is not null && Node.Attribute("data-value-commit") is null),
            "Read-only Transform keeps its three Vector/Rotator groups and nine axis controls using the same disabled state");
        var MixedAxes = Inputs.ToArray();
        MixedAxes[1] = MixedAxes[1] with
        {
            ReadOnly = true,
        };
        var MixedVector = XElement.Parse(UiValueEditors.Vector("Vector", MixedAxes));
        Check(MixedVector.Descendants("input").Count(Node => Node.Attribute("disabled") is not null) == 1 &&
            MixedVector.Descendants("div").Count(Node => ((string?)Node.Attribute("class"))?.Contains("se-vector-axis-disabled", StringComparison.Ordinal) == true) == 1,
            "Per-axis read-only state is preserved when a Vector mixes editable and disabled components");
        foreach (var Invalid in new[]
        {
            new UiTreeNode[] { new("a", "b"), new("b", "a") },
            new UiTreeNode[] { new("a", "missing") },
            new UiTreeNode[] { new("a", null), new("a", null) },
        })
        {
            var Rejected = false;
            try
            {
                UiTreeProjection.Visible(Invalid, new HashSet<string>());
            }
            catch (InvalidDataException)
            {
                Rejected = true;
            }
            Check(Rejected, "Generic tree rejects cyclic, dangling or duplicate identity even without a domain asset");
        }
    }
}
