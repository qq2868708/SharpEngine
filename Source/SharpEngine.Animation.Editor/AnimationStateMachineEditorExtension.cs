using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using SharpEngine.Assets;
using SharpEngine.Editor;
using SharpEngine.Editor.Layout;
using SharpEngine.UI;

namespace SharpEngine.Animation;

internal sealed partial class AnimationStateMachineEditorExtension : IEditorExtension
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

    private sealed partial class EditorInstance : IAssetEditorInstance, IAssetEditorLivePreview
    {
        private static readonly AssetEditorLayoutDefinition Layout = AssetEditorLayoutDefinition.LoadEmbedded(
            typeof(AnimationStateMachineEditorExtension).Assembly, "SharpEngine.Animation.AnimationStateMachineEditor.rml");
        private readonly EditorAssetOpenContext Context;
        private readonly AnimationStateMachineAuthoringDocument Document;
        private AnimationStateMachineAsset StateMachine
        {
            get
            {
                return Document.StateMachine;
            }
        }
        private string Diagnostic = string.Empty;
        private readonly AssetEditorLayoutInstance LayoutInstance;

        internal EditorInstance(EditorAssetOpenContext Context)
        {
            this.Context = Context;
            var Assets = Context.Runtime.Assets ?? throw new InvalidOperationException("Asset registry is unavailable");
            Document = new AnimationStateMachineAuthoringDocument(Assets, Context.Asset, Context.Path);
            LayoutInstance = Layout.CreateInstance(Context.EditorRootId);
            InitializePreview(Assets);
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
                return Document.IsDirty;
            }
        }
        public bool IsReadOnly
        {
            get
            {
                return Document.IsReadOnly;
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
            new("play", "Play", "Preview", 10, null, true),
            new("pause", "Pause", "Preview", 20, null, true),
            new("stop", "Reset", "Preview", 30, null, true),
            new("step", "Step 1/60s", "Preview", 40, null, true),
            new("add-state", "Add State", "Edit", 100, "add", true),
            new("add-parameter", "Add Parameter", "Edit", 110, "add", true),
            new("add-transition", "Add Transition", "Edit", 120, "add", true),
            new("undo", "Undo", "Edit", 200, "reset", true),
            new("redo", "Redo", "Edit", 210, "reset", true),
        ];

        public EditorLayoutDocument CreateDefaultLayout() { return Layout.CreateDockLayout(); }
        public bool CanExecuteCommand(string CommandId)
        {
            if (CommandId is "play" or "pause" or "stop" or "step")
            {
                return PreviewPlayer is not null && (CommandId switch
                {
                    "play" => !PreviewPlaying,
                    "pause" => PreviewPlaying,
                    "step" => !PreviewPlaying,
                    _ => true,
                });
            }
            return !IsReadOnly && (CommandId switch
            {
                "undo" => Document.CanUndo,
                "redo" => Document.CanRedo,
                "add-transition" => StateMachine.States.Count >= 2,
                "add-state" or "add-parameter" => true,
                _ => false,
            });
        }
        public void ExecuteCommand(string CommandId)
        {
            if (!CanExecuteCommand(CommandId))
            {
                return;
            }
            if (ExecutePreviewCommand(CommandId))
            {
                return;
            }
            if (CommandId == "undo")
            {
                Document.Undo();
                Diagnostic = string.Empty;
                RebuildPreview();
            }
            else if (CommandId == "redo")
            {
                Document.Redo();
                Diagnostic = string.Empty;
                RebuildPreview();
            }
            else
            {
                Apply(CommandId);
            }
        }
        public bool TryHandleAction(string Action)
        {
            if (TryHandlePreviewAction(Action))
            {
                return true;
            }
            const string Prefix = "editor.state-machine:";
            if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return LayoutInstance.TryHandleAction(Action);
            }
            Apply(Action[Prefix.Length..]);
            return true;
        }
        public void Update(float DeltaSeconds)
        {
            UpdatePreview(DeltaSeconds);
        }
        public void Save()
        {
            Document.Save();
            SharpEngine.World.SceneRenderer.InvalidateAssets(PreviewAssets);
            Diagnostic = string.Empty;
        }
        public void Dispose() { LayoutInstance.Dispose(); }

        public string BuildPaneMarkup(string PaneId)
        {
            if (PaneId == "details")
            {
                return BuildDetails();
            }
            var Playback = PreviewPlayer?.Describe();
            var Values = new JsonObject
            {
                ["Title"] = Title,
                ["InitialState"] = StateMachine.InitialStateId,
                ["StateCount"] = StateMachine.States.Count,
                ["TransitionCount"] = StateMachine.Transitions.Count,
                ["PreviewUnavailable"] = PreviewPlayer is null,
                ["Playing"] = PreviewPlaying,
                ["Paused"] = !PreviewPlaying,
                ["PreviewState"] = PreviewPlayer?.StateId ?? "Unavailable",
                ["PreviewTransition"] = Playback?["blending"]?.GetValue<bool>() == true
                    ? Playback["sourceStateId"]!.GetValue<string>() + " → " + PreviewPlayer!.StateId : "No active blend",
                ["PreviewDiagnostic"] = PreviewDiagnostic,
                ["PreviewParameters"] = new JsonArray(PreviewParameters.Select(Parameter => (JsonNode)new JsonObject
                {
                    ["Id"] = Uri.EscapeDataString(Parameter.Key),
                    ["Name"] = Parameter.Key,
                    ["Value"] = Parameter.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                }).ToArray()),
                ["States"] = new JsonArray(StateMachine.States.Select(State => (JsonNode)new JsonObject
                {
                    ["Id"] = State.StateId,
                    ["ActionId"] = Uri.EscapeDataString(State.StateId),
                    ["Clip"] = State.ClipId,
                    ["InitialClass"] = (State.StateId == StateMachine.InitialStateId ? " initial" : string.Empty) +
                        (State.StateId == PreviewPlayer?.StateId ? " preview-active" : string.Empty),
                    ["SwitchDisabled"] = PreviewPlayer is null || PreviewPlayer.IsBlending || State.StateId == PreviewPlayer.StateId,
                }).ToArray()),
                ["Transitions"] = new JsonArray(StateMachine.Transitions.Select(Transition => (JsonNode)new JsonObject
                {
                    ["Id"] = Transition.TransitionId,
                    ["From"] = Transition.FromStateId,
                    ["To"] = Transition.ToStateId,
                    ["ConditionCount"] = Transition.Conditions.Count,
                    ["ActionId"] = Uri.EscapeDataString(Transition.TransitionId),
                    ["BlendDuration"] = Transition.BlendDuration,
                    ["BlendCurve"] = Transition.BlendCurve,
                    ["TriggerDisabled"] = PreviewPlayer is null || PreviewPlayer.IsBlending ||
                        Transition.FromStateId != PreviewPlayer.StateId,
                }).ToArray()),
                ["DetailsJson"] = DescribeDocument().ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                }),
            };
            return LayoutInstance.RenderPane(PaneId, new UiLayoutContext(1200, 320, Title), Values,
                new Dictionary<string, IUiLayoutPart>
                {
                    ["Viewport"] = PreviewViewport,
                    ["ParameterEditors"] = new PreviewParameterEditors(PreviewParameters),
                });
        }

        private sealed class PreviewParameterEditors(IReadOnlyDictionary<string, double> Parameters) : IUiLayoutPart
        {
            public string BuildLayout(UiLayoutContext Context)
            {
                return string.Concat(Parameters.Select(Parameter => SharpEngine.UI.UiValueEditors.Number(Parameter.Key,
                    new SharpEngine.UI.UiValueInput("stateMachinePreviewParameter-" + Uri.EscapeDataString(Parameter.Key),
                        Parameter.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                        "editor.state-machine-preview:parameter:" + Uri.EscapeDataString(Parameter.Key) + ":"))));
            }
        }

        public JsonObject Describe()
        {
            var State = DescribeDocument();
            State["schemaVersion"] = 4;
            State["stateRevision"] = Document.Revision + PreviewRevision;
            State["editorRootId"] = Context.EditorRootId;
            State["panes"] = new JsonArray(Panes.Select(Pane => (JsonNode)Pane.PaneId).ToArray());
            State["playing"] = PreviewPlaying;
            State["preview"] = DescribePreview();
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
                ["readOnly"] = IsReadOnly,
                ["documentRevision"] = Document.Revision,
                ["savepointRevision"] = Document.SavepointRevision,
                ["dirty"] = IsDirty,
                ["diagnostic"] = Diagnostic,
                ["stateCount"] = StateMachine.States.Count,
                ["parameterCount"] = StateMachine.Parameters.Count,
                ["transitionCount"] = StateMachine.Transitions.Count,
                ["clipIds"] = new JsonArray(StateMachine.ClipIds.Select(ClipId => (JsonNode)ClipId).ToArray()),
                ["document"] = StateMachine.ToJson(),
            };
        }

        private void Apply(string Operation)
        {
            var Changed = Document.Edit(Operation, out var Message);
            Diagnostic = Changed ? string.Empty : Message;
            if (Diagnostic.Length != 0)
            {
                Context.SetStatus(Diagnostic);
            }
            if (Changed)
            {
                RebuildPreview();
            }
        }

        private string BuildDetails()
        {
            var Root = Document.Snapshot;
            var Markup = new StringBuilder("<div id=\"stateMachineAuthoring\" class=\"asset-inspector\">");
            Markup.Append(BuildPreviewMeshPicker());
            if (Diagnostic.Length != 0)
            {
                Markup.Append("<div class=\"ui-diagnostic ui-diagnostic-error\">")
                    .Append(AssetEditorInstanceUtilities.Escape(Diagnostic)).Append("</div>");
            }
            Field("Name", Root["name"]!, "name");
            Field("Initial State", Root["initialStateId"]!, "initial");
            Markup.Append("<h3>States</h3>");
            var States = Root["states"]!.AsArray();
            for (var Index = 0; Index < States.Count; Index++)
            {
                Markup.Append("<div class=\"animation-state-machine-state\">");
                foreach (var Key in new[]
                {
                    "stateId", "clipId", "loop", "speed",
                })
                {
                    Field(Key, States[Index]![Key]!, $"state:{Index}:{Key}");
                }
                Button("Remove State + Transitions", $"remove-state:{Index}", States.Count > 1);
                Markup.Append("</div>");
            }
            Markup.Append("<h3>Parameters</h3>");
            var Parameters = Root["parameters"]!.AsArray();
            for (var Index = 0; Index < Parameters.Count; Index++)
            {
                Field("Parameter", Parameters[Index]!["parameterId"]!, $"parameter:{Index}:parameterId");
                Field("Default", Parameters[Index]!["defaultValue"]!, $"parameter:{Index}:defaultValue");
                Button("Remove Parameter", $"remove-parameter:{Index}");
            }
            Markup.Append("<h3>Transitions (first match wins)</h3>");
            var Transitions = Root["transitions"]!.AsArray();
            for (var Index = 0; Index < Transitions.Count; Index++)
            {
                Markup.Append("<div class=\"animation-state-machine-state\">");
                foreach (var Key in new[]
                {
                    "transitionId", "fromStateId", "toStateId", "exitTime", "blendDuration", "blendCurve",
                })
                {
                    if (Key == "blendCurve")
                    {
                        var Curve = Transitions[Index]![Key]!.GetValue<string>();
                        var NextCurve = Curve == "Linear" ? "SmoothStep" : "Linear";
                        var CurveButton = new StringBuilder("<button id=\"stateMachineCurve-")
                            .Append(Index).Append("\" data-action=\"editor.state-machine:transition:")
                            .Append(Index).Append(":blendCurve:").Append(NextCurve).Append('"');
                        if (IsReadOnly)
                        {
                            CurveButton.Append(" disabled=\"disabled\"");
                        }
                        CurveButton.Append('>').Append(Curve).Append(" (switch to ").Append(NextCurve).Append(")</button>");
                        Markup.Append(UiValueEditors.Field("blendCurve", CurveButton.ToString(), Class: "asset-field"));
                    }
                    else
                    {
                        Field(Key, Transitions[Index]![Key], $"transition:{Index}:{Key}");
                    }
                }
                Button("Move Up", $"move-up:{Index}", Index > 0);
                Button("Move Down", $"move-down:{Index}", Index + 1 < Transitions.Count);
                Button("Remove Transition", $"remove-transition:{Index}");
                var Conditions = Transitions[Index]!["conditions"]!.AsArray();
                for (var ConditionIndex = 0; ConditionIndex < Conditions.Count; ConditionIndex++)
                {
                    foreach (var Key in new[]
                    {
                        "parameterId", "comparison", "value",
                    })
                    {
                        Field(Key, Conditions[ConditionIndex]![Key]!, $"condition:{Index}:{ConditionIndex}:{Key}");
                    }
                    Button("Remove Condition", $"remove-condition:{Index}:{ConditionIndex}");
                }
                Button("Add Condition", $"add-condition:{Index}", Parameters.Count > 0);
                Markup.Append("</div>");
            }
            Markup.Append(AssetEditorInstanceUtilities.Source(Document.Source)).Append("</div>");
            return Markup.ToString();

            void Field(string Label, JsonNode? Value, string Operation)
            {
                var Text = Value is JsonValue Scalar && Scalar.TryGetValue<string>(out var StringValue)
                    ? StringValue : Value?.ToJsonString() ?? string.Empty;
                var Id = "stateMachineField-" + Operation.Replace(':', '-');
                var Action = "editor.state-machine:" + Operation + ":";
                var Kind = Value?.GetValueKind();
                if (Kind is JsonValueKind.True or JsonValueKind.False)
                {
                    var Checked = Value!.GetValue<bool>();
                    Markup.Append(UiValueEditors.Boolean(Label, Id, Checked,
                        Action + (Checked ? "false" : "true"), IsReadOnly));
                }
                else if (Kind == JsonValueKind.Number)
                {
                    Markup.Append(UiValueEditors.Number(Label, new UiValueInput(Id, Text, Action, IsReadOnly), Class: "asset-field"));
                }
                else
                {
                    Markup.Append(AssetEditorInstanceUtilities.Field(Label, Text, Action, IsReadOnly, Id: Id));
                }
            }

            void Button(string Label, string Operation, bool Enabled = true)
            {
                Markup.Append("<button class=\"asset-inline-button\" data-action=\"editor.state-machine:")
                    .Append(Operation).Append('"');
                if (IsReadOnly || !Enabled)
                {
                    Markup.Append(" disabled=\"disabled\"");
                }
                Markup.Append('>').Append(Label).Append("</button>");
            }
        }
    }
}
