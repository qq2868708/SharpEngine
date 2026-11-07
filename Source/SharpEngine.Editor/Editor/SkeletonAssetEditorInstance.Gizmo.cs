using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Rendering;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

internal sealed partial class SkeletonAssetEditorInstance
{
    private string GizmoTool = "move";
    private ulong InteractionRevision = 1;
    private SkeletonSocketGizmoDrag? GizmoDrag;
    private SkeletonAsset? GizmoCandidate;
    private BoneTransform? GizmoTransform;
    private bool RefreshAfterGesture;

    public bool SuppressMarkupRefresh
    {
        get
        {
            return GizmoDrag is not null;
        }
    }

    public bool ConsumeMarkupRefreshRequest()
    {
        var Requested = RefreshAfterGesture;
        RefreshAfterGesture = false;
        return Requested;
    }

    public IReadOnlyList<AssetEditorLiveProperty> DrainLiveProperties()
    {
        return [];
    }

    private TransformGizmo? CreateGizmo(SkeletonAsset Value, UiRect Bounds)
    {
        if (IsReadOnly || SelectedKind != "socket")
        {
            return null;
        }
        var Pose = new SkeletonRigPose(Value);
        var Found = Pose.SocketModelMatrices.TryGetValue(SelectedId, out var Model);
        return Found ? new TransformGizmo(Model, Preview.Viewport.Camera, Bounds, GizmoTool) : null;
    }

    private ViewportFrame DecoratePreview(ViewportRenderContext RenderContext, ViewportFrame Frame)
    {
        var Decorated = PreviewWorld.Decorate(RenderContext, Frame);
        var Gizmo = CreateGizmo(GizmoCandidate ?? Skeleton, Frame.Bounds);
        return Gizmo is null ? Decorated : Decorated with
        {
            Lines = Decorated.Lines.Concat(Gizmo.BuildLines()).ToArray(),
        };
    }

    private bool TryHandleGizmoAction(string Action)
    {
        const string Prefix = "ui.viewport3d:skeletonPreviewViewport:pointer-";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Parts = Action[Prefix.Length..].Split(':');
        if (Parts.Length == 1 && Parts[0] == "cancel")
        {
            CancelGizmoDrag();
            return true;
        }
        if (Parts.Length != 3 || Parts[0] is not ("down" or "move" or "up"))
        {
            return true;
        }
        var HasX = float.TryParse(Parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var X);
        var HasY = float.TryParse(Parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var Y);
        if (!HasX || !HasY || !float.IsFinite(X) || !float.IsFinite(Y))
        {
            CancelGizmoDrag();
            return true;
        }
        var Pointer = new Vector2(X, Y);
        if (Parts[0] == "down")
        {
            CancelGizmoDrag();
            var Found = Context.TryGetElementRect(Preview.Viewport.ElementId, out var Bounds);
            var Gizmo = Found ? CreateGizmo(Skeleton, Bounds) : null;
            var Handle = default(TransformGizmoHandle);
            var Hit = Gizmo is not null && Gizmo.TryPick(X, Y, out Handle);
            if (Hit)
            {
                var Socket = Skeleton.Sockets.Single(Socket => Socket.SocketId == SelectedId);
                var Parent = new SkeletonRigPose(Skeleton).BoneModelMatrices[Socket.ParentBoneId];
                GizmoDrag = new SkeletonSocketGizmoDrag(Socket, Parent, Handle, Pointer, Document.Revision);
                Diagnostic = string.Empty;
                InteractionRevision++;
            }
            return true;
        }
        if (GizmoDrag is null)
        {
            return true;
        }
        if (GizmoDrag.Revision != Document.Revision || GizmoDrag.Socket.SocketId != SelectedId)
        {
            CancelGizmoDrag();
            return true;
        }
        try
        {
            var Transform = GizmoDrag.Evaluate(Pointer);
            var Candidate = Skeleton.ToJson();
            WriteSocketTransform(Candidate, SelectedId, Transform);
            // Parse the complete candidate before publishing any derived preview state.
            GizmoCandidate = SkeletonAsset.Parse(Candidate.ToJsonString(), Context.Path);
            GizmoTransform = Transform;
            PreviewWorld.SetRigPreview(GizmoCandidate);
            InteractionRevision++;
            if (Parts[0] == "up")
            {
                var Id = SelectedId;
                var Changed = Transform != GizmoDrag.Socket.LocalTransform;
                ClearGizmoDrag();
                if (Changed)
                {
                    EditRig("socket-gizmo-" + GizmoTool, Root => WriteSocketTransform(Root, Id, Transform));
                }
                PreviewWorld.SetRigPreview(Skeleton);
            }
        }
        catch (Exception Error) when (Error is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            CancelGizmoDrag();
            Diagnostic = Error.Message;
            Context.SetStatus(Diagnostic);
        }
        return true;
    }

    private void CancelGizmoDrag()
    {
        if (GizmoDrag is null)
        {
            return;
        }
        ClearGizmoDrag();
        PreviewWorld.SetRigPreview(Skeleton);
    }

