using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Mathematics;
using SharpEngine.Rendering;
using SharpEngine.UI;
using SharpEngine.World;

namespace SharpEngine.Editor;

internal sealed partial class MeshAssetEditorInstance
{
    private string GizmoTool = "move";
    private ulong InteractionRevision = 1;
    private MeshSocketGizmoDrag? GizmoDrag;
    private StaticMeshSocketHierarchy? GizmoCandidate;
    private TransformGizmoPose? GizmoTransform;
    private bool RefreshAfterGesture;
    private string? ActiveInputId;

    public bool SuppressMarkupRefresh
    {
        get
        {
            return GizmoDrag is not null || ActiveInputId is not null;
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

    private TransformGizmo? CreateGizmo(StaticMeshSocketHierarchy Value, UiRect Bounds)
    {
        if (IsReadOnly || SelectedSocketId is null)
        {
            return null;
        }
        var Socket = Value.Sockets.FirstOrDefault(Socket => Socket.SocketId == SelectedSocketId);
        return Socket is null ? null : new TransformGizmo(Value.ModelMatrices[Socket.SocketId], Preview.Viewport.Camera, Bounds, GizmoTool);
    }

    private ViewportFrame DecoratePreview(ViewportRenderContext RenderContext, ViewportFrame Frame)
    {
        if (!IsStatic)
        {
            return Frame;
        }
        var Value = GizmoCandidate ?? StaticMesh!.SocketHierarchy;
        var Lines = Frame.Lines.ToList();
        var Matrix = RenderContext.Camera.ViewProjection(Frame.Bounds.Width / Frame.Bounds.Height);
        foreach (var Socket in Value.Sockets)
        {
            var Model = Value.ModelMatrices[Socket.SocketId];
            var Origin = Model.Translation;
            var ParentOrigin = Socket.ParentSocketId is null ? Vector3.Zero : Value.ModelMatrices[Socket.ParentSocketId].Translation;
            SceneRenderer.AddLine(Lines, ParentOrigin, Origin, Matrix, Frame.Bounds, new Vector4(0.8f, 0.4f, 1, 1));
            var Axes = new[]
            {
                Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
            };
            var Colors = new[]
            {
                new Vector4(1, 0.2f, 0.2f, 1), new Vector4(0.2f, 1, 0.2f, 1), new Vector4(0.2f, 0.5f, 1, 1),
            };
            for (var Index = 0; Index < Axes.Length; Index++)
            {
                var End = Origin + VectorMath.NormalizeOrZero(Vector3.TransformNormal(Axes[Index], Model)) * 0.15f;
                SceneRenderer.AddLine(Lines, Origin, End, Matrix, Frame.Bounds, Colors[Index]);
            }
        }
        var Gizmo = CreateGizmo(Value, Frame.Bounds);
        return Frame with
        {
            Lines = Lines.Concat(Gizmo?.BuildLines() ?? []).ToArray(),
        };
    }

    private bool TryHandleGizmoAction(string Action)
    {
        const string Prefix = "ui.viewport3d:meshPreviewViewport:pointer-";
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
            EndTextInput();
            var Found = Context.TryGetElementRect(Preview.Viewport.ElementId, out var Bounds);
            var Gizmo = Found ? CreateGizmo(StaticMesh!.SocketHierarchy, Bounds) : null;
            var Handle = default(TransformGizmoHandle);
            var Hit = Gizmo is not null && Gizmo.TryPick(X, Y, out Handle);
            if (Hit)
            {
                var Socket = StaticMesh!.Sockets.Single(Socket => Socket.SocketId == SelectedSocketId);
                var Parent = Socket.ParentSocketId is null ? Matrix4x4.Identity : StaticMesh.SocketHierarchy.ModelMatrices[Socket.ParentSocketId];
                GizmoDrag = new MeshSocketGizmoDrag(Socket, Parent, Handle, Pointer, Document.Revision);
                Diagnostic = string.Empty;
                InteractionRevision++;
            }
            return true;
        }
        if (GizmoDrag is null)
        {
            return true;
        }
        if (GizmoDrag.Revision != Document.Revision || GizmoDrag.Socket.SocketId != SelectedSocketId)
        {
            CancelGizmoDrag();
            return true;
        }
        try
        {
            var Transform = GizmoDrag.Evaluate(Pointer);
            var Candidate = StaticMesh!.Sockets.Select(Socket => Socket.SocketId == SelectedSocketId ? Socket with
            {
                Position = Transform.Position,
                Rotation = Transform.Rotation,
                Scale = Transform.Scale,
            } : Socket).ToArray();
            // Validate the complete Socket graph without copying or reparsing mesh geometry on pointer movement.
            GizmoCandidate = new StaticMeshSocketHierarchy(Candidate);
            GizmoTransform = Transform;
            InteractionRevision++;
            if (Parts[0] == "up")
            {
                var Id = SelectedSocketId!;
                var Changed = Transform != GizmoDrag.StartTransform;
                ClearGizmoDrag();
                if (Changed)
                {
                    EditSockets("socket-gizmo-" + GizmoTool, Root => WriteSocketTransform(Root, Id, Transform));
                }
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
    }

    private void EndTextInput()
    {
        if (ActiveInputId is null)
        {
            return;
        }
        ActiveInputId = null;
        RefreshAfterGesture = true;
        InteractionRevision++;
    }

    private void ClearGizmoDrag()
    {
        GizmoDrag = null;
        GizmoCandidate = null;
        GizmoTransform = null;
        RefreshAfterGesture = true;
        InteractionRevision++;
    }

    private static void WriteSocketTransform(JsonObject Root, string Id, TransformGizmoPose Transform)
    {
        var Socket = Root["sockets"]!.AsArray().Single(Node => Node!["socketId"]!.GetValue<string>() == Id)!;
        Socket["position"] = Vector(Transform.Position);
        Socket["rotation"] = new JsonArray(Transform.Rotation.X, Transform.Rotation.Y, Transform.Rotation.Z, Transform.Rotation.W);
        Socket["scale"] = Vector(Transform.Scale);
    }

    private JsonObject DescribeGizmo()
    {
        var Found = Context.TryGetElementRect(Preview.Viewport.ElementId, out var Bounds);
        var Gizmo = Found ? CreateGizmo(GizmoCandidate ?? StaticMesh!.SocketHierarchy, Bounds) : null;
        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["tool"] = GizmoTool,
            ["editable"] = !IsReadOnly && SelectedSocketId is not null,
            ["coordinateSpace"] = "socket-local",
            ["transformSpace"] = "parent-local",
            ["parentSocketId"] = StaticMesh!.Sockets.FirstOrDefault(Socket => Socket.SocketId == SelectedSocketId)?.ParentSocketId,
            ["dragging"] = GizmoDrag is not null,
            ["socketId"] = SelectedSocketId is not null ? SelectedSocketId : null,
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
            ["previewPose"] = GizmoCandidate?.ToJson(),
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


internal sealed class MeshSocketGizmoDrag(StaticMeshSocket Socket, Matrix4x4 Parent, TransformGizmoHandle Handle,
    Vector2 StartPointer, ulong Revision)
{
    public StaticMeshSocket Socket
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
    public TransformGizmoPose StartTransform
    {
        get;
    } = new(Socket.Position, Socket.Rotation, Socket.Scale);

    private readonly TransformGizmoDrag Drag = new(new TransformGizmoPose(Socket.Position, Socket.Rotation, Socket.Scale),
        Parent, Handle, StartPointer);

    public TransformGizmoPose Evaluate(Vector2 Pointer) { return Drag.Evaluate(Pointer); }
}
