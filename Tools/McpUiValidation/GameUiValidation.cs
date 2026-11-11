using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// End-to-end validation of code-driven runtime UI on the real Game target. It launches
/// the built Game executable with a fixture project whose Runtime Module presents a
/// project screen, then reads the live RmlUi snapshot over the game MCP pipe. This
/// proves the screen that the editor authors is the screen the player sees.
/// </summary>
internal static class GameUiValidation
{
    private const string ScreenAssetId = "project://ui/screen";
    private const string FillerAssetId = "project://ui/filler";

    internal static string CreateProject(string Root, string Configuration)
    {
        var Folder = Path.Combine(Root, "Saved/Validation/GameUi",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/UI"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/Worlds"));
        Directory.CreateDirectory(Path.Combine(Folder, "Content/Assets"));
        Directory.CreateDirectory(Path.Combine(Folder, "Source/GameUi"));
        File.Copy(Path.Combine(Root, "Templates/BasicProject/Directory.Build.props"),
            Path.Combine(Folder, "Directory.Build.props"), true);
        File.Copy(Path.Combine(Root, "Content/Worlds/Main.sworld"),
            Path.Combine(Folder, "Content/Worlds/Main.sworld"), true);

        const string ScreenRml = """
            <rml>
            <head><link type="text/rcss" href="Screen.rcss" /></head>
            <body><div id="screen" class="screen"><span id="caption" class="caption">Project screen</span></div></body>
            </rml>
            """;
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Screen.sui"), new JsonObject
        {
            ["$schema"] = "https://sharpengine.dev/schemas/ui-document-v1.json",
            ["schemaVersion"] = 1,
            ["name"] = "Runtime Screen",
            ["rml"] = ScreenRml,
        }.ToJsonString());
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Screen.rcss"), """
            .screen { display: flex; flex-direction: column; padding: 20px; background-color: #123456; }
            .caption { font-size: 28px; color: #ffeeaa; }
            """);
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Filler.sui"), new JsonObject
        {
            ["$schema"] = "https://sharpengine.dev/schemas/ui-document-v1.json",
            ["schemaVersion"] = 1,
            ["name"] = "Filler Screen",
            ["rml"] = """
                <rml><body><div id="filler"><span id="fillerLabel">filler</span></div></body></rml>
                """,
        }.ToJsonString());

        File.Copy(Path.Combine(Root, "Content/UI/Controls/CommonControls.rcss"),
            Path.Combine(Folder, "Content/UI/CommonControls.rcss"), true);

        // --- Composition fixture -------------------------------------------------
        // One reusable button definition (icon + decorative effects), referenced twice
        // by the screen template. Each reference is a separate instance with its own
        // parameters and its own click counter.
        File.Copy(Path.Combine(Root, "Resources/Editor/Icons/play.tga"),
            Path.Combine(Folder, "Content/UI/Play.tga"), true);
        File.Copy(Path.Combine(Root, "Resources/Editor/Icons/style.tga"),
            Path.Combine(Folder, "Content/UI/Style.tga"), true);
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Button.sui"), new JsonObject
        {
            ["$schema"] = "https://sharpengine.dev/schemas/ui-document-v1.json",
            ["schemaVersion"] = 1,
            ["name"] = "Reusable Button",
            ["rml"] = """
                <template>
                <div id="${Id}" class="button" style="border-color: ${Accent};">
                <img id="${Id}-icon" class="button-icon" src="${Icon}" />
                <span id="${Id}-label" class="button-label">${Label}</span>
                <span id="${Id}-count" class="button-count">${Count}</span>
                <button id="${Id}-hit" class="button-hit" action="ui.button:${Id}:click">Activate</button>
                </div>
                </template>
                """,
        }.ToJsonString());
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Composed.sui"), new JsonObject
        {
            ["$schema"] = "https://sharpengine.dev/schemas/ui-document-v1.json",
            ["schemaVersion"] = 1,
            ["name"] = "Composed Screen",
            ["rml"] = """
                <template>
                <div id="screen" class="screen">
                <span id="caption" class="caption">${Title}</span>
                <ui-slot name="Primary" />
                <ui-slot name="Secondary" />
                <div id="clip-test" style="position:absolute;left:1050px;top:580px;width:100px;height:80px;overflow:hidden;clip:always;">
                <div id="clip-child" style="display:block;width:180px;height:50px;transform:translate(40px,15px);background-color:#ee2277;"> </div></div>
                <div id="alpha-test" style="position:absolute;left:800px;top:600px;width:100px;height:60px;background-color:#000000;">
                <div id="alpha-child" style="display:block;width:100px;height:60px;background-color:#ff000080;"> </div></div>
                </div>
                </template>
                """,
            ["styleSheets"] = new JsonArray("Composed.rcss", "Button.rcss", "CommonControls.rcss"),
        }.ToJsonString());
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Composed.rcss"), """
            .screen { display: flex; flex-direction: column; padding: 20px; gap: 12px; background-color: #123456; }
            .caption { font-size: 26px; color: #ffeeaa; }
            """);
        // Decorative effects are authored once and shared by both instances.
        File.WriteAllText(Path.Combine(Folder, "Content/UI/Button.rcss"), """
            .button { display: flex; flex-direction: row; align-items: center; gap: 10px; padding: 10px 14px; background-color: #1d2733; border: 2px #3aa78f; border-radius: 10px; box-shadow: 0px 3px 6px #000000; }
            .button:hover { background-color: #2b3a4a; }
            .button-icon { width: 28px; height: 28px; }
            .button-label { font-size: 20px; color: #e6eef8; }
            .button-count { font-size: 16px; color: #8fd3c1; }
            .button-hit { height: 26px; padding: 2px 10px; }
            """);

        File.WriteAllText(Path.Combine(Folder, "Content/Assets/assets.json"), new JsonObject
        {
            ["schemaVersion"] = 2,
            ["assets"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "ui/screen",
                    ["type"] = "UiDocument",
                    ["name"] = "Runtime Screen",
                    ["path"] = "UI/Screen.sui",
                    ["importer"] = "SharpEngine.UiDocumentJson",
                },
                new JsonObject
                {
                    ["id"] = "ui/filler",
                    ["type"] = "UiDocument",
                    ["name"] = "Filler Screen",
                    ["path"] = "UI/Filler.sui",
                    ["importer"] = "SharpEngine.UiDocumentJson",
                },
                new JsonObject
                {
                    ["id"] = "ui/composed",
                    ["type"] = "UiDocument",
                    ["name"] = "Composed Screen",
                    ["path"] = "UI/Composed.sui",
                    ["importer"] = "SharpEngine.UiDocumentJson",
                },
                new JsonObject
                {
                    ["id"] = "ui/button",
                    ["type"] = "UiDocument",
                    ["name"] = "Reusable Button",
                    ["path"] = "UI/Button.sui",
                    ["importer"] = "SharpEngine.UiDocumentJson",
                },
            },
        }.ToJsonString());
        File.WriteAllText(Path.Combine(Folder, "Content/Worlds/Main.sworld"),
            File.ReadAllText(Path.Combine(Root, "Content/Worlds/Main.sworld")));

        var Module = File.ReadAllText(
                Path.Combine(Root, "Templates/BasicProject/Source/Runtime/Runtime.csproj.template"))
            .Replace("__PROJECT_NAMESPACE__", "GameUi", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(Folder, "Source/GameUi/GameUi.csproj"), Module);
        // The fixture proves that project code, not the engine, chooses the screen, and
        // that a wrong id is reported instead of silently swapping documents.
        File.WriteAllText(Path.Combine(Folder, "Source/GameUi/Module.cs"), $$"""
            using SharpEngine.Modules;
            using SharpEngine.UI;

            public sealed class GameUiModule : ISharpEngineModule
            {
                public void Startup(ModuleContext Context)
                {
                    var Ui = Context.Ui!;
                    var Wrong = Ui.ShowScreen("project://ui/filler");
                    if (!Wrong.Success || Wrong.AssetId != "{{FillerAssetId}}")
                    {
                        Context.Logger.Error($"Filler screen failed: {Wrong.Code} {Wrong.Message}");
                    }
                    var Shown = Ui.ShowScreen("{{ScreenAssetId}}");
                    if (!Shown.Success)
                    {
                        Context.Logger.Error($"Screen failed: {Shown.Code} {Shown.Message}");
                        return;
                    }
                    var Missing = Ui.ShowScreen("project://ui/absent");
                    if (Missing.Success || Missing.Code != "UI_SCREEN_ASSET_MISSING")
                    {
                        Context.Logger.Error($"Missing asset was accepted: {Missing.Code}");
                    }
                    Context.Logger.Info($"ACTIVE_SCREEN={Ui.ActiveScreenId}");

                    // Composition: one button definition, two independent instances.
                    var Definitions = new UiCompositionFixture();
                    var Primary = Definitions.Create("primary", "Primary Action",
                        "Play.tga", "#3aa78f");
                    var Secondary = Definitions.Create("secondary", "Secondary Action",
                        "Style.tga", "#c98b3a");
                    var Composed = Ui.ShowScreen(new UiScreenRequest(
                        "project://ui/composed",
                        new System.Text.Json.Nodes.JsonObject { ["Title"] = "Composed Screen" },
                        new System.Collections.Generic.Dictionary<string, IUiLayoutPart>
                        {
                            ["Primary"] = Primary,
                            ["Secondary"] = Secondary,
                        }));
                    if (!Composed.Success)
                    {
                        Context.Logger.Error($"Composed screen failed: {Composed.Code} {Composed.Message}");
                    }
                    Context.Logger.Info(
                        $"COMPOSED active={Composed.Success} primary={Primary.Clicks} secondary={Secondary.Clicks}");
                }

                public void Shutdown()
                {
                }
            }
            """);

        File.WriteAllText(Path.Combine(Folder, "Source/GameUi/Composition.cs"), """
            using System.IO;
            using System.Text.Json.Nodes;
            using SharpEngine.Assets;
            using SharpEngine.UI;

            /// <summary>Loads the reusable button definition once and hands out instances.</summary>
            public sealed class UiCompositionFixture
            {
                private readonly UiLayoutTemplate Definition;

                public UiCompositionFixture()
                {
                    var ProjectRoot = Path.GetDirectoryName(
                        System.Environment.GetEnvironmentVariable("SHARPENGINE_PROJECT_MANIFEST")!)!;
                    var Package = Path.Combine(ProjectRoot, "Content", "UI", "Button.sui");
                    Definition = UiLayoutTemplate.Parse(UiDocumentAsset.Load(Package).Rml);
                }

                public ReusableButton Create(string Id, string Label, string Icon,
                    string Accent) { return new ReusableButton(Definition, Id, Label, Icon, Accent); }
            }

            /// <summary>One use of the shared definition. Every parameter and the click
            /// counter belong to this instance, never to the definition.</summary>
            public sealed class ReusableButton : IUiLayoutPart, IUiActionHandler
            {
                private readonly UiLayoutTemplate Definition;

                public ReusableButton(UiLayoutTemplate Definition, string Id, string Label,
                    string Icon, string Accent)
                {
                    this.Definition = Definition;
                    this.Id = Id;
                    this.Label = Label;
                    this.Icon = Icon;
                    this.Accent = Accent;
                }

                public string Id { get; }
                public string Label { get; }
                public string Icon { get; }
                public string Accent { get; }
                public int Clicks { get; private set; }

                public double EditedValue
                {
                    get;
                    private set;
                } = 1;

                public double RangeValue
                {
                    get;
                    private set;
                } = 0.25;

                private int RangeCommits;

                public string ReferenceValue
                {
                    get;
                    private set;
                } = "project://materials/green";

                private double[] ColorValue = new[]
                {
                    0.2, 0.6, 0.9, 1.0,
                };
                private int ColorCommits;
                private bool ShowCollections;
                private bool CollectionExpanded = true;
                private bool DisabledCollectionExpanded = true;
                private bool EmptyExpanded = true;
                private readonly System.Collections.Generic.List<double> EmptyValues = new();
                private readonly System.Collections.Generic.List<double> CollectionValues = new()
                {
                    3, 8,
                };
                private readonly System.Collections.Generic.Stack<double[]> CollectionUndo = new();
                private readonly System.Collections.Generic.Stack<double[]> CollectionRedo = new();
                private readonly System.Collections.Generic.Dictionary<string, System.Numerics.Vector3> MapValues = new()
                {
                    ["zeta"] = new System.Numerics.Vector3(9, 8, 7),
                    ["alpha"] = new System.Numerics.Vector3(1, 2, 3),
                };
                private readonly System.Collections.Generic.List<string> MapOrder = new()
                {
                    "alpha", "zeta",
                };
                private bool MapExpanded = true;
                private bool ShowMaps;

                private string BuildCollections()
                {
                    var Rows = CollectionValues.Select((Value, Index) => new UiCollectionItem(
                        "collection-item-" + Index,
                        UiValueEditors.Number("Value", new UiValueInput("collection-value-" + Index,
                            Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "fixture.collection:value," + Index + ":", false, "number")),
                        "fixture.collection:remove," + Index, "fixture.collection:up," + Index, "fixture.collection:down," + Index)).ToArray();
                    var DisabledRows = new[]
                    {
                        new UiCollectionItem("disabled-item-0", UiValueEditors.Vector("Vector", new[]
                        {
                            new UiValueInput("disabled-list-x", "1", "forbidden", true),
                            new UiValueInput("disabled-list-y", "2", "forbidden", true),
                            new UiValueInput("disabled-list-z", "3", "forbidden", true),
                        }), "forbidden", "forbidden", "forbidden"),
                        new UiCollectionItem("disabled-item-1", UiValueEditors.Text("Text",
                            new UiValueInput("disabled-list-text", "Read-only value", "forbidden", true)), "forbidden", "forbidden", "forbidden"),
                    };
                    var EmptyRows = EmptyValues.Select((Value, Index) => new UiCollectionItem("empty-item-" + Index,
                        UiValueEditors.Number("Value", new UiValueInput("empty-value-" + Index, "0", "fixture.collection:empty-value," + Index + ":")),
                        "fixture.collection:empty-remove," + Index)).ToArray();
                    return "<div id='collection-fixture' style='position:absolute;left:20px;top:20px;width:1240px;height:680px;z-index:20;box-sizing:border-box;padding:14px;background-color:#16212d;'>" +
                        "<span style='display:block;font-size:24px;color:#e6eef8;margin-bottom:14px;'>Shared collection editors</span>" +
                        "<button id='map-show' action='fixture.map:show'>Map editor</button>" +
                        "<div style='display:flex;gap:24px;'><div style='display:block;width:48%;'>" +
                        UiCollectionEditor.Render("editable-list", "Numbers (editable)", Rows, "fixture.collection:fold", "fixture.collection:add", CollectionExpanded) +
                        "<div style='display:flex;gap:8px;'><button id='collection-undo' class='se-collection-command' style='width:90px;' action='fixture.collection:undo'>Undo</button><button id='collection-redo' class='se-collection-command' style='width:90px;' action='fixture.collection:redo'>Redo</button></div>" +
                        "<span id='collection-state' style='display:block;'>" + string.Join(",", CollectionValues.Select(Value => Value.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "</span>" +
                        UiCollectionEditor.Render("empty-list", "Initially empty list", EmptyRows, "fixture.collection:empty-fold", "fixture.collection:empty-add", EmptyExpanded) +
                        UiCollectionEditor.Render("null-list", "Null list (disabled)", System.Array.Empty<UiCollectionItem>(), "fixture.collection:null-fold", string.Empty, ReadOnly: true, IsNull: true) +
                        "</div><div style='display:block;width:48%;'>" +
                        UiCollectionEditor.Render("disabled-list", "Values (disabled)", DisabledRows, "fixture.collection:disabled-fold", "forbidden", DisabledCollectionExpanded, true) +
                        "</div></div></div>";
                }

                private string BuildMapEditor()
                {
                    var Rows = new System.Collections.Generic.List<UiMapEntry>();
                    for (var Index = 0; Index < MapOrder.Count; Index++)
                    {
                        var Key = MapOrder[Index];
                        var EncodedKey = Uri.EscapeDataString(Key);
                        var Value = MapValues[Key];
                        var RenameAction = "fixture.map:rename:" + EncodedKey + ":";
                        var KeyMarkup = "<div class=\"se-map-key\">" + UiValueEditors.Input(
                            new UiValueInput("map-key-" + Key, Key, RenameAction)) + "</div>";
                        var Axes = new[]
                        {
                            new UiValueInput("map-value-" + Key + "-X",
                                UiValueEditors.Format(Value.X), "fixture.map:axis:" + EncodedKey + ":0:", false, "number"),
                            new UiValueInput("map-value-" + Key + "-Y",
                                UiValueEditors.Format(Value.Y), "fixture.map:axis:" + EncodedKey + ":1:", false, "number"),
                            new UiValueInput("map-value-" + Key + "-Z",
                                UiValueEditors.Format(Value.Z), "fixture.map:axis:" + EncodedKey + ":2:", false, "number"),
                        };
                        Rows.Add(new UiMapEntry("map-entry-" + Key, KeyMarkup,
                            UiValueEditors.Vector("Offset", Axes, "XYZ"),
                            "fixture.map:remove:" + EncodedKey,
                            Index == 0 ? string.Empty : "fixture.map:view-up:" + EncodedKey,
                            Index + 1 == MapOrder.Count ? string.Empty :
                                "fixture.map:view-down:" + EncodedKey));
                    }
                    var DataState = string.Join("|", MapValues.OrderBy(Pair => Pair.Key,
                        StringComparer.Ordinal).Select(Pair => Pair.Key + ":" +
                        UiValueEditors.Format(Pair.Value.X) + "," +
                        UiValueEditors.Format(Pair.Value.Y) + "," +
                        UiValueEditors.Format(Pair.Value.Z)));
                    return "<div id='map-fixture' style='position:absolute;left:20px;top:20px;width:900px;height:680px;z-index:20;box-sizing:border-box;padding:16px;background-color:#16212d;'>" +
                        "<span style='display:block;font-size:24px;color:#e6eef8;margin-bottom:14px;'>Shared Map editor</span>" +
                        UiMapEditor.Render("interactive-map", "Offsets (unordered data)", Rows,
                            "fixture.map:fold", "fixture.map:add", MapExpanded) +
                        "<div style='display:block;margin-top:14px;color:#e6eef8;'>Editor display order: <span id='map-order'>" +
                        string.Join(",", MapOrder) + "</span></div>" +
                        "<div style='display:block;margin-top:6px;color:#95a6ba;'>Data (stable key order): <span id='map-data-state'>" +
                        DataState + "</span></div></div>";
                }

                private void HandleMapAction(string Action)
                {
                    var Parts = Action["fixture.map:".Length..].Split(':', 4);
                    var Operation = Parts[0];
                    if (Operation == "show")
                    {
                        ShowMaps = true;
                        ShowCollections = false;
                        return;
                    }
                    if (Operation == "fold")
                    {
                        MapExpanded = !MapExpanded;
                        return;
                    }
                    if (Operation == "add")
                    {
                        var Key = "New Key";
                        for (var Index = 2; MapValues.ContainsKey(Key); Index++)
                        {
                            Key = "New Key " + Index;
                        }
                        MapValues.Add(Key, System.Numerics.Vector3.Zero);
                        MapOrder.Add(Key);
                        return;
                    }
                    var EntryKey = Uri.UnescapeDataString(Parts[1]);
                    if (Operation is "view-up" or "view-down")
                    {
                        var Index = MapOrder.IndexOf(EntryKey);
                        var Target = Index + (Operation == "view-up" ? -1 : 1);
                        (MapOrder[Index], MapOrder[Target]) = (MapOrder[Target], MapOrder[Index]);
                        return;
                    }
                    if (Operation == "remove")
                    {
                        MapValues.Remove(EntryKey);
                        MapOrder.Remove(EntryKey);
                        return;
                    }
                    if (Operation == "rename")
                    {
                        var NewKey = Parts[2];
                        if (!MapValues.ContainsKey(NewKey))
                        {
                            var Index = MapOrder.IndexOf(EntryKey);
                            MapValues[NewKey] = MapValues[EntryKey];
                            MapValues.Remove(EntryKey);
                            MapOrder[Index] = NewKey;
                        }
                        return;
                    }
                    if (Operation == "axis")
                    {
                        var Axis = int.Parse(Parts[2], System.Globalization.CultureInfo.InvariantCulture);
                        var Number = float.Parse(Parts[3], System.Globalization.CultureInfo.InvariantCulture);
                        var Value = MapValues[EntryKey];
                        MapValues[EntryKey] = Axis switch
                        {
                            0 => new System.Numerics.Vector3(Number, Value.Y, Value.Z),
                            1 => new System.Numerics.Vector3(Value.X, Number, Value.Z),
                            _ => new System.Numerics.Vector3(Value.X, Value.Y, Number),
                        };
                    }
                }

                public string BuildLayout(UiLayoutContext Context)
                {
                    if (Id == "secondary" && ShowMaps)
                    {
                        return BuildMapEditor();
                    }
                    if (Id == "secondary" && ShowCollections)
                    {
                        return BuildCollections();
                    }
                    var Markup = Definition.Render(Context, new JsonObject
                    {
                        ["Id"] = Id,
                        ["Label"] = Label,
                        ["Icon"] = Icon,
                        ["Accent"] = Accent,
                        ["Count"] = Clicks,
                    });
                    if (Id == "primary")
                    {
                        Markup += UiValueEditors.Number("Shared Value", new UiValueInput("common-game-number",
                            EditedValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "ui.game-number:")) +
                            "<span id='common-game-value'>" + EditedValue.ToString(System.Globalization.CultureInfo.InvariantCulture) + "</span>";
                    }
                    if (Id == "secondary")
                    {
                        var ColorAxes = new[]
                        {
                            new UiValueInput("shared-color-R", "0.2", "fixture.color:R:", true, "number"),
                            new UiValueInput("shared-color-G", "0.6", "fixture.color:G:", true, "number"),
                            new UiValueInput("shared-color-B", "0.9", "fixture.color:B:", true, "number"),
                            new UiValueInput("shared-color-A", "1", "fixture.color:A:", true, "number"),
                        };
                        var Options = new[]
                        {
                            (string.Empty, "None"), ("project://materials/green", "Green Material"),
                            ("project://materials/blue", "Blue Material"),
                        };
                        var EditableAxes = ColorValue.Select((Value, Index) => new UiValueInput(
                            "editable-color-" + "RGBA"[Index],
                            Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "fixture.color-axis:" + Index + ":", false, "number")).ToArray();
                        Markup += "<div style='display:flex;gap:20px;margin-top:12px;'>" +
                            "<div style='display:block;width:48%;'>" +
                            UiValueEditors.Color("Color (editable)", EditableAxes, "fixture.color:") +
                            "<span id='color-commits'>" + ColorCommits + "</span>" +
                            UiValueEditors.Range("Range (editable)", new UiValueInput("shared-range",
                                RangeValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "fixture.range:", false, "number"), 0, 1, 0.01) +
                            "<span id='range-state'>" + RangeValue.ToString(System.Globalization.CultureInfo.InvariantCulture) + "</span>" +
                            "<span id='range-commits'>" + RangeCommits + "</span>" +
                            UiValueEditors.Reference("Reference (editable)", "shared-reference", ReferenceValue,
                                Options, "fixture.reference:") +
                            "</div><div style='width:48%;'>" +
                            UiValueEditors.Color("Color RGBA (disabled)", ColorAxes) +
                            UiValueEditors.Range("Range (disabled)", new UiValueInput("readonly-range", "0.75", "fixture.range:", true, "number"), 0, 1, 0.01) +
                            UiValueEditors.Reference("Reference (disabled)", "readonly-reference", "project://materials/green",
                                Options, "fixture.reference:", true) + "</div></div>" +
                            "<button id='collection-show' action='fixture.collection:show'>Collection editors</button>" +
                            "<button id='map-show' action='fixture.map:show'>Map editor</button>";
                    }
                    return Markup;
                }

                public bool TryHandleAction(string Action)
                {
                    if (Id == "secondary" && Action.StartsWith("fixture.map:", System.StringComparison.Ordinal))
                    {
                        HandleMapAction(Action);
                        return true;
                    }
                    if (Id == "secondary" && Action.StartsWith("fixture.collection:", System.StringComparison.Ordinal))
                    {
                        var Parts = Action["fixture.collection:".Length..].Split(':', 2);
                        var Operation = Parts[0].Split(',');
                        if (Operation[0] == "show")
                        {
                            ShowCollections = true;
                        }
                        else if (Operation[0] == "fold")
                        {
                            CollectionExpanded = !CollectionExpanded;
                        }
                        else if (Operation[0] == "disabled-fold")
                        {
                            DisabledCollectionExpanded = !DisabledCollectionExpanded;
                        }
                        else if (Operation[0] == "empty-fold")
                        {
                            EmptyExpanded = !EmptyExpanded;
                        }
                        else if (Operation[0] == "empty-add")
                        {
                            EmptyValues.Add(0);
                        }
                        else if (Operation[0] == "empty-remove")
                        {
                            EmptyValues.RemoveAt(int.Parse(Operation[1]));
                        }
                        else if (Operation[0] == "empty-value")
                        {
                            EmptyValues[int.Parse(Operation[1])] = double.Parse(Parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else if (Operation[0] == "undo" && CollectionUndo.Count > 0)
                        {
                            CollectionRedo.Push(CollectionValues.ToArray());
                            CollectionValues.Clear();
                            CollectionValues.AddRange(CollectionUndo.Pop());
                        }
                        else if (Operation[0] == "redo" && CollectionRedo.Count > 0)
                        {
                            CollectionUndo.Push(CollectionValues.ToArray());
                            CollectionValues.Clear();
                            CollectionValues.AddRange(CollectionRedo.Pop());
                        }
                        else if (Operation[0] is "add" or "remove" or "up" or "down" or "value")
                        {
                            CollectionUndo.Push(CollectionValues.ToArray());
                            CollectionRedo.Clear();
                            var Index = Operation.Length > 1 ? int.Parse(Operation[1]) : -1;
                            if (Operation[0] == "add")
                            {
                                CollectionValues.Add(0);
                            }
                            else if (Operation[0] == "remove")
                            {
                                CollectionValues.RemoveAt(Index);
                            }
                            else if (Operation[0] == "value")
                            {
                                CollectionValues[Index] = double.Parse(Parts[1], System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                var Target = Index + (Operation[0] == "up" ? -1 : 1);
                                var Value = CollectionValues[Index];
                                CollectionValues.RemoveAt(Index);
                                CollectionValues.Insert(Target, Value);
                            }
                        }
                        return true;
                    }
                    if (Id == "secondary" && Action.StartsWith("fixture.color:", System.StringComparison.Ordinal))
                    {
                        var Values = JsonNode.Parse(Action["fixture.color:".Length..])!.AsArray();
                        ColorValue = Values.Select(Value => Value!.GetValue<double>()).ToArray();
                        ColorCommits++;
                        return true;
                    }
                    if (Id == "secondary" && Action.StartsWith("fixture.color-axis:", System.StringComparison.Ordinal))
                    {
                        var Parts = Action["fixture.color-axis:".Length..].Split(':');
                        ColorValue[int.Parse(Parts[0])] = double.Parse(Parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        ColorCommits++;
                        return true;
                    }
                    if (Id == "secondary" && Action.StartsWith("fixture.range:", System.StringComparison.Ordinal))
                    {
                        RangeValue = double.Parse(Action["fixture.range:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                        RangeCommits++;
                        return true;
                    }
                    if (Id == "secondary" && Action.StartsWith("fixture.reference:", System.StringComparison.Ordinal))
                    {
                        ReferenceValue = Action["fixture.reference:".Length..];
                        return true;
                    }
                    if (Id == "primary" && Action.StartsWith("ui.game-number:", System.StringComparison.Ordinal))
                    {
                        EditedValue = double.Parse(Action["ui.game-number:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                        return true;
                    }
                    if (Action != "ui.button:" + Id + ":click")
                    {
                        return false;
                    }
                    Clicks++;
                    return true;
                }
            }
            """);

        var Manifest = Path.Combine(Folder, "GameUi.sharpproject");
        File.WriteAllText(Manifest, new JsonObject
        {
            ["schemaVersion"] = 3,
            ["projectId"] = "game-ui-validation",
            ["displayName"] = "Game UI validation",
            ["engine"] = new JsonObject
            {
                ["id"] = "SharpEngine",
                ["version"] = "0.2.0-dev",
                ["association"] = "game-ui-validation",
                ["manifest"] = "engine.json",
            },
            ["modules"] = new JsonArray("Source/GameUi/GameUi.csproj"),
            ["assets"] = new JsonObject
            {
                ["manifest"] = "Content/Assets/assets.json",
            },
            ["world"] = new JsonObject
            {
                ["startup"] = "Content/Worlds/Main.sworld",
            },
        }.ToJsonString());

        var Environment = new Dictionary<string, string>
        {
            ["SHARPENGINE_ROOT"] = Root,
        };
        var Start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var Argument in new[]
                 {
                     "run", "--project",
                     Path.Combine(Root, "Tools/ProjectBuild/SharpEngine.ProjectBuild.csproj"),
                     "--", "build", Manifest, "--engine-root", Root, "--target", "Game",
                     "--configuration", Configuration,
                 })
        {
            Start.ArgumentList.Add(Argument);
        }
        foreach (var Pair in Environment)
        {
            Start.Environment[Pair.Key] = Pair.Value;
        }
        using var Build = Process.Start(Start)!;
        var Output = Build.StandardOutput.ReadToEndAsync();
        var Errors = Build.StandardError.ReadToEndAsync();
        Build.WaitForExit();
        File.WriteAllText(Path.Combine(Folder, "build.log"), Output.Result + Errors.Result);
        if (Build.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Game UI fixture build failed: " + Output.Result + Errors.Result);
        }
        return Manifest;
    }

    internal static void Run(string Root, string Configuration, Action<bool, string> Check,
        string? RenderBackend, Func<byte[], DecodedPng> DecodeFrame)
    {
        var Manifest = Environment.GetEnvironmentVariable("SHARPENGINE_VALIDATION_PROJECT");
        if (string.IsNullOrWhiteSpace(Manifest))
        {
            Manifest = CreateProject(Root, Configuration);
        }
        var Executable = Path.Combine(Root, "Binaries/Win64", Configuration, "Game",
            "Engine.exe");
        if (!File.Exists(Executable))
        {
            throw new FileNotFoundException(
                "Build the Game target before Game UI validation", Executable);
        }

        var Output = Path.Combine(Root, "Saved", "Validation", "GameUi");
        Directory.CreateDirectory(Output);
        var SessionId = Guid.NewGuid().ToString("N");
        var Start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Start.ArgumentList.Add("--project");
        Start.ArgumentList.Add(Manifest);
        Start.ArgumentList.Add("--mcp-session-id");
        Start.ArgumentList.Add(SessionId);
        if (RenderBackend is not null)
        {
            Start.ArgumentList.Add(RenderBackend);
            if (RenderBackend == "--render-offscreen")
            {
                Start.ArgumentList.Add("--frames");
                Start.ArgumentList.Add("1000000");
            }
        }
        Start.ArgumentList.Add("--hidden");
        using var Game = Process.Start(Start) ??
            throw new IOException("Could not start the Game executable");
        var GameOutput = Game.StandardOutput.ReadToEndAsync();
        var GameErrors = Game.StandardError.ReadToEndAsync();
        var Registry = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "SharpEngine", "GameMcpSessions", SessionId + ".json");
        try
        {
            var Deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
            while (!File.Exists(Registry) && DateTime.UtcNow < Deadline)
            {
                if (Game.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The Game exited {Game.ExitCode}: " +
                        GameErrors.GetAwaiter().GetResult());
                }
                Thread.Sleep(100);
            }
            Check(File.Exists(Registry),
                "The running Game publishes a UI observation session");

            var Descriptor = JsonNode.Parse(File.ReadAllText(Registry))!.AsObject();
            using var Pipe = new NamedPipeClientStream(".", Descriptor["pipeName"]!
                .GetValue<string>(), PipeDirection.InOut);
            Pipe.Connect(10000);
            using var Reader = new StreamReader(Pipe, new UTF8Encoding(false), false, 4096,
                true);
            using var Writer = new StreamWriter(Pipe, new UTF8Encoding(false), 4096, true)
            {
                AutoFlush = true,
            };
            var Sequence = 1;
            JsonObject Call(string Method, JsonObject Arguments)
            {
                var Request = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = Sequence++,
                    ["method"] = Method,
                    ["params"] = Arguments,
                };
                Writer.WriteLine(Request.ToJsonString());
                var Line = Reader.ReadLine() ??
                    throw new IOException($"Game closed while handling {Method}");
                return JsonNode.Parse(Line)!.AsObject();
            }
            JsonObject Tool(string Name, JsonObject Arguments) => Call("tools/call",
                new JsonObject
                {
                    ["name"] = Name,
                    ["arguments"] = Arguments,
                });

            var CatalogDirectory = Path.Combine(Output, "McpUiCatalogs", SessionId);
            Directory.CreateDirectory(CatalogDirectory);
            foreach (var Catalog in new[]
            {
                "tools", "resources", "resources/templates",
            })
            {
                File.WriteAllText(Path.Combine(CatalogDirectory, Catalog.Replace('/', '-') + ".json"),
                    Call(Catalog + "/list", new JsonObject()).ToJsonString());
            }
            var Render = Tool("render.describe", new JsonObject())["result"]!["structuredContent"]!;
            var ExpectedBackend = RenderBackend == "--render-offscreen" ? "offscreen" : RenderBackend?.Split('=').Last() ?? "opengl";
            Check(Render["backend"]!.GetValue<string>() == ExpectedBackend,
                "Game UI validation uses the requested native graphics backend");
            File.WriteAllText(Path.Combine(CatalogDirectory, "render.json"), Render.ToJsonString());

            // Frame pinning needs one completed presentation; poll until the Game has
            // rendered, exactly as an MCP client must.
            JsonObject? Snapshot = null;
            var SnapshotDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < SnapshotDeadline)
            {
                var Candidate = Tool("ui.describe", new JsonObject())["result"]
                    ?["structuredContent"]?.AsObject();
                if (Candidate?["frameId"]?.GetValue<ulong>() > 0 &&
                    Candidate["hosts"] is JsonArray)
                {
                    Snapshot = Candidate;
                    break;
                }
                Thread.Sleep(150);
            }
            Check(Snapshot is not null,
                "The running Game publishes a coherent UI snapshot");
            Snapshot ??= new JsonObject
            {
                ["hosts"] = new JsonArray(),
                ["frameId"] = 0UL,
            };
            File.WriteAllText(Path.Combine(Output, "game-ui.json"),
                Snapshot.ToJsonString(new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerOptions.Default)
                {
                    WriteIndented = true,
                }));
            var Host = Snapshot["hosts"]!.AsArray().Select(Node => Node!.AsObject())
                .SingleOrDefault(Node => Node["hostId"]?.GetValue<string>() == "game") ??
                new JsonObject
                {
                    ["elements"] = new JsonArray(),
                };
            var Elements = Host["elements"]!.AsArray().Select(Node => Node!.AsObject())
                .ToArray();

            // The presented screen must be the project asset, not the engine sample.
            Check(Elements.Any(Element => Element["semanticId"]?.GetValue<string>() ==
                    "screen") &&
                Elements.Any(Element => Element["semanticId"]?.GetValue<string>() ==
                    "caption"),
                "Game code presents the project screen selected by its Runtime Module");
            Check(!Elements.Any(Element => Element["semanticId"]?.GetValue<string>() ==
                    "fillerLabel"),
                "Showing a second screen replaces the previous document");
            var Caption = Elements.SingleOrDefault(Element =>
                Element["semanticId"]?.GetValue<string>() == "caption") ?? new JsonObject();
            Check(Caption["visible"]?.GetValue<bool>() == true &&
                Caption["bounds"]?["width"]?.GetValue<double>() > 0 &&
                Caption["text"]?.GetValue<string>() == "Composed Screen",
                "The composed screen renders the template value passed per use");
            Check(Elements.Any(Element =>
                    Element["semanticId"]?.GetValue<string>() == "screen" &&
                    Element["bounds"]!["height"]!.GetValue<double>() > 40),
                "Linked RCSS from the presented screen is applied by the runtime");

            // --- Composition: one definition, two instances -----------------------
            JsonObject? Find(string Id) => Elements.SingleOrDefault(Element =>
                Element["semanticId"]?.GetValue<string>() == Id);
            string TextOf(string Id) => Find(Id)?["text"]?.GetValue<string>() ?? "<missing>";
            Check(Find("primary") is not null && Find("secondary") is not null,
                "Both references to the shared button definition render as distinct elements");
            Check(TextOf("primary-label") == "Primary Action" &&
                TextOf("secondary-label") == "Secondary Action",
                "Each instance renders its own label parameter");
            Check(Find("primary-icon") is not null && Find("secondary-icon") is not null,
                "Both instances render the shared image element");
            Check(TextOf("primary-count") == "0" &&
                TextOf("secondary-count") == "0",
                "Each instance starts with its own independent counter");
            var PrimaryBox = Find("primary-hit")?["bounds"]?.AsObject();
            var SecondaryBox = Find("secondary-hit")?["bounds"]?.AsObject();
            Check(PrimaryBox?["width"]?.GetValue<double>() > 0 &&
                SecondaryBox?["width"]?.GetValue<double>() > 0,
                "Both instance buttons are laid out and hit-testable");

            // Each pointer event must be tied to a fresh observation: a `down` changes
            // hover/focus, so reusing its frame for `up` is stale by contract.
            void SendPointer(string Event, int X, int Y, string Button = "left")
            {
                for (var Attempt = 0; Attempt < 12; Attempt++)
                {
                    var Current = Tool("ui.describe", new JsonObject())["result"]
                        ?["structuredContent"]?.AsObject();
                    var Frame = Current?["frameId"]?.GetValue<ulong>() ?? 0;
                    if (Frame == 0)
                    {
                        Thread.Sleep(150);
                        continue;
                    }
                    var Result = Tool("ui.pointer", new JsonObject
                    {
                        ["hostId"] = "game",
                        ["event"] = Event,
                        ["button"] = Button,
                        ["x"] = X,
                        ["y"] = Y,
                        ["modifiers"] = 0,
                        ["pointerId"] = 0,
                        ["expectedFrameId"] = Frame,
                    })["result"]?["structuredContent"]?.AsObject();
                    if (Result?["accepted"]?.GetValue<bool>() == true)
                    {
                        return;
                    }
                    var Code = Result?["diagnostics"]?[0]?["code"]?.GetValue<string>();
                    if (Code != "UI_FRAME_STALE")
                    {
                        throw new InvalidOperationException(Result?.ToJsonString() ??
                            "pointer rejected");
                    }
                    Thread.Sleep(100);
                }
                throw new InvalidOperationException(
                    $"Pointer '{Event}' never reached a retained frame");
            }
            string CountAfterClick(string Id)
            {
                // The action is flushed at the next frame; poll until the recompose lands.
                for (var Wait = 0; Wait < 30; Wait++)
                {
                    Thread.Sleep(100);
                    var After = Tool("ui.describe", new JsonObject())["result"]
                        ?["structuredContent"]?.AsObject();
                    var AfterElements = After?["hosts"]?.AsArray()
                        .Select(Node => Node!.AsObject())
                        .SingleOrDefault(Node => Node["hostId"]?.GetValue<string>() == "game")
                        ?["elements"]?.AsArray().Select(Node => Node!.AsObject()).ToArray() ?? [];
                    var Count = AfterElements.SingleOrDefault(Element =>
                        Element["semanticId"]?.GetValue<string>() ==
                        Id.Replace("-hit", "-count"));
                    if (Count?["text"]?.GetValue<string>() is { } Value && Value != "0")
                    {
                        return Value;
                    }
                }
                return "0";
            }
            void ClickHit(string Id)
            {
                ObserveGame();
                var Target = Find(Id) ?? throw new InvalidOperationException(
                    $"Missing composed control '{Id}'");
                var Bounds = Target["bounds"]!.AsObject();
                var X = (int)(Bounds["x"]!.GetValue<double>() +
                    Bounds["width"]!.GetValue<double>() / 2);
                var Y = (int)(Bounds["y"]!.GetValue<double>() +
                    Bounds["height"]!.GetValue<double>() / 2);
                SendPointer("move", X, Y, "none");
                SendPointer("down", X, Y);
                SendPointer("up", X, Y);
            }
            ClickHit("primary-hit");
            var PrimaryCount = CountAfterClick("primary-hit");
            Check(PrimaryCount == "1",
                $"Clicking one instance updates only that instance's state (got {PrimaryCount})");
            var SecondaryCount = CountAfterClick("secondary-hit");
            Check(SecondaryCount == "0",
                $"The other instance of the same definition keeps its own state (got {SecondaryCount})");
            ClickHit("primary-hit");
            var PrimaryAgain = CountAfterClick("primary-hit");
            Check(PrimaryAgain == "2",
                $"Repeated clicks accumulate per instance (got {PrimaryAgain})");

            JsonObject ObserveGame()
            {
                var Current = Tool("ui.describe", new JsonObject())["result"]!["structuredContent"]!.AsObject();
                Elements = Current["hosts"]!.AsArray().Single(Node => Node!["hostId"]!.GetValue<string>() == "game")!["elements"]!
                    .AsArray().Select(Node => Node!.AsObject()).ToArray();
                return Current;
            }
            void ValueKey(string Event, int Code = 0, string Text = "", int Modifiers = 0)
            {
                for (var Attempt = 0; Attempt < 10; Attempt++)
                {
                    var Current = ObserveGame();
                    var Result = Tool("ui.key", new JsonObject
                    {
                        ["hostId"] = "game", ["event"] = Event, ["key"] = Code, ["text"] = Text,
                        ["modifiers"] = Modifiers, ["expectedFrameId"] = Current["frameId"]!.DeepClone(),
                    })["result"]!["structuredContent"]!;
                    if (Result["accepted"]?.GetValue<bool>() == true)
                    {
                        return;
                    }
                    if (Result["diagnostics"]?[0]?["code"]?.GetValue<string>() != "UI_FRAME_STALE")
                    {
                        throw new InvalidOperationException(Result.ToJsonString());
                    }
                }
                throw new InvalidOperationException("Game value input frame remained stale");
            }
            ObserveGame();
            ClickHit("common-game-number");
            ValueKey("down", 0x41, Modifiers: 1);
            ValueKey("up", 0x41, Modifiers: 1);
            ValueKey("text", Text: "12");
            ObserveGame();
            Check(TextOf("common-game-value") == "1", "A common Number input keeps its draft outside project state in Game without an Editor");
            ValueKey("down", 0x0D);
            ValueKey("up", 0x0D);
            for (var Attempt = 0; Attempt < 20; Attempt++)
            {
                ObserveGame();
                if (TextOf("common-game-value") == "12")
                {
                    break;
                }
                Thread.Sleep(50);
            }
            Check(TextOf("common-game-value") == "12", "The shared native adapter commits the Game input through its caller-supplied semantic action");
            ClickHit("common-game-number");
            ValueKey("down", 0x41, Modifiers: 1);
            ValueKey("up", 0x41, Modifiers: 1);
            ValueKey("text", Text: "-");
            ValueKey("down", 0x1B);
            ValueKey("up", 0x1B);
            ObserveGame();
            Check(TextOf("common-game-value") == "12" && Find("common-game-number")?["value"]?.GetValue<string>() == "12",
                "Escape cancels the same primitive draft policy in Game while keeping committed state: committed=" + TextOf("common-game-value") + "; input=" + Find("common-game-number")?["value"]?.GetValue<string>());

            foreach (var Id in new[]
            {
                "shared-color-R", "shared-color-G", "shared-color-B", "shared-color-A", "readonly-range", "readonly-reference",
            })
            {
                var Control = Find(Id);
                Check(Control?["visible"]?.GetValue<bool>() == true && Control["enabled"]?.GetValue<bool>() == false,
                    "The rendered common control keeps its disabled state: " + Id);
            }
            ClickHit("shared-range");
            ValueKey("down", 0x41, Modifiers: 1);
            ValueKey("up", 0x41, Modifiers: 1);
            ValueKey("text", Text: "0.6");
            ValueKey("down", 0x0D);
            ValueKey("up", 0x0D);
            ObserveGame();
            Check(Find("shared-range")?["value"]?.GetValue<string>() == "0.6" &&
                Find("shared-range-range")?["value"]?.GetValue<string>() == "0.6",
                "Range text commits into the shared slider through project state");

            ClickHit("shared-range-range");
            ObserveGame();
            var RangeNumber = double.Parse(Find("shared-range")!["value"]!.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture);
            Check(RangeNumber > 0.4 && RangeNumber < 0.6,
                "Real pointer input on the range track changes the authoritative value");

            var RangeStart = TextOf("range-state");
            var RangeCommitsBefore = int.Parse(TextOf("range-commits"));
            var RangeBounds = Find("shared-range-range")!["bounds"]!;
            var RangeLeft = RangeBounds["x"]!.GetValue<double>();
            var RangeWidth = RangeBounds["width"]!.GetValue<double>();
            var SliderY = (int)(RangeBounds["y"]!.GetValue<double>() + RangeBounds["height"]!.GetValue<double>() / 2);
            int SliderX(double Value) { return (int)(RangeLeft + 6 + (RangeWidth - 12) * Value); }
            SendPointer("move", SliderX(RangeNumber), SliderY, "none");
            SendPointer("down", SliderX(RangeNumber), SliderY);
            SendPointer("move", SliderX(0.8), SliderY, "none");
            SendPointer("move", SliderX(0.7), SliderY, "none");
            CaptureReference("range-drag-preview");
            var RangeDraft = double.Parse(Find("shared-range-range")!["value"]!.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture);
            var NumberDraft = double.Parse(Find("shared-range")!["value"]!.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture);
            Check(RangeDraft > 0.65 && Math.Abs(RangeDraft - NumberDraft) < 0.001 &&
                TextOf("range-state") == RangeStart && int.Parse(TextOf("range-commits")) == RangeCommitsBefore,
                "Dragging a Range updates its numeric draft without submitting intermediate project changes: " +
                $"slider={RangeDraft}, number={NumberDraft}");
            SendPointer("up", SliderX(0.7), SliderY + 30);
            CaptureReference("range-drag-committed");
            Check(Math.Abs(double.Parse(TextOf("range-state"), System.Globalization.CultureInfo.InvariantCulture) - RangeDraft) < 0.001 &&
                int.Parse(TextOf("range-commits")) == RangeCommitsBefore + 1,
                "Releasing outside the Range commits its final draft once");
            SendPointer("down", SliderX(RangeDraft), SliderY);
            SendPointer("move", SliderX(0.2), SliderY, "none");
            ValueKey("down", 0x1B);
            ValueKey("up", 0x1B);
            SendPointer("up", SliderX(0.2), SliderY);
            CaptureReference("range-drag-cancelled");
            Check(Math.Abs(double.Parse(Find("shared-range")!["value"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture) - RangeDraft) < 0.001 &&
                Math.Abs(double.Parse(Find("shared-range-range")!["value"]!.GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture) - RangeDraft) < 0.001 &&
                int.Parse(TextOf("range-commits")) == RangeCommitsBefore + 1,
                "Escape cancels a held slider gesture and restores both controls without a commit");
            ClickHit("shared-range");
            ValueKey("down", 0x09);
            ValueKey("up", 0x09);
            ObserveGame();
            var KeyboardStart = double.Parse(TextOf("range-state"), System.Globalization.CultureInfo.InvariantCulture);
            var KeyboardCommits = int.Parse(TextOf("range-commits"));
            ValueKey("down", 0x27);
            ObserveGame();
            var KeyboardDraft = double.Parse(Find("shared-range")!["value"]!.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture);
            CaptureReference("range-keyboard-preview");
            Check(KeyboardDraft > KeyboardStart && Math.Abs(KeyboardDraft -
                    double.Parse(Find("shared-range-range")!["value"]!.GetValue<string>(),
                        System.Globalization.CultureInfo.InvariantCulture)) < 0.001 &&
                int.Parse(TextOf("range-commits")) == KeyboardCommits,
                $"An arrow key previews the same slider and numeric draft before committing: start={KeyboardStart}, draft={KeyboardDraft}, " +
                $"slider={Find("shared-range-range")!["value"]}, commits={TextOf("range-commits")}/{KeyboardCommits}");
            ValueKey("up", 0x27);
            CaptureReference("range-keyboard-committed");
            Check(Math.Abs(double.Parse(TextOf("range-state"), System.Globalization.CultureInfo.InvariantCulture) - KeyboardDraft) < 0.001 &&
                int.Parse(TextOf("range-commits")) == KeyboardCommits + 1,
                "Releasing the arrow key commits its final value once");
            ClickHit("readonly-range-range");
            ObserveGame();
            Check(Find("readonly-range-range")!["value"]!.GetValue<string>() == "0.75" &&
                Find("readonly-range")!["value"]!.GetValue<string>() == "0.75" &&
                int.Parse(TextOf("range-commits")) == KeyboardCommits + 1,
                "Disabled slider and numeric controls reject changes and share the same value");

            JsonObject CaptureReference(string Name)
            {
                var Current = ObserveGame();
                var Frame = Call("resources/read", new JsonObject
                {
                    ["uri"] = $"sharpengine://ui/frames/{Current["frameId"]!.GetValue<ulong>()}/game.png",
                });
                File.WriteAllBytes(Path.Combine(Output, Name + ".png"),
                    Convert.FromBase64String(Frame["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
                File.WriteAllText(Path.Combine(Output, Name + ".json"), Current.ToJsonString());
                return Current;
            }
            ObserveGame();
            Check(Find("shared-reference")?["hitTarget"]?.GetValue<bool>() == true,
                "The Reference value area is visible and hit-testable");
            ClickHit("shared-reference");
            CaptureReference("reference-open");
            ValueKey("down", 0x28);
            ValueKey("up", 0x28);
            ValueKey("down", 0x0D);
            ValueKey("up", 0x0D);
            ObserveGame();
            Check(Find("shared-reference")?["value"]?.GetValue<string>() == "project://materials/blue",
                "Opening the Reference and choosing its next option updates the selected asset");
            ClickHit("readonly-reference");
            ObserveGame();
            Check(Find("readonly-reference")?["value"]?.GetValue<string>() == "project://materials/green" &&
                Find("readonly-reference")?["enabled"]?.GetValue<bool>() == false,
                "Clicking a disabled Reference preserves its value and disabled state");
            CaptureReference("reference-selected");

            const string PickerId = "editable-color-R-picker";
            ClickHit(PickerId + "-open");
            CaptureReference("color-picker-initial");
            Check(Find(PickerId + "-sv")?["visible"]?.GetValue<bool>() == true &&
                Find(PickerId + "-hue")?["visible"]?.GetValue<bool>() == true,
                "The common Color control opens its saturation/value palette and hue strip");
            ClickHit(PickerId + "-hue");
            ClickHit(PickerId + "-sv");
            ClickHit(PickerId + "-alpha");
            var ColorDraft = CaptureReference("color-picker-open");
            Check(TextOf("color-commits") == "0" && Find("editable-color-R")?["value"]?.GetValue<string>() == "0.2",
                "Palette preview leaves committed RGBA channels and transaction count unchanged");
            var ColorFrame = Call("resources/read", new JsonObject
            {
                ["uri"] = $"sharpengine://ui/frames/{ColorDraft["frameId"]!.GetValue<ulong>()}/game.png",
            });
            var PalettePixels = DecodeFrame(Convert.FromBase64String(ColorFrame["result"]!["contents"]![0]!["blob"]!.GetValue<string>()));
            var PaletteBounds = Find(PickerId + "-sv")!["bounds"]!;
            var PaletteLeft = (int)PaletteBounds["x"]!.GetValue<double>();
            var PaletteTop = (int)PaletteBounds["y"]!.GetValue<double>();
            var PaletteWidth = (int)PaletteBounds["width"]!.GetValue<double>();
            var PaletteHeight = (int)PaletteBounds["height"]!.GetValue<double>();
            var Light = PalettePixels.Pixel(PaletteLeft + 8, PaletteTop + 8);
            var Dark = PalettePixels.Pixel(PaletteLeft + 8, PaletteTop + PaletteHeight - 8);
            var Saturated = PalettePixels.Pixel(PaletteLeft + PaletteWidth - 8, PaletteTop + 8);
            Check(Light.R + Light.G + Light.B > Dark.R + Dark.G + Dark.B + 300 && Saturated != Light,
                "The actual captured palette contains both saturation and brightness gradients");
            ClickHit(PickerId + "-cancel");
            ObserveGame();
            Check(TextOf("color-commits") == "0" && Find(PickerId + "-sv")?["visible"]?.GetValue<bool>() == false,
                "Cancel closes the common palette without a color commit");
            ClickHit(PickerId + "-open");
            ClickHit(PickerId + "-hue");
            ClickHit(PickerId + "-sv");
            ClickHit(PickerId + "-alpha");
            ClickHit(PickerId + "-apply");
            ObserveGame();
            Check(TextOf("color-commits") == "1" && Find("editable-color-R")?["value"]?.GetValue<string>() != "0.2",
                "Apply commits the entire RGBA color exactly once through its owner action");
            var AppliedAlpha = float.Parse(Find("editable-color-A")!["value"]!.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture);
            Check(AppliedAlpha > 0.3f && AppliedAlpha < 0.7f,
                "The first opacity slider change commits its current value rather than the preceding attribute value");
            CaptureReference("color-picker-applied");
            ClickHit(PickerId + "-open");
            ClickHit(PickerId + "-open");
            ObserveGame();
            Check(TextOf("color-commits") == "1" && Find(PickerId + "-sv")?["visible"]?.GetValue<bool>() == false,
                "Toggling an open palette closed preserves its committed color");
            ClickHit(PickerId + "-open");
            ClickHit(PickerId + "-sv");
            ValueKey("down", 0x1B);
            ValueKey("up", 0x1B);
            ObserveGame();
            Check(TextOf("color-commits") == "1" && Find(PickerId + "-sv")?["visible"]?.GetValue<bool>() == false,
                "Escape cancels the color preview without committing its draft");
            ClickHit("shared-color-R-picker-open");
            ObserveGame();
            Check(Find("shared-color-R-picker-sv")?["visible"]?.GetValue<bool>() == false && TextOf("color-commits") == "1",
                "The disabled Color preview does not open an interactive palette");

            // Read a freshly observed frame: the earlier snapshot predates the clicks,
            // and its pinned frame may already have left the bounded cache.
            JsonObject? Final = null;
            for (var Attempt = 0; Attempt < 20; Attempt++)
            {
                var Candidate = Tool("ui.describe", new JsonObject())["result"]
                    ?["structuredContent"]?.AsObject();
                if (Candidate?["frameId"]?.GetValue<ulong>() > 0)
                {
                    Final = Candidate;
                }
                if (Final is not null)
                {
                    var Probe = Call("resources/read", new JsonObject
                    {
                        ["uri"] = $"sharpengine://ui/frames/" +
                            $"{Final["frameId"]!.GetValue<ulong>()}/game.png",
                    })["result"]?["contents"]?[0]?["blob"]?.GetValue<string>();
                    if (Probe is not null)
                    {
                        Check(true,
                            "The Game publishes a rendered frame for the presented screen");
                        File.WriteAllBytes(Path.Combine(Output, "game-ui.png"),
                            Convert.FromBase64String(Probe));
                        var Image = DecodeFrame(Convert.FromBase64String(Probe));
                        foreach (var Id in new[]
                        {
                            "shared-reference", "readonly-reference",
                        })
                        {
                            var Bounds = Find(Id)!["bounds"]!;
                            var Left = (int)Math.Floor(Bounds["x"]!.GetValue<double>());
                            var Top = (int)Math.Floor(Bounds["y"]!.GetValue<double>());
                            var Width = Bounds["width"]!.GetValue<double>();
                            var Height = Bounds["height"]!.GetValue<double>();
                            var MidX = Left + (int)(Width / 2);
                            var MidY = Top + (int)(Height / 2);
                            var Background = Image.Pixel(Left - 2, MidY);
                            Check(Math.Abs(Height - 28) < 0.1 &&
                                Image.Pixel((int)Math.Ceiling(Left + Width) + 2, MidY) == Background &&
                                Image.Pixel(MidX, (int)Math.Ceiling(Top + Height) + 2) == Background,
                                "Reference pixels stay within its 28px control bounds: " + Id);
                        }
                        var Clipped = Image.Pixel(1100, 610);
                        var Outside = Image.Pixel(1170, 610);
                        Check(Clipped.R > 200 && Clipped.G < 60 && Clipped.B > 80 &&
                            !(Outside.R > 200 && Outside.G < 60 && Outside.B > 80),
                            $"Transformed UI obeys window-space clipping: inside={Clipped}, outside={Outside}");
                        var AlphaBounds = Find("alpha-test")!["bounds"]!;
                        var Alpha = Image.Pixel((int)AlphaBounds["x"]!.GetValue<double>() + 40,
                            (int)AlphaBounds["y"]!.GetValue<double>() + 30);
                        Check(Alpha.R is >= 120 and <= 135 && Alpha.G < 5 && Alpha.B < 5,
                            $"UI premultiplied alpha has no doubled triangle seam: {Alpha}");
                        var RangeControl = Find("shared-range")!["bounds"]!;
                        var RangeX = (int)RangeControl["x"]!.GetValue<double>();
                        var RangeY = (int)RangeControl["y"]!.GetValue<double>();
                        var ForegroundPixels = 0;
                        for (var Y = RangeY + 6; Y < RangeY + 24; Y++)
                        {
                            for (var X = RangeX + 3; X < RangeX + 65; X++)
                            {
                                var Pixel = Image.Pixel(X, Y);
                                if (Pixel.R > 100 && Pixel.G > 100 && Pixel.B > 100)
                                {
                                    ForegroundPixels++;
                                }
                            }
                        }
                        Check(ForegroundPixels > 10, "The matching frame contains readable glyph pixels in the actual Range input");
                        // Also keep the post-click snapshot, which is what proves the
                        // instance state after the interactions.
                        File.WriteAllText(Path.Combine(Output, "game-ui-after-click.json"),
                            Final.ToJsonString(new System.Text.Json.JsonSerializerOptions(
                                System.Text.Json.JsonSerializerOptions.Default)
                            {
                                WriteIndented = true,
                            }));
                        break;
                    }
                    Final = null;
                }
                Thread.Sleep(200);
            }
            Check(Final is not null,
                "The Game publishes a rendered frame for the presented screen");
            ClickHit("collection-show");
            CaptureReference("collection-expanded");
            Check(TextOf("editable-list-count") == "2" && Find("collection-value-0")?["visible"]?.GetValue<bool>() == true &&
                Find("collection-item-0-up")?["enabled"]?.GetValue<bool>() == false &&
                Find("collection-item-1-down")?["enabled"]?.GetValue<bool>() == false,
                "The common collection shows its count, primitive values and disabled boundary moves");
            ClickHit("collection-value-0");
            ValueKey("down", 0x41, Modifiers: 1);
            ValueKey("up", 0x41, Modifiers: 1);
            ValueKey("text", Text: "12");
            ObserveGame();
            Check(TextOf("collection-state") == "3,8", "Collection element drafts do not change project state");
            ValueKey("down", 0x0D);
            ValueKey("up", 0x0D);
            ObserveGame();
            Check(TextOf("collection-state") == "12,8", "Enter commits one collection element and preserves its sibling");
            ClickHit("collection-value-0");
            ValueKey("down", 0x41, Modifiers: 1);
            ValueKey("up", 0x41, Modifiers: 1);
            ValueKey("text", Text: "-");
            ValueKey("down", 0x1B);
            ValueKey("up", 0x1B);
            ObserveGame();
            Check(TextOf("collection-state") == "12,8" && Find("collection-value-0")?["value"]?.GetValue<string>() == "12",
                "Escape discards the collection element draft through the shared value adapter");
            ClickHit("collection-item-0-down");
            ObserveGame();
            Check(TextOf("collection-state") == "8,12", "Real pointer Down reorders the collection");
            ClickHit("collection-item-1-up");
            ObserveGame();
            Check(TextOf("collection-state") == "12,8", "Real pointer Up restores the order");
            ClickHit("editable-list-add");
            ObserveGame();
            Check(TextOf("editable-list-count") == "3" && TextOf("collection-state") == "12,8,0",
                "Real pointer Add appends one default item");
            ClickHit("collection-item-2-remove");
            ObserveGame();
            Check(TextOf("collection-state") == "12,8", "Remove deletes the selected item");
            ClickHit("collection-undo");
            ObserveGame();
            Check(TextOf("collection-state") == "12,8,0", "The owner can undo one collection operation");
            ClickHit("collection-redo");
            ObserveGame();
            Check(TextOf("collection-state") == "12,8", "The owner can redo one collection operation");
            foreach (var Id in new[] { "disabled-list-add", "disabled-item-0-remove", "disabled-list-x", "disabled-list-text" })
            {
                Check(Find(Id)?["enabled"]?.GetValue<bool>() == false, "The collection retains disabled value and command styling: " + Id);
            }
            ClickHit("disabled-list-add");
            ObserveGame();
            Check(TextOf("disabled-list-count") == "2", "Clicking a disabled collection command changes no values");
            ClickHit("editable-list-toggle");
            ObserveGame();
            Check(Find("collection-value-0") is null && Find("disabled-list-x")?["visible"]?.GetValue<bool>() == true &&
                TextOf("collection-state") == "12,8", "Folding hides child controls and leaves the other collection independent");
            CaptureReference("collection-collapsed");
            ClickHit("editable-list-toggle");
            ClickHit("disabled-list-toggle");
            ObserveGame();
            Check(Find("disabled-list-x") is null && Find("collection-value-0")?["visible"]?.GetValue<bool>() == true,
                "Read-only collection folding remains available");
            ClickHit("disabled-list-toggle");
            CaptureReference("collection-edited");
            ClickHit("empty-list-add");
            ObserveGame();
            Check(TextOf("empty-list-count") == "1" && Find("empty-value-0")?["visible"]?.GetValue<bool>() == true,
                "An initially empty collection creates its first value through real Add input");
            ClickHit("empty-item-0-remove");
            ObserveGame();
            Check(TextOf("empty-list-count") == "0" && Find("empty-value-0") is null,
                "Removing the last element returns the collection to its empty state");

            ClickHit("map-show");
            CaptureReference("map-initial");
            var InitialMapData = TextOf("map-data-state");
            Check(TextOf("map-order") == "alpha,zeta" &&
                TextOf("interactive-map-count") == "2",
                "Offscreen Game renders the shared Map editor and its initial ordinal order");
            ClickHit("map-entry-zeta-up");
            ObserveGame();
            CaptureReference("map-reordered");
            Check(TextOf("map-order") == "zeta,alpha" &&
                TextOf("map-data-state") == InitialMapData,
                "Real pointer input changes only the Map editor view order");
            ClickHit("map-value-alpha-X");
            ValueKey("down", 0x41, Modifiers: 1);
            ValueKey("up", 0x41, Modifiers: 1);
            ValueKey("text", Text: "21");
            ValueKey("down", 0x0D);
            ValueKey("up", 0x0D);
            ObserveGame();
            CaptureReference("map-edited");
            Check(TextOf("map-data-state") == "alpha:21,2,3|zeta:9,8,7" &&
                TextOf("map-order") == "zeta,alpha",
                "Real focused input commits one Map vector axis without changing visual order");
            Console.WriteLine("PASS: Game UI presents the composed project screen");
        }
        finally
        {
            if (!Game.HasExited)
            {
                Game.Kill(true);
                Game.WaitForExit(10000);
            }
            File.WriteAllText(Path.Combine(Output, "game.log"),
                GameOutput.GetAwaiter().GetResult() + GameErrors.GetAwaiter().GetResult());
            if (File.Exists(Registry))
            {
                File.Delete(Registry);
            }
        }
    }
}