    private void ClearGizmoDrag()
    {
        GizmoDrag = null;
        GizmoCandidate = null;
        GizmoTransform = null;
        RefreshAfterGesture = true;
        InteractionRevision++;
    }

    private static void WriteSocketTransform(JsonObject Root, string Id, BoneTransform Transform)
    {
        var Socket = Root["sockets"]!.AsArray().Single(Node => Node!["socketId"]!.GetValue<string>() == Id)!;
        Socket["position"] = Vector(Transform.Position);
        Socket["rotation"] = new JsonArray(Transform.Rotation.X, Transform.Rotation.Y, Transform.Rotation.Z, Transform.Rotation.W);
        Socket["scale"] = Vector(Transform.Scale);
    }

    private JsonObject DescribeGizmo()
    {
        var Found = Context.TryGetElementRect(Preview.Viewport.ElementId, out var Bounds);
        var Gizmo = Found ? CreateGizmo(GizmoCandidate ?? Skeleton, Bounds) : null;
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["tool"] = GizmoTool,
            ["editable"] = !IsReadOnly && SelectedKind == "socket",
            ["coordinateSpace"] = "socket-local",
            ["dragging"] = GizmoDrag is not null,
            ["socketId"] = SelectedKind == "socket" ? SelectedId : null,
            ["startDocumentRevision"] = GizmoDrag?.Revision,
            ["startPointer"] = GizmoDrag is null ? null : Point(GizmoDrag.StartPointer),
            ["startHandle"] = GizmoDrag is null ? null : new JsonObject
            {
                ["axis"] = GizmoDrag.Handle.Axis.ToString(),
                ["pivot"] = Point(GizmoDrag.Handle.Pivot),
                ["end"] = Point(GizmoDrag.Handle.End),
                ["worldDirection"] = Vector(GizmoDrag.Handle.WorldDirection),
                ["worldLength"] = GizmoDrag.Handle.WorldLength,
            },
            ["bounds"] = Found ? new JsonObject
            {
                ["x"] = Bounds.X,
                ["y"] = Bounds.Y,
                ["width"] = Bounds.Width,
                ["height"] = Bounds.Height,
            } : null,
            ["axis"] = GizmoDrag?.Handle.Axis.ToString(),
            ["previewTransform"] = GizmoTransform is null ? null : new JsonObject
            {
                ["position"] = Vector(GizmoTransform.Value.Position),
                ["rotation"] = new JsonArray(GizmoTransform.Value.Rotation.X, GizmoTransform.Value.Rotation.Y,
                    GizmoTransform.Value.Rotation.Z, GizmoTransform.Value.Rotation.W),
                ["scale"] = Vector(GizmoTransform.Value.Scale),
            },
            ["previewRigPose"] = GizmoCandidate is null ? null : new SkeletonRigPose(GizmoCandidate).ToJson(),
            ["handles"] = new JsonArray((Gizmo?.Handles ?? []).Select(Handle => (JsonNode)new JsonObject
            {
                ["axis"] = Handle.Axis.ToString(),
                ["kind"] = Handle.Kind.ToString(),
                ["pivot"] = Point(Handle.Pivot),
                ["end"] = Point(Handle.End),
                ["worldDirection"] = Vector(Handle.WorldDirection),
                ["worldLength"] = Handle.WorldLength,
                ["path"] = new JsonArray(Gizmo!.GetHandlePath(Handle).Select(Position =>
                    float.IsFinite(Position.X) && float.IsFinite(Position.Y) ? (JsonNode?)Point(Position) : null).ToArray()),
            }).ToArray()),
        };
    }

    private static JsonArray Vector(Vector3 Value)
    {
        return new JsonArray(Value.X, Value.Y, Value.Z);
    }

    private static JsonArray Point(Vector2 Value)
    {
        return new JsonArray(Value.X, Value.Y);
    }
}

/// <summary>Per-gesture data. Local TRS is evaluated from the starting pose, never decomposed from a sheared parent matrix.</summary>
internal sealed class SkeletonSocketGizmoDrag(
    SkeletonSocket Socket, Matrix4x4 Parent, TransformGizmoHandle Handle, Vector2 StartPointer, ulong Revision)
{
    public SkeletonSocket Socket
    {
        get;
    } = Socket;
    public TransformGizmoHandle Handle
    {
        get;
    } = Handle;
    public ulong Revision
    {
        get;
    } = Revision;
    public Vector2 StartPointer
    {
        get;
    } = StartPointer;

    private readonly TransformGizmoDrag Drag = new(new TransformGizmoPose(
        Socket.LocalTransform.Position, Socket.LocalTransform.Rotation, Socket.LocalTransform.Scale),
        Parent, Handle, StartPointer);

    public BoneTransform Evaluate(Vector2 Pointer)
    {
        var Pose = Drag.Evaluate(Pointer);
        return new BoneTransform(Pose.Position, Pose.Rotation, Pose.Scale);
    }
}
