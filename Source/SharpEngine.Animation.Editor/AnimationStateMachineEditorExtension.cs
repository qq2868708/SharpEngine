using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.Editor.Layout;
using SharpEngine.UI;

namespace SharpEngine.Animation;

internal sealed class AnimationStateMachineEditorExtension : IEditorExtension
{
    private IDisposable? Registration;

    public void Startup(EditorContext Context)
    {
        Registration = Context.AssetOpeners.Register(new EditorAssetOpener(
            "engine.animation-state-machine-editor", "AnimationStateMachine", OpenAsset,
            Priority: -100, EditorId: "timeline-editor"));
    }

    public void Shutdown()
    {
        Registration?.Dispose();
        Registration = null;
    }

    private static IAssetEditorInstance OpenAsset(EditorAssetOpenContext Context) { return new EditorInstance(Context); }

    private sealed class EditorInstance : IAssetEditorInstance
    {
        private static readonly AssetEditorLayoutDefinition Layout = AssetEditorLayoutDefinition.LoadEmbedded(
            typeof(AnimationStateMachineEditorExtension).Assembly, "SharpEngine.Animation.AnimationStateMachineEditor.rml");
        private readonly EditorAssetOpenContext Context;
        private readonly AnimationStateMachineAsset StateMachine;
        private readonly AssetEditorLayoutInstance LayoutInstance;

        internal EditorInstance(EditorAssetOpenContext Context)
        {
            this.Context = Context;
            var Assets = Context.Runtime.Assets ?? throw new InvalidOperationException("Asset registry is unavailable");
            StateMachine = AnimationStateMachineContent.Load(Assets, Context.Asset.Id);
            LayoutInstance = Layout.CreateInstance(Context.EditorRootId);
        }

        public AssetDescriptor Asset
        {
            get
            {
                return Context.Asset;
            }
        }
        public string EditorId
        {
            get
            {
                // Preserve the existing window identity and saved Dock-layout key.
                return "timeline-editor";
            }
        }
        public string Title
        {
            get
            {
                return Asset.Name;
            }
        }
        public string PrimaryPaneId
        {
            get
            {
                return Layout.PrimaryPaneId;
            }
        }
        public bool IsDirty
        {
            get
            {
                return false;
            }
        }
        public bool IsReadOnly
        {
            get
            {
                return true;
            }
        }
        public IReadOnlyList<AssetEditorPaneDefinition> Panes
        {
            get
            {
                return Layout.Panes;
            }
        }
        public IReadOnlyList<AssetEditorCommandDefinition> Commands
        {
            get;
        } =
        [
            new("add-notify", "Add Notify", "Edit", 100, "add", true),
            new("undo", "Undo", "Edit", 200, "reset", true),
            new("redo", "Redo", "Edit", 210, "reset", true),
        ];

        public EditorLayoutDocument CreateDefaultLayout() { return Layout.CreateDockLayout(); }
        public bool CanExecuteCommand(string CommandId) { return false; }
        public void ExecuteCommand(string CommandId)
        {
        }
        public bool TryHandleAction(string Action) { return LayoutInstance.TryHandleAction(Action); }
        public void Update(float DeltaSeconds)
        {
        }
        public void Save() { throw new InvalidOperationException("This asset is read-only"); }
        public void Dispose() { LayoutInstance.Dispose(); }

        public string BuildPaneMarkup(string PaneId)
        {
            var Values = new JsonObject
            {
                ["Title"] = Title,
                ["InitialState"] = StateMachine.InitialStateId,
                ["StateCount"] = StateMachine.States.Count,
                ["TransitionCount"] = StateMachine.Transitions.Count,
                ["States"] = new JsonArray(StateMachine.States.Select(State => (JsonNode)new JsonObject
                {
                    ["Id"] = State.StateId,
                    ["Clip"] = State.ClipId,
                    ["InitialClass"] = State.StateId == StateMachine.InitialStateId ? " initial" : string.Empty,
                }).ToArray()),
                ["DetailsJson"] = DescribeDocument().ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                }),
            };
            return LayoutInstance.RenderPane(PaneId, new UiLayoutContext(1200, 320, Title), Values,
                new Dictionary<string, IUiLayoutPart>());
        }

        public JsonObject Describe()
        {
            var State = DescribeDocument();
            State["schemaVersion"] = 3;
            State["editorRootId"] = Context.EditorRootId;
            State["panes"] = new JsonArray(Panes.Select(Pane => (JsonNode)Pane.PaneId).ToArray());
            State["playing"] = false;
            State["notifyPreview"] = "ignored";
            State["runtimeUi"] = LayoutInstance.Describe();
            return State;
        }

        private JsonObject DescribeDocument()
        {
            return new JsonObject
            {
                ["assetId"] = Asset.Id,
                ["type"] = Asset.Type,
                ["source"] = Asset.Source.ToString(),
                ["readOnly"] = true,
                ["documentRevision"] = 1,
                ["savepointRevision"] = 1,
                ["dirty"] = false,
                ["stateCount"] = StateMachine.States.Count,
                ["parameterCount"] = StateMachine.Parameters.Count,
                ["transitionCount"] = StateMachine.Transitions.Count,
                ["clipIds"] = new JsonArray(StateMachine.ClipIds.Select(ClipId => (JsonNode)ClipId).ToArray()),
                ["document"] = StateMachine.ToJson(),
            };
        }
    }
}
