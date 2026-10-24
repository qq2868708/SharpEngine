using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.UI;

internal static class UiResponsiveLayoutValidation
{
    internal static void Run(string Root, Action<bool, string> Check)
    {
        var Folder = Directory.CreateDirectory(Path.Combine(Root, "Responsive", "Content", "UI"));
        var PathValue = Path.Combine(Folder.FullName, "Layout.sui");
        var Rml = """
            <template><div id="root" data-ui-layout="free" style="position: relative; width: 800px; height: 600px;">
              <div id="right" style="position: absolute; right: 10%; top: 20px; width: 25%; height: 40px;" />
              <div id="stretch" style="position: absolute; left: 10px; right: 20px; top: 100px; width: auto; height: 40px;" />
              <div id="overlay" data-ui-layout="overlay" style="position: relative; width: 300px; height: 200px;">
                <span id="label" data-ui-align-x="center" data-ui-align-y="center" style="position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%); margin-left: 2%;">Label</span>
              </div>
              <div id="row" data-ui-layout="horizontal" style="display: flex; flex-direction: row;">
                <span id="fill" style="width: auto; flex-grow: 1; flex-basis: 0px; height: 40px;">Fill</span>
              </div>
              <div id="custom" style="display: block;"><span id="custom-child">Custom</span></div>
            </div></template>
            """;
        File.WriteAllText(PathValue, UiDocumentAsset.Create("Responsive", Rml).Serialize());
        var Asset = new AssetDescriptor
        {
            Id = "project://ui/responsive",
            Type = "UiDocument",
            RelativePath = "UI/Layout.sui",
            Source = AssetSource.Project,
        };
        UiEditorDocument Load() => UiEditorDocument.Load(Asset, PathValue, Root);
        var Document = Load();
        var Initial = Document.CanonicalSource;
        var Revision = Document.Revision;
        Check(Document.SetPreviewSize(1920, 1080, out _) && Document.SetPreviewSize(720, 1280, out _) &&
            !Document.IsDirty && Document.Revision == Revision && Document.CanonicalSource == Initial && !Document.CanUndo,
            "Preview resolutions are per-instance environment, outside source, dirty state and Undo");
        Check(Load().PreviewWidth == 1280 && Document.PreviewWidth == 720 && !Document.SetPreviewSize(0, 720, out _),
            "Preview size is bounded and isolated from another use of the same asset");
        var Metrics = new UiLayoutMetrics(520, 20, 200, 40, 800, 600, 1920, 1080);
        string Style(string Name) => Document.Sheet.Compute(Document.SelectedElement()!).GetValueOrDefault(Name)?.Value ?? "";
        Document.Select("#right");
        Document.BeginGesture("move", 520, 20, 200, 40, out _, Metrics);
        Document.ApplyGestureMove("move", 20, 10, false, out _);
        Document.ApplyGestureMove("move", 0, 0, false, out _);
        Document.CommitGesture();
        Check(Document.CanonicalSource == Initial && !Document.CanUndo && !Document.IsDirty,
            "Dragging back to the origin restores exact authoring state and produces no Undo entry");
        Check(Document.BeginGesture("move", 520, 20, 200, 40, out _, Metrics) &&
            Document.ApplyGestureMove("move", 40, 0, false, out _), "A percentage/right-aligned control can move");
        Document.CommitGesture();
        Check(Style("right") == "5%" && Style("left") == "" && Style("width") == "25%",
            "Moving right reduces its authored right percentage and preserves size and unspecified left");
        Document.Undo();
        Check(Document.CanonicalSource == Initial, "One Undo restores the complete unit-preserving gesture");
        Document.Select("#right");
        Document.BeginGesture("resize-e", 520, 20, 200, 40, out _, Metrics);
        Document.ApplyGestureResize("resize-e", 40, 0, false, out _);
        Document.CommitGesture();
        Check(Style("width") == "30%" && Style("right") == "5%" && Style("height") == "40px",
            "Trailing resize preserves percentage width, holds the leading edge and does not rewrite height");
        Document.Undo();
        Document.Select("#stretch");
        var Stretch = Metrics with
        {
            X = 10,
            Y = 100,
            Width = 770,
        };
        Document.BeginGesture("resize-e", 10, 100, 770, 40, out _, Stretch);
        Document.ApplyGestureResize("resize-e", -30, 0, false, out _);
        Document.CommitGesture();
        Check(Style("right") == "50px" && Style("left") == "10px" && Style("width") == "auto",
            "Resizing a stretched item changes its edge margin, retaining automatic extent");
        Document.Undo();
        Document.Select("#label");
        var Overlay = Metrics with
        {
            ParentWidth = 300,
            ParentHeight = 200,
            Width = 60,
            Height = 20,
        };
        Document.BeginGesture("move", 120, 90, 60, 20, out _, Overlay);
        Document.ApplyGestureMove("move", 30, 10, false, out _);
        Document.CommitGesture();
        Check(Style("margin-left") == "12%" && Style("margin-top") == "10px" && Style("left") == "50%" &&
            Style("transform") == "translate(-50%, -50%)", "Overlay dragging changes slot margins, keeping alignment and transform");
        Document.Undo();
        Document.Select("#fill");
        Document.BeginGesture("resize-se", 0, 0, 200, 40, out _, Metrics);
        Check(!Document.ApplyGestureResize("resize-se", 30, 20, false, out _) && Document.CanonicalSource == Initial,
            "A parent-owned fill axis rejects resizing atomically, including a second axis");
        Document.CancelGesture();
        Document.BeginGesture("resize-se", 0, 0, 200, 40, out _, Metrics);
        Check(Document.ApplyGestureResize("resize-se", 0, 20, false, out _) && Style("height") == "60px" && Style("flex-grow") == "1",
            "A corner drag may resize the independent axis while the fill axis remains unchanged");
        Document.CancelGesture();
        Document.Select("#right");
        Check(Document.SetAxisMode(true, "content", Metrics, out _) && Style("width") == "auto", "Content sizing writes auto");
        Check(Document.SetAxisMode(true, "fill", Metrics, out _) && Document.AxisMode(Document.SelectedElement()!, true) == "fill",
            "Fill in free layout writes edge constraints");
        Check(Document.SetAxisMode(true, "percent", Metrics, out _) && Style("width") == "25%", "Percent sizing derives from native parent extent");
        Check(Document.SetLayoutAlignment(true, "center", out _) && Style("left") == "50%" && Style("right") == "auto",
            "Center alignment replaces conflicting trailing-edge constraint");
        Document.Select("#custom-child");
        var Before = Document.CanonicalSource;
        Check(!Document.SetAxisMode(true, "fill", Metrics, out _) && Document.CanonicalSource == Before,
            "Custom parent layouts are preserved until explicitly adopted");
        Document.Select("#custom");
        Check(Document.SetContainerLayout("overlay", new Dictionary<string, UiLayoutMetrics>(), out _) &&
            Document.LayoutOf(Document.SelectedElement()) == "overlay", "A container explicitly adopts overlay semantics");
        Document.Select("#custom-child");
        Check(Style("position") == "absolute" && Document.SetLayoutAlignment(false, "end", out _) && Style("bottom") == "0px",
            "Overlay children serialize real positioning and per-axis alignment");
        Document.Save();
        var Reloaded = Load();
        Check(Reloaded.CanonicalSource == Document.CanonicalSource && !Reloaded.IsDirty && Reloaded.PreviewWidth == 1280,
            "Visual layouts survive save/reopen, while preview environment stays transient");
        var Runtime = UiScreenComposer.Compose(UiDocumentAsset.Load(PathValue), new UiScreenRequest(Asset.Id));
        Check(Runtime.Contains("data-ui-layout=\"overlay\"", StringComparison.Ordinal) && Runtime.Contains("bottom: 0px", StringComparison.Ordinal),
            "Game consumes the saved RML/RCSS directly without an editor-only runtime layout interpreter");
        var Dp = UiLayoutLength.Parse("20dp", "width", Metrics with
        {
            Density = 2,
        });
        Check(Dp?.Offset(10) == "25dp" && UiLayoutLength.Parse("5%", "margin-top", Metrics)?.Pixels == 40,
            "Unit conversion distinguishes dp scaling and RCSS vertical-margin percentage basis");
        var Custom = Load();
        Custom.Select("#custom-child");
        Custom.SetStyle("transform", "rotate(20deg)", out _);
        Check(!Custom.BeginGesture("move", 0, 0, 100, 40, out _, Metrics),
            "Direct manipulation refuses an unsupported transform instead of using the wrong coordinate basis");
        Custom.Select("#custom");
        var CustomSource = Custom.CanonicalSource;
        Check(!Custom.SetContainerLayout("horizontal", new Dictionary<string, UiLayoutMetrics>(), out _) && Custom.CanonicalSource == CustomSource,
            "Container conversion cannot silently discard a child's authored transform");
        var InvalidRejected = false;
        try
        {
            _ = UiDocumentAsset.Create("Invalid", "<template><div data-ui-layout='unknown' /></template>");
        }
        catch (InvalidDataException Error)
        {
            InvalidRejected = Error.Message.Contains("UI_LAYOUT_MODE_INVALID", StringComparison.Ordinal);
        }
        Check(InvalidRejected, "Unknown serialized layout intent is rejected explicitly");
    }
}
