using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using SharpEngine.Editor;
using SharpEngine.Interop;
using SharpEngine.UI;

internal static unsafe class ContentManagerMenuValidation
{
    private static readonly Dictionary<(string Host, string Id), string> Markup = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte Publish(byte* Host, byte* Id, byte* Value)
    {
        Markup[(Text(Host), Text(Id))] = Text(Value);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static nint CreateHost(byte* Host, byte* Title) { return 1; }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte DestroyHost(byte* Host) { return 1; }

    private static string Text(byte* Value) { return Marshal.PtrToStringUTF8((nint)Value)!; }

    internal static void Run(Action<bool, string> Check)
    {
        Markup.Clear();
        var Api = new NativeEngineApiTable
        {
            Version = 12,
            SetHostMarkup = &Publish,
            CreateDockWindow = &CreateHost,
            DestroyDockWindow = &DestroyHost,
        };
        var Runtime = new EditorRuntime(0, 900, 600, (nint)(&Api));
        Runtime.Initialize();
        try
        {
            Runtime.InvokeUiAction("editor.dock-reset-layout");
            var BeforeWorld = Runtime.World!.Serialize();
            var BeforeRevision = Runtime.DocumentRevision;
            Runtime.InvokeUiAction("editor.content.source:content:engine");

            XElement Menu(string Host = "main")
            {
                return XElement.Parse(Markup[(Host, "contextMenu")]);
            }
            string Command(string Id, string Host = "main")
            {
                var Button = Menu(Host).Descendants("button").Single(Item =>
                    (string?)Item.Attribute("id") == "context-command-" + Id);
                // A disabled entry carries no action by contract: availability removes
                // dispatchability instead of only greying the entry out.
                return Button.Attribute("data-action")?.Value ??
                    throw new InvalidOperationException(
                        $"Command '{Id}' is disabled and must not expose an action");
            }
            void Request(string Kind, string Source, string Target, string Pane = "content", string Host = "main")
            {
                var Request = new JsonObject
                {
                    ["route"] = new JsonArray(ContentManagerView.ScopeId(Pane)),
                    ["x"] = 880,
                    ["y"] = 580,
                    ["payload"] = new JsonObject
                    {
                        ["kind"] = Kind,
                        ["source"] = Source,
                        ["target"] = Target,
                    }.ToJsonString(),
                };
                Runtime.InvokeUiAction($"editor.host-action:{Host}:ui.context-menu:" + Request.ToJsonString());
            }
            void Execute(string Action, string Host = "main")
            {
                Runtime.InvokeUiAction($"editor.host-action:{Host}:{Action}");
            }

            var Opened = "";
            using var Opener = Runtime.AssetOpeners.Register(new EditorAssetOpener(
                "validation.context-open", "Mesh", Context => Opened = Context.Asset.Id, Priority: 100));
            string[] BrowserCommands = ["core.content-up", "core.content-refresh", "core.content-clear-filters",
                "core.content-view-tile", "core.content-view-list"];
            void BrowserItems(bool Expected, string Host = "main")
            {
                var Ids = Menu(Host).Descendants("button").Select(Button => (string?)Button.Attribute("id")).ToHashSet();
                Check(BrowserCommands.All(Id => Ids.Contains("context-command-" + Id) == Expected),
                    Expected ? "Background owns browser commands" : "Target menus exclude browser commands");
            }
            var Workspace = XElement.Parse("<root>" + Markup[("main", "workspace")] + "</root>");
            Check(Workspace.Descendants().Single(Element => (string?)Element.Attribute("id") == "content-pane")
                .Attribute("data-ui-context-scope")?.Value == ContentManagerView.ScopeId("content"),
                "The Content Manager layout root declares its event scope");
            var BeforeChildRequest = Runtime.Selection;
            var ChildHandled = false;
            using (var ChildScope = Runtime.Context.ContextMenuRouter.Register("validation.child", Event =>
            {
                ChildHandled = true;
                return true;
            }))
            {
                Runtime.InvokeUiAction("ui.context-menu:" + new JsonObject
                {
                    ["route"] = new JsonArray("validation.child", ContentManagerView.ScopeId("content")),
                    ["x"] = 10,
                    ["y"] = 10,
                    ["payload"] = new JsonObject
                    {
                        ["kind"] = "asset",
                        ["source"] = "engine",
                        ["target"] = "engine://meshes/cube",
                    }.ToJsonString(),
                }.ToJsonString());
            }
            Check(ChildHandled && Runtime.Selection == BeforeChildRequest,
                "A nested UI consumer stops the Content Manager without an EditorRuntime-specific branch");
            Request("asset", "engine", "engine://meshes/cube");
            BrowserItems(false);
            Check(Menu().Descendants("button").Single(Button =>
                (string?)Button.Attribute("id") == "context-command-asset-delete").Attribute("disabled") is not null,
                "The pane-owned delete command appears disabled for engine assets");
            Check(Runtime.Selection.Id == "engine://meshes/cube" &&
                Menu().Attribute("data-anchor-x")?.Value == "880",
                "Content requests select their own target and publish host-local anchors");
            var Open = Command("core.content-open");
            Runtime.InvokeUiAction("editor.content.select:content:engine://meshes/plane");
            Execute(Open);
            Check(Opened == "engine://meshes/cube", "Open uses the captured target after another selection");
            Opened = "";
            Execute(Open);
            Check(Opened.Length == 0, "A menu execution token cannot be replayed");

            Runtime.InvokeUiAction("editor.dock-toggle:content-2");
            var Floating = JsonNode.Parse(Runtime.DescribeEditorJson())!["layout"]!["floatingHosts"]!
                .AsArray().Single()!["hostId"]!.GetValue<string>();
            Runtime.InvokeUiAction("editor.content.source:content-2:engine");
            Request("content", "engine", "", "content-2", Floating);
            BrowserItems(true, Floating);
            Check(!Menu(Floating).Descendants("button").Any(Button =>
                (string?)Button.Attribute("id") == "context-command-asset-delete"),
                "Background menus exclude the pane-owned asset delete command");
            var BackgroundSession = JsonNode.Parse(Runtime.DescribeContentJson())!["contextMenus"]!["sessions"]!
                .AsArray().Single(Node => Node!["hostId"]!.GetValue<string>() == Floating)!;
            Check(BackgroundSession["targetKind"]!.GetValue<string>() == "None",
                "A background request does not masquerade as a folder or asset target");
            var List = Command("core.content-view-list", Floating);
            var SecondSelection = Runtime.ContentManagers!.Get("content-2").SelectedPath;
            Runtime.InvokeUiAction("editor.content.activate:content");
            Execute(List, "main");
            Check(Runtime.ContentManagers.Get("content-2").ViewMode == SharpEngine.Assets.ContentViewMode.Tile,
                "A different host cannot execute a floating menu token");
            Execute(List, Floating);
            Check(Runtime.ContentManagers.Get("content-2").ViewMode == SharpEngine.Assets.ContentViewMode.List &&
                Runtime.ContentManagers.Get("content").ViewMode == SharpEngine.Assets.ContentViewMode.Tile &&
                Runtime.ContentManagers.Get("content-2").SelectedPath == SecondSelection,
                "View commands act on the captured manager instead of the later active manager");

            Request("folder", "engine", "Assets", "content-2", Floating);
            BrowserItems(false, Floating);
            Check(!Menu(Floating).Descendants("button").Any(Button =>
                (string?)Button.Attribute("id") == "context-command-asset-delete"),
                "Folder menus exclude the pane-owned asset delete command");
            Check(Runtime.ContentManagers.Get("content-2").CurrentFolder == "",
                "Right-click selects a folder without entering it");
            Execute(Command("core.content-open", Floating), Floating);
            Check(Runtime.ContentManagers.Get("content-2").CurrentFolder == "Assets",
                "Folder Open uses the ordinary browser navigation");
            Request("content", "engine", "Assets", "content-2", Floating);
            Execute(Command("core.content-up", Floating), Floating);
            Check(Runtime.ContentManagers.Get("content-2").CurrentFolder == "", "Background menu can navigate to the parent");

            var Called = 0;
            var Enabled = true;
            EditorCommandContext? Captured = null;
            using var Extension = Runtime.Commands.Register(new EditorCommand("validation.context-extra", "Extra <&>",
                Context =>
                {
                    Captured = Context;
                    Called++;
                }, Context => Enabled));
            using var Category = Runtime.ContextMenus.RegisterCategory(new EditorContextMenuCategory(
                EditorContextMenuKind.Asset, "validation", Label: "Validation <&>"));
            using var Placement = Runtime.ContextMenus.RegisterItem(new EditorContextMenuItem(
                EditorContextMenuKind.Asset, "validation", "validation.context-extra"));
            Request("asset", "engine", "engine://meshes/cube");
            var Extra = Command("validation.context-extra");
            Check(Menu().Descendants("div").Single(Element =>
                (string?)Element.Attribute("id") == "menu-category-validation").Value == "Validation <&>",
                "Context categories render their escaped display label instead of only a divider");
            var DisabledToken = Menu().Attribute("data-menu-session")!.Value;
            Enabled = false;
            Execute(Extra);
            Check(Called == 0, "Extension CanExecute is rechecked immediately before execution");
            Check(Menu().Attribute("data-menu-session")?.Value == DisabledToken,
                "A command that becomes unavailable preserves the current popup session");
            Enabled = true;
            Request("asset", "engine", "engine://meshes/cube");
            Extra = Command("validation.context-extra");
            Runtime.InvokeUiAction("editor.select-entity:player");
            Execute(Extra);
            Check(Called == 1 && Captured!.SelectedAsset!.Id == "engine://meshes/cube" &&
                Captured.SelectedEntity is null && Captured.ContentPaneId == "content",
                "Legacy extension placements receive a bound asset context independent of global selection");

            Request("asset", "engine", "engine://meshes/cube");
            Extra = Command("validation.context-extra");
            Extension.Dispose();
            using var Replacement = Runtime.Commands.Register(new EditorCommand(
                "validation.context-extra", "Replacement", Context => Called += 100));
            Execute(Extra);
            Check(Called == 1, "Reloading a command invalidates its earlier menu binding");

            var TestAsset = Runtime.Assets!.Find("project://materials/validation")!;
            var TestPath = Runtime.Assets.ResolvePath(TestAsset)!;
            var DeleteOwner = Runtime.ContentManagers.Get("content");
            var DeleteHandler = DeleteOwner.DeleteRequested;
            string? DeleteTarget = null;
            DeleteOwner.DeleteRequested = Asset => DeleteTarget = Asset.Id;
            try
            {
                Request("asset", "project", TestAsset.Id);
                var Delete = Command("asset-delete");
                Check(Menu().Descendants("button").Single(Button =>
                    (string?)Button.Attribute("id") == "context-command-asset-delete").Attribute("disabled") is null &&
                    Runtime.Commands.Commands.All(Item => Item.Id != "asset-delete"),
                    "Private delete is enabled for a project asset without entering the global command table");
                Runtime.InvokeUiAction("editor.content.select:content:engine://meshes/plane");
                Execute(Delete);
                Check(DeleteTarget == TestAsset.Id,
                    "Pane-owned delete executes against the captured asset after the pane selection changes");
                DeleteTarget = null;
                Execute(Delete);
                Check(DeleteTarget is null, "Private delete tokens cannot be replayed");
                DeleteOwner.SelectFolder(SharpEngine.Assets.AssetSource.Project, "Worlds");
                var EmptyPaneContext = new EditorCommandContext(Runtime, DeleteOwner);
                Check(EmptyPaneContext.SelectedAsset is null,
                    "A pane without an asset selection never falls back to the editor's selected asset");
            }
            finally
            {
                DeleteOwner.DeleteRequested = DeleteHandler;
            }
            var OriginalBytes = File.ReadAllBytes(TestPath);
            try
            {
                Request("asset", "project", TestAsset.Id);
                Extra = Command("validation.context-extra");
                File.Delete(TestPath);
                Execute(Extra);
                Check(Called == 1, "A deleted asset rejects commands even while registry Exists metadata is cached");
                Request("asset", "project", TestAsset.Id);
                Check(Menu().Descendants("button").Single(Button =>
                    (string?)Button.Attribute("id") == "context-command-core.content-open")
                    .Attribute("disabled") is not null, "A missing asset disables Open at menu construction");
            }
            finally
            {
                File.WriteAllBytes(TestPath, OriginalBytes);
            }

            Request("asset", "engine", "engine://meshes/cube");
            Open = Command("core.content-open");
            var Token = Menu().Attribute("data-menu-session")!.Value;
            Execute("editor.menu-closed:" + Token);
            Execute(Open);
            Check(Opened.Length == 0 && Markup[("main", "contextMenu")].Length == 0,
                "Dismissed menus clear presentation and cannot execute stale actions");

            Request("asset", "engine", "engine://meshes/cube");
            Open = Command("core.content-open");
            Runtime.InvokeUiAction("editor.dock-reset-layout");
            Execute(Open);
            Check(Opened.Length == 0, "Replacing the browser layout invalidates captured targets");
            Check(JsonNode.Parse(Runtime.DescribeContentJson())!["contextRouting"]!["scopes"]!.AsArray().Count == 4 &&
                Runtime.Commands.Commands.Count(Command => Command.Id.StartsWith("core.content-", StringComparison.Ordinal)) == 7,
                "Replacing browser instances releases old scopes and command bindings before rebuilding");
            using (var Module = new SharpEngine.Modules.ModuleRegistrationScope())
            {
                using var Entered = Module.Enter();
                Runtime.InvokeUiAction("editor.dock-reset-layout");
            }
            Check(Runtime.Commands.Commands.Count(Command => Command.Id.StartsWith("core.content-", StringComparison.Ordinal)) == 7,
                "Browser command ownership does not transfer to the module that requested a layout reset");
            Request("asset", "project", TestAsset.Id);
            var StaleDelete = Command("asset-delete");
            Check(Menu().Descendants("button").Single(Button =>
                (string?)Button.Attribute("id") == "context-command-asset-delete").Attribute("disabled") is null,
                "Module disposal preserves the pane-owned delete command and its menu placement");
            Runtime.ReloadAssetRegistry();
            Execute(StaleDelete);
            Check(Runtime.Assets!.Find(TestAsset.Id) is not null &&
                Runtime.ContextMenuRouter.Describe()["scopes"]!.AsArray().Count == 4,
                "Registry reload recreates browser views and invalidates private command sessions");
            Request("asset", "project", TestAsset.Id);
            Check(Menu().Descendants("button").Any(Button =>
                (string?)Button.Attribute("id") == "context-command-asset-delete"),
                "Reloaded browser views still expose their pane-owned commands");
            var Selection = Runtime.Selection;
            Request("folder", "engine", "../");
            Check(Runtime.Selection == Selection, "Invalid browser paths do not change selection or open a menu");
            Request("asset", "engine", "engine://meshes/cube", "content-2", "main");
            Check(Runtime.Selection == Selection, "Requests cannot target a hidden or differently hosted pane");
            Check(Runtime.World.Serialize() == BeforeWorld && Runtime.DocumentRevision == BeforeRevision,
                "Content menu interaction never changes Authoring data or its revision");
            Check(JsonNode.Parse(Runtime.DescribeContentJson())!["contextMenus"]!["schemaVersion"]!.GetValue<int>() == 1,
                "Menu sessions are discoverable as versioned transient content state");

            var Items = new List<UiMenuItem>
            {
                new("item", "<Label & text>", "action<&", false, true),
            };
            var Ui = new UiMenu([new UiMenuSection("section", Items, "Category <&>")]);
            Items.Clear();
            var Layout = XElement.Parse(Ui.BuildLayout(new UiLayoutContext(100, 100)));
            Check(Layout.Descendants("button").Single().Value.Contains("<Label & text>") &&
                Layout.Descendants("button").Single().Attribute("disabled") is not null &&
                Layout.Descendants("button").Single().Attribute("data-action") is null,
                "Generic menu rendering escapes labels, snapshots definitions without World " +
                "dependencies, and gives a disabled entry no action to dispatch");
            Check(Layout.Descendants("div").Single(Element =>
                (string?)Element.Attribute("id") == "menu-category-section").Value == "Category <&>" &&
                Layout.Descendants("button").Single().Attribute("class")!.Value.Contains("disabled"),
                "Generic menu sections retain labels and disabled items carry their visual state");
            var Children = new List<UiMenuItem>
            {
                new("nested-leaf", "Nested", "nested-action"),
            };
            var NestedMenu = new UiMenu([new UiMenuSection("nested", [
                new UiMenuItem("nested-parent", "Parent", "", Children: Children),
                new UiMenuItem("disabled-parent", "Disabled", "", false, Children: [new("disabled-leaf", "Leaf", "", false)]),
            ])]);
            Children.Clear();
            var NestedLayout = XElement.Parse(NestedMenu.BuildLayout(new UiLayoutContext(100, 100)));
            Check(NestedLayout.Descendants("button").Single(Element =>
                (string?)Element.Attribute("id") == "nested-parent").Attribute("data-menu-submenu") is not null &&
                NestedLayout.Descendants("div").Count(Element =>
                    (string?)Element.Attribute("class") == "menu-popup context-submenu") == 2 &&
                NestedLayout.Descendants("button").Single(Element =>
                    (string?)Element.Attribute("id") == "disabled-parent").Attribute("data-menu-submenu") is null,
                "Nested menu definitions are deep snapshots and disabled parents expose no submenu trigger");
            Runtime.InvokeUiAction("editor.play");
            Runtime.Shutdown();
            Check(Runtime.ContextMenuRouter.Describe()["scopes"]!.AsArray().Count == 0,
                "Stopping a playing editor releases browser scopes after the final runtime-to-authoring UI refresh");
        }
        finally
        {
            Runtime.Shutdown();
        }
    }
}
