using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using SharpEngine.Editor;
using SharpEngine.World;
using SharpEngine.Timeline;

// Exercise the public API with the same versioned native callback boundary used
// by the editor, without a window. UI assertions inspect the submitted RML.
internal static unsafe class EditorExtensionValidation
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeApi
    {
        public uint Version;
        public nint SetWindowTitle, BeginUi, UiPanel, UiText, UiButton, EndUi, UiInput;
        public delegate* unmanaged[Cdecl]<byte*, byte*, void> SetUiText;
        public delegate* unmanaged[Cdecl]<float*, float*, float*, float*, byte> GetViewportRect;
        public nint SubmitViewportFrame;
        public delegate* unmanaged[Cdecl]<byte*, byte*, void> SetUiMarkup;
        public delegate* unmanaged[Cdecl]<byte*, byte*, byte*, void> SetUiProperty;
        public nint ViewportInput;
    }

    private static readonly Dictionary<string, string> Text = new();
    private static string _toolbar = "";
    private static string _menus = "";
    private static string _contextMenu = "";
    private static int _toolbarUpdates;
    private static readonly Dictionary<string, string> Properties = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void SetText(byte* id, byte* value) =>
        Text[Marshal.PtrToStringUTF8((nint)id)!] = Marshal.PtrToStringUTF8((nint)value)!;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void SetMarkup(byte* id, byte* value)
    {
        var key = Marshal.PtrToStringUTF8((nint)id)!;
        var markup = Marshal.PtrToStringUTF8((nint)value)!;
        if (key == "toolbarLeft") { _toolbar = markup; _toolbarUpdates++; }
        else if (key == "topMenus") _menus = markup;
        else if (key == "contextMenu") _contextMenu = markup;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void SetProperty(byte* id, byte* name, byte* value) =>
        Properties[$"{Marshal.PtrToStringUTF8((nint)id)}.{Marshal.PtrToStringUTF8((nint)name)}"] =
            Marshal.PtrToStringUTF8((nint)value)!;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte NoViewport(float* x, float* y, float* width, float* height) => 0;

    private static XElement Toolbar() => XElement.Parse($"<root>{_toolbar}</root>");
    private static XElement Menus() => XElement.Parse($"<root>{_menus}</root>");
    private static XElement ContextMenu() => XElement.Parse($"<root>{_contextMenu}</root>");

    public static void Run(Action<bool, string> require)
    {
        Text.Clear();
        Properties.Clear();
        var api = new NativeApi
        {
            Version = 6, SetUiText = &SetText, GetViewportRect = &NoViewport,
            SetUiMarkup = &SetMarkup, SetUiProperty = &SetProperty,
        };
        var runtime = new EditorRuntime(0, 1440, 900, (nint)(&api));
        runtime.Initialize();
        try
        {
            var contentResourceRejected = false;
            try
            {
                _ = new EditorToolbarTool("validation.invalid-resource", "validation",
                    "Invalid", ["validation.invalid"], icon:
                        "sharpengine-resource://project/Plugins/ValidationPlugin/Content/icon.tga");
            }
            catch (ArgumentException) { contentResourceRejected = true; }
            require(contentResourceRejected,
                "Editor resource URIs must reject Plugin Content paths");
            var module = new TransformExtension();
            var registration = runtime.RegisterExtension(module);
            var PluginAsset = runtime.Assets!.Assets.Single(asset =>
                string.Equals(asset.Type, "PluginData", StringComparison.OrdinalIgnoreCase));
            var OpenedBy = string.Empty;
            using var DefaultAssetOpener = runtime.AssetOpeners.Register(new EditorAssetOpener(
                "validation.plugin-default", "PluginData", _ => OpenedBy = "default"));
            using var ProjectAssetOpener = runtime.AssetOpeners.Register(new EditorAssetOpener(
                "validation.plugin-project", "PluginData", _ => OpenedBy = "project", Priority: 10));
            require(runtime.AssetOpeners.Resolve(PluginAsset)?.Id == "validation.plugin-project",
                "A higher-priority project opener must override the type default");
            using var SpecificAssetOpener = runtime.AssetOpeners.Register(new EditorAssetOpener(
                "validation.plugin-specific", "PluginData", _ => OpenedBy = "specific",
                AssetId: PluginAsset.Id));
            require(runtime.AssetOpeners.Resolve(PluginAsset)?.Id == "validation.plugin-specific",
                "An asset-specific opener must override a type-wide opener");
            var OpenedAsset = runtime.OpenAsset(PluginAsset.Id);
            require(OpenedAsset.Success && OpenedBy == "specific" &&
                OpenedAsset.OpenerId == "validation.plugin-specific",
                "OpenAsset must execute the resolved opener and report its stable ID");
            SpecificAssetOpener.Dispose();
            require(runtime.AssetOpeners.Resolve(PluginAsset)?.Id == "validation.plugin-project",
                "Disposing an asset-specific opener must restore the project override");
            var TimelineExtensionType = typeof(TimelineEditorModule).Assembly.GetType(
                "SharpEngine.Timeline.TimelineEditorExtension", throwOnError: true)!;
            var TimelineExtension = (IEditorExtension)Activator.CreateInstance(
                TimelineExtensionType, nonPublic: true)!;
            using var TimelineRegistration = runtime.RegisterExtension(TimelineExtension);
            var RobotWalkOpen = runtime.OpenAsset("engine://animations/robot-walk");
            require(RobotWalkOpen.Success &&
                RobotWalkOpen.OpenerId == "engine.skeletal-animation-editor" &&
                runtime.DescribeEditorJson().Contains("engine://animations/robot-walk",
                    StringComparison.Ordinal) &&
                runtime.DescribeEditorJson().Contains("\"timeline\"", StringComparison.Ordinal),
                "The built-in SkeletalAnimation opener must activate the Timeline pane and document");
            var UefnIdleOpen = runtime.OpenAsset("engine://animations/uefn-mannequin-idle");
            require(UefnIdleOpen.Success &&
                runtime.DescribeEditorJson().Contains("engine://animations/uefn-mannequin-idle",
                    StringComparison.Ordinal),
                "Opening another SkeletalAnimation must preserve independent asset documents");
            var LocomotionOpen = runtime.OpenAsset(
                "engine://animation-state-machines/uefn-mannequin-locomotion");
            var LocomotionEditor = runtime.DescribeEditorJson();
            require(LocomotionOpen.Success &&
                LocomotionOpen.OpenerId == "engine.animation-state-machine-editor" &&
                LocomotionEditor.Contains("AnimationStateMachine", StringComparison.Ordinal) &&
                LocomotionEditor.Contains("uefn-mannequin-locomotion", StringComparison.Ordinal) &&
                LocomotionEditor.Contains("\"stateCount\"", StringComparison.Ordinal),
                "AnimationStateMachine assets must use the unified opener flow and custom editor");
            require(runtime.CustomBars.RegisteredSurfaces.Any(surface =>
                    surface.Id == EditorCustomBarRegistry.ViewportSurfaceId) &&
                runtime.CustomBars.RegisteredSurfaces.Any(surface =>
                    surface.Id == EditorCustomBarRegistry.TimelineSurfaceId),
                "Engine must create fixed Viewport and Animation Timeline CustomBar surfaces");
            using var projectCategory = runtime.Menus.RegisterCategory(new EditorMenuCategory(
                "file", "validation-tools", 350));
            using var projectItem = runtime.Menus.RegisterItem(new EditorMenuItem(
                "file", "validation-tools", "validation.reset", 100));
            var checkedState = false;
            using var checkedCommand = runtime.Commands.Register(new EditorCommand(
                "validation.checked", "Checked State", _ => checkedState = !checkedState,
                getChecked: _ => checkedState));
            using var checkedCategory = runtime.Menus.RegisterCategory(new EditorMenuCategory(
                "file", "validation-state", 360));
            using var checkedItem = runtime.Menus.RegisterItem(new EditorMenuItem(
                "file", "validation-state", "validation.checked", 100));
            using var objectCategory = runtime.ContextMenus.RegisterCategory(
                new EditorContextMenuCategory(EditorContextMenuKind.Object, "tools"));
            using var objectItem = runtime.ContextMenus.RegisterItem(
                new EditorContextMenuItem(EditorContextMenuKind.Object, "tools", "validation.reset"));
            runtime.Update(0);
            var button = Toolbar().Descendants("button").Single(element =>
                (string?)element.Attribute("data-action") == "editor.command:validation.reset");
            require(button.Value.Contains("Reset <&>\"", StringComparison.Ordinal) &&
                (string?)button.Attribute("data-action") == "editor.command:validation.reset" &&
                (string?)button.Parent!.Parent!.Attribute("data-category") == "project-test",
                "Project registration must publish escaped, actionable RML to the native UI");
            require(button.Descendants("span").Any(element =>
                    element.Value == "Reset <selected> & transform"),
                "Toolbar tooltips must preserve escaped project text");
            require(Menus().Descendants("button").Any(element => element.Value == "File") &&
                Menus().Descendants("button").Any(element =>
                    (string?)element.Attribute("data-action") == "editor.command:validation.reset"),
                "A project must extend the engine-created File menu with command placement");
            var checkedMenuItem = Menus().Descendants("button").Single(element =>
                (string?)element.Attribute("data-action") == "editor.command:validation.checked");
            require(checkedMenuItem.Descendants("span").First().Value == "[ ]",
                "GetChecked false must render an unchecked indicator");
            runtime.InvokeUiAction("editor.command:validation.checked");
            runtime.Update(0);
            checkedMenuItem = Menus().Descendants("button").Single(element =>
                (string?)element.Attribute("data-action") == "editor.command:validation.checked");
            require(checkedState && checkedMenuItem.Descendants("span").First().Value == "[x]",
                "Menu check state must be read back from GetChecked after execution");

            runtime.InvokeUiAction("editor.context:120:140:object::player");
            require(runtime.Selection.Kind == EditorSelectionKind.Entity &&
                runtime.Selection.Id == "player" &&
                ContextMenu().Descendants("button").Any(element =>
                    (string?)element.Attribute("data-action") == "editor.command:validation.reset") &&
                Properties["contextMenu.left"] == "120px" &&
                Properties["contextMenu.top"] == "140px" &&
                Properties["contextMenu.display"] == "block",
                "Object context requests must select their target and publish a positioned menu");

            runtime.Update(0);
            var updateCount = _toolbarUpdates;
            runtime.Update(0);
            require(updateCount == _toolbarUpdates,
                "An unchanged command state must preserve the existing native UI controls");
            module.Enabled = false;
            runtime.Update(0);
            require(Toolbar().Descendants("button").Single(element =>
                    (string?)element.Attribute("data-action") == "editor.command:validation.reset")
                .Attribute("disabled") is not null,
                "CanExecute must react to project C# state without a UI action");
            runtime.InvokeUiAction("editor.command:validation.reset");
            require(module.Calls == 0, "Disabled commands must also reject programmatic actions");

            module.Enabled = true;
            runtime.InvokeUiAction("editor.select-scene");
            runtime.Update(0);
            require(Toolbar().Descendants("button").Single(element =>
                    (string?)element.Attribute("data-action") == "editor.command:validation.reset")
                .Attribute("disabled") is not null,
                "Clearing selection must disable a selection-dependent command");
            runtime.InvokeUiAction("editor.select-entity:player");
            runtime.Update(0);
            var original = runtime.SelectedEntity!;
            runtime.InvokeUiAction("editor.command:validation.reset");
            require(module.Calls == 1 && runtime.IsDirty && original.Position == WorldVector3.Zero &&
                Text["position"] == "X 0   Y 0   Z 0",
                "The callback must edit the current world, refresh Inspector, and mark edits dirty");

            runtime.InvokeUiAction("editor.play");
            runtime.Update(0);
            runtime.InvokeUiAction("editor.command:validation.reset");
            require(module.Calls == 1, "CanExecute must be enforced while the editor is playing");
            runtime.InvokeUiAction("editor.stop");
            var restored = runtime.SelectedEntity!;
            require(ReferenceEquals(original, restored) && runtime.IsDirty,
                "Stop must restore simulation transforms without replacing the Authoring document");
            restored.Position = new WorldVector3(7, 8, 9);
            runtime.InvokeUiAction("editor.command:validation.reset");
            require(module.Calls == 2 && restored.Position == WorldVector3.Zero,
                "Command context must keep resolving the Authoring world after Stop");

            var duplicateRejected = false;
            try { runtime.Commands.Register(new EditorCommand("validation.reset", "Duplicate", _ => { })); }
            catch (InvalidOperationException) { duplicateRejected = true; }
            require(duplicateRejected, "Duplicate command ids must not replace an existing callback");

            using var earlier = runtime.Commands.Register(new EditorCommand("validation.first", "First", _ => { }));
            using var earlierButton = runtime.Toolbar.RegisterTool(new EditorToolbarTool(
                "validation.first", "project-test", "First", ["validation.first"], -1));
            using var dropdown = runtime.Toolbar.RegisterTool(new EditorToolbarTool(
                "validation.dropdown", "project-test", "Validation Actions",
                ["validation.first", "validation.reset"], 50));
            runtime.Update(0);
            var projectButtons = Toolbar().Descendants().Single(element =>
                (string?)element.Attribute("data-category") == "project-test").Descendants("button");
            require((string?)projectButtons.First().Attribute("data-action") ==
                "editor.command:validation.first", "Toolbar order must apply within a category");
            var dropdownButton = Toolbar().Descendants("button").Single(element =>
                (string?)element.Attribute("data-popup-id") == "toolbar-popup-validation.dropdown");
            require(dropdownButton.Parent!.Descendants("button").Count() == 3 &&
                dropdownButton.Parent.Descendants("button").Skip(1).All(element =>
                    ((string?)element.Attribute("data-action"))?.StartsWith(
                        "editor.command:validation.", StringComparison.Ordinal) == true),
                "A toolbar tool with multiple commands must render a dropdown menu");

            registration.Dispose();
            registration.Dispose();
            runtime.Update(0);
            runtime.InvokeUiAction("editor.command:validation.reset");
            require(module.ShutdownCalls == 1 && module.Calls == 2 &&
                !Toolbar().Descendants("button").Any(element =>
                    (string?)element.Attribute("data-action") == "editor.command:validation.reset"),
                "Unloading an extension must remove its UI and callback exactly once");

            using var old = runtime.Commands.Register(new EditorCommand("validation.replace", "Old", _ => { }));
            runtime.Commands.Remove("validation.replace");
            using var replacement = runtime.Commands.Register(new EditorCommand("validation.replace", "New", _ => { }));
            old.Dispose();
            require(runtime.Commands.Commands.Any(command => command.Label == "New"),
                "An old registration handle must not remove a replacement command");

            using var badCondition = runtime.Commands.Register(new EditorCommand("validation.condition", "Condition", _ => { },
                _ => throw new InvalidOperationException("Expected validation predicate failure")));
            using var badCategory = runtime.Toolbar.RegisterCategory(
                new EditorToolbarCategory("validation-errors", 900));
            using var badButton = runtime.Toolbar.RegisterTool(new EditorToolbarTool(
                "validation.condition", "validation-errors", "Condition", ["validation.condition"]));
            using var badCallback = runtime.Commands.Register(new EditorCommand("validation.failure", "Failure",
                _ => throw new InvalidOperationException("Expected validation callback failure")));
            runtime.Update(0);
            runtime.InvokeUiAction("editor.command:validation.failure");
            runtime.InvokeUiAction("editor.select-scene");
            require(Toolbar().Descendants("button").Any(element =>
                    element.Attribute("disabled") is not null),
                "A throwing project predicate or command must not break subsequent editor actions");

            var failingModule = new TransformExtension { FailStartup = true };
            var startupRejected = false;
            try { runtime.RegisterExtension(failingModule); }
            catch (InvalidOperationException) { startupRejected = true; }
            require(startupRejected && failingModule.ShutdownCalls == 1 &&
                runtime.Commands.Commands.All(command => command.Id != "validation.reset"),
                "A failed module startup must call Shutdown to release partial registrations");

            runtime.RegisterExtension(new TransformExtension());
            runtime.Shutdown();
            using var snapshot = JsonDocument.Parse(runtime.DescribeEditorJson());
            require(_toolbar.Length == 0 &&
                snapshot.RootElement.GetProperty("extensions").GetProperty("commands").GetArrayLength() == 0,
                "Editor shutdown must release module callbacks and clear the extension Toolbar");
        }
        finally { runtime.Shutdown(); }
    }

    private sealed class TransformExtension : IEditorExtension
    {
        private IDisposable? _command, _category, _button;
        public bool Enabled = true;
        public bool FailStartup;
        public int Calls, ShutdownCalls;

        public void Startup(EditorContext context)
        {
            _command = context.Commands.Register(new EditorCommand("validation.reset", "Reset <&>\"",
                command =>
                {
                    Calls++;
                    command.SelectedEntity!.Position = WorldVector3.Zero;
                    command.MarkDirty();
                    command.RefreshUi();
                },
                command => Enabled && command.SelectedEntity is not null &&
                    command.PlayState == EditorPlayState.Stopped,
                "Reset <selected> & transform"));
            if (FailStartup) throw new InvalidOperationException("Expected validation startup failure");
            _category = context.Toolbar.RegisterCategory(new EditorToolbarCategory("project-test", 700));
            _button = context.Toolbar.RegisterTool(new EditorToolbarTool(
                "validation.reset", "project-test", "Reset <&>\"",
                ["validation.reset"], 10, "Reset <selected> & transform"));
        }

        public void Shutdown()
        {
            ShutdownCalls++;
            _button?.Dispose();
            _category?.Dispose();
            _command?.Dispose();
        }
    }
}
