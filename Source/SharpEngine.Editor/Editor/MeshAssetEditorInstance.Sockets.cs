using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Mathematics;

namespace SharpEngine.Editor;

internal sealed partial class MeshAssetEditorInstance
{
    private string? SelectedSocketId;
    private string Diagnostic = string.Empty;

    private void EnsureSocketSelection()
    {
        if (SelectedSocketId is not null && !StaticMesh!.Sockets.Any(Socket => Socket.SocketId == SelectedSocketId))
        {
            SelectedSocketId = StaticMesh.Sockets.FirstOrDefault()?.SocketId;
        }
    }

    private bool EditSockets(string Kind, Action<JsonObject> Mutation)
    {
        InteractionRevision++;
        var Changed = Document.Edit(Kind, Root =>
        {
            var Candidate = StaticMeshAsset.Parse(Root.ToJsonString(), Context.Path, Asset.Id).ToJson();
            Mutation(Candidate);
            Root.Clear();
            foreach (var Field in Candidate)
            {
                Root[Field.Key] = Field.Value?.DeepClone();
            }
        }, out var Message);
        Diagnostic = Changed || Message.Length == 0 ? string.Empty : Message;
        if (Changed)
        {
            RefreshAsset();
        }
        else if (Diagnostic.Length != 0)
        {
            Context.SetStatus(Diagnostic);
        }
        return Changed;
    }

    private void HandleSocketAction(string Operation)
    {
        InteractionRevision++;
        if (Operation == "select-root")
        {
            EndTextInput();
            SelectedSocketId = null;
            Diagnostic = string.Empty;
            return;
        }
        if (Operation.StartsWith("select:", StringComparison.Ordinal))
        {
            EndTextInput();
            var Id = Operation["select:".Length..];
            if (StaticMesh!.Sockets.Any(Socket => Socket.SocketId == Id))
            {
                SelectedSocketId = Id;
                Diagnostic = string.Empty;
            }
            else
            {
                Diagnostic = "STATIC_MESH_SOCKET_SELECTION: socket does not exist";
            }
            return;
        }
        if (Operation.StartsWith("field-for:", StringComparison.Ordinal))
        {
            var Target = Operation.Split(':', 4);
            if (Target.Length != 4 || Uri.UnescapeDataString(Target[1]) != SelectedSocketId)
            {
                Diagnostic = "STATIC_MESH_SOCKET_SELECTION: field target no longer matches the selected Socket";
                return;
            }
            Operation = "field:" + Target[2] + ":" + Target[3];
        }
        if (Operation.StartsWith("axis:", StringComparison.Ordinal))
        {
            var Parts = Operation.Split(':', 5);
            if (Parts.Length != 5 || Uri.UnescapeDataString(Parts[1]) != SelectedSocketId || Parts[2] is not ("position" or "rotation" or "scale"))
            {
                Diagnostic = "STATIC_MESH_SOCKET_SELECTION: invalid or stale axis input";
                return;
            }
            EditSockets("socket-axis", Root =>
            {
                var Node = Root["sockets"]!.AsArray().Single(Node => Node!["socketId"]!.GetValue<string>() == SelectedSocketId)!.AsObject();
                var Axis = int.Parse(Parts[3], CultureInfo.InvariantCulture);
                var Number = float.Parse(Parts[4], NumberStyles.Float, CultureInfo.InvariantCulture);
                EditorValueControls.SetVectorAxis(Node, Parts[2], Axis, Number, Parts[2] == "rotation");
            });
            return;
        }
        string? CreatedId = null;
        var Changed = EditSockets(Operation, Root =>
        {
            var Sockets = Root["sockets"]!.AsArray();
            if (Operation == "add-socket")
            {
                CreatedId = Guid.NewGuid().ToString("N");
                Sockets.Add(new JsonObject
                {
                    ["socketId"] = CreatedId,
                    ["name"] = UniqueSocketName("Socket"),
                    ["parentSocketId"] = SelectedSocketId,
                    ["position"] = new JsonArray(0, 0, 0),
                    ["rotation"] = new JsonArray(0, 0, 0, 1),
                    ["scale"] = new JsonArray(1, 1, 1),
                });
                return;
            }
            var Node = Sockets.FirstOrDefault(Node => Node!["socketId"]!.GetValue<string>() == SelectedSocketId) as JsonObject;
            if (Node is null)
            {
                throw new InvalidDataException("STATIC_MESH_SOCKET_SELECTION: select a Socket first");
            }
            if (Operation == "remove")
            {
                if (StaticMesh!.SocketHierarchy.HasChildren(SelectedSocketId!))
                {
                    throw new InvalidDataException("STATIC_MESH_SOCKET_CHILDREN: move or remove child Sockets before deleting this node");
                }
                Sockets.Remove(Node);
                return;
            }
            if (Operation == "duplicate")
            {
                var Copy = Node.DeepClone().AsObject();
                CreatedId = Guid.NewGuid().ToString("N");
                Copy["socketId"] = CreatedId;
                Copy["name"] = UniqueSocketName(Node["name"]!.GetValue<string>());
                Sockets.Add(Copy);
                return;
            }
            var Parts = Operation.Split(':', 3);
            if (Parts.Length != 3 || Parts[0] != "field" || Parts[1] is not ("name" or "parentSocketId" or "position" or "rotation" or "scale"))
            {
                throw new InvalidDataException("STATIC_MESH_SOCKET_OPERATION: unsupported operation or field");
            }
            var Key = Parts[1];
            var Text = Parts[2].Trim();
            if (Key == "parentSocketId")
            {
                ReparentSocket(Node, Text.Length == 0 ? null : Text);
                return;
            }
            if (Key == "name")
            {
                Node[Key] = Text;
                return;
            }
            var Numbers = Text.Split(',', StringSplitOptions.TrimEntries)
                .Select(Value => float.Parse(Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            if (Numbers.Length != 3 || Numbers.Any(Value => !float.IsFinite(Value)))
            {
                throw new InvalidDataException("STATIC_MESH_SOCKET_TRANSFORM: enter three finite numbers separated by commas");
            }
            if (Key == "rotation")
            {
                var Rotation = QuaternionMath.Euler(new Vector3(Numbers[0], Numbers[1], Numbers[2]));
                Node[Key] = new JsonArray(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W);
            }
            else
            {
                Node[Key] = new JsonArray(Numbers.Select(Value => JsonValue.Create(Value)).ToArray());
            }
        });
        if (Changed && CreatedId is not null)
        {
            SelectedSocketId = CreatedId;
        }
    }

    private string UniqueSocketName(string Prefix)
    {
        var Names = StaticMesh!.Sockets.Select(Socket => Socket.Name).ToHashSet(StringComparer.Ordinal);
        var Name = Prefix;
        var Index = 1;
        while (Names.Contains(Name))
        {
            Name = Prefix + "_" + Index++;
        }
        return Name;
    }

    private readonly SharpEngine.UI.UiTreeView SocketTreeView = new([]);

    private readonly HashSet<string> CollapsedSocketNodes = new(StringComparer.Ordinal);

    private string BuildSocketsMarkup()
    {
        var RootKey = "mesh:" + Asset.Id;
        var Nodes = new List<SharpEngine.UI.UiTreeNode>
        {
            new(RootKey, null),
        };
        Nodes.AddRange(StaticMesh!.Sockets.Select(Socket => new SharpEngine.UI.UiTreeNode("socket:" + Socket.SocketId, Socket.ParentSocketId is null ? RootKey : "socket:" + Socket.ParentSocketId)));
        var ById = StaticMesh.Sockets.ToDictionary(Socket => Socket.SocketId, StringComparer.Ordinal);
        var Rows = SharpEngine.UI.UiTreeProjection.Visible(Nodes, CollapsedSocketNodes).Select(Node =>
        {
            var Root = Node.Key == RootKey;
            var SocketId = Root ? string.Empty : Node.Key["socket:".Length..];
            var Selected = Root ? SelectedSocketId is null : SelectedSocketId == SocketId;
            return new SharpEngine.UI.UiTreeRow(Node.Key, Root ? Asset.Name : ById[SocketId].Name,
                Root ? "Mesh (read only)" : "Socket", EditorResourceUris.IconSource(Root ? "mesh" : "move"), Node.Depth,
                Selected, Node.HasChildren, Node.Expanded,
                Root ? "editor.mesh-socket:select-root" : "editor.mesh-socket:select:" + SocketId,
                "editor.mesh-tree-toggle:" + Node.Key, Root ? "meshSocketRoot" : "meshSocketNode-" + SocketId,
                "meshSocketToggle-" + Node.Key);
        }).ToArray();
        SocketTreeView.SetRows(Rows);
        return "<div class=\"asset-tree\">" + SocketTreeView.BuildLayout(default) + "</div>";
    }

    private string BuildSocketDetails()
    {
        var Markup = new StringBuilder("<h3>Socket</h3>");
        if (Diagnostic.Length != 0)
        {
            Markup.Append("<div class=\"ui-diagnostic ui-diagnostic-error\">")
                .Append(AssetEditorInstanceUtilities.Escape(Diagnostic)).Append("</div>");
        }
        var Socket = StaticMesh!.Sockets.FirstOrDefault(Socket => Socket.SocketId == SelectedSocketId);
        if (Socket is null)
        {
            return Markup.Append("<p>Mesh root: read only. Select it to add a root Socket; select a Socket to add its child.</p>").ToString();
        }
        Markup.Append(AssetEditorInstanceUtilities.ReadOnlyField("Stable ID", Socket.SocketId));
        Field("Name", "name", Socket.Name);
        var Excluded = StaticMesh.SocketHierarchy.GetSubtreeIds(Socket.SocketId).ToHashSet(StringComparer.Ordinal);
        var ParentOptions = new List<(string Id, string Label)>
        {
            (string.Empty, Asset.Name + " (Mesh root)"),
        };
        ParentOptions.AddRange(StaticMesh.Sockets.Where(Parent => !Excluded.Contains(Parent.SocketId)).Select(Parent => (Parent.SocketId, Parent.Name)));
        Markup.Append(SharpEngine.UI.UiValueEditors.Choice("Parent", "meshSocketField-parentSocketId", Socket.ParentSocketId ?? string.Empty,
            ParentOptions, "editor.mesh-socket:field:parentSocketId:", IsReadOnly));
        if (Excluded.Count > 1)
        {
            Markup.Append("<p>Move or remove child Sockets before deleting this node.</p>");
        }
        Markup.Append(EditorValueControls.Transform("meshSocketField-", Socket.Position,
            QuaternionMath.ToEuler(Socket.Rotation), Socket.Scale,
            (Field, Axis) => "editor.mesh-socket:axis:" + Uri.EscapeDataString(Socket.SocketId) + ":" + Field + ":" + Axis + ":", IsReadOnly));
        Markup.Append("<p>TRS is relative to Parent. Reparent keeps the Mesh-space frame. Esc cancels a Gizmo drag.</p>");
        return Markup.ToString();

        void Field(string Label, string Key, string Value)
        {
            var Id = "meshSocketField-" + Key;
            Markup.Append(AssetEditorInstanceUtilities.Field(Label, Value,
                "editor.mesh-socket:field-for:" + Uri.EscapeDataString(Socket.SocketId) + ":" + Key + ":",
                IsReadOnly, Id: Id, FocusAction: "editor.mesh-input:begin:" + Id, EndEditAction: "editor.mesh-input:end:" + Id));
        }
    }

    private void ReparentSocket(JsonObject Node, string? ParentId)
    {
        var Id = Node["socketId"]!.GetValue<string>();
        var Value = StaticMesh!;
        if (ParentId is not null && (!Value.SocketHierarchy.ModelMatrices.ContainsKey(ParentId) ||
            Value.SocketHierarchy.GetSubtreeIds(Id).Contains(ParentId, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("STATIC_MESH_SOCKET_PARENT: select the Mesh root or an existing Socket outside the selected subtree");
        }
        if (Node["parentSocketId"]?.GetValue<string>() == ParentId)
        {
            return;
        }
        var Parent = ParentId is null ? Matrix4x4.Identity : Value.SocketHierarchy.ModelMatrices[ParentId];
        var Invertible = TransformMath.TryInverse(Parent, out var Inverse);
        if (!Invertible)
        {
            throw new InvalidDataException("STATIC_MESH_SOCKET_TRANSFORM: parent frame is not invertible");
        }
        var Representable = TransformMath.TryDecompose(Value.SocketHierarchy.ModelMatrices[Id] * Inverse,
            out var Position, out var Rotation, out var Scale);
        if (!Representable || Scale.X <= 0 || Scale.Y <= 0 || Scale.Z <= 0)
        {
            throw new InvalidDataException("STATIC_MESH_SOCKET_REPARENT: preserving the Mesh-space frame would require shear or invalid local TRS");
        }
        Node["parentSocketId"] = ParentId;
        Node["position"] = Vector(Position);
        Node["rotation"] = new JsonArray(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W);
        Node["scale"] = Vector(Scale);
    }

    private JsonObject DescribeSocketTree()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["root"] = new JsonObject
            {
                ["kind"] = "mesh",
                ["assetId"] = Asset.Id,
                ["name"] = Asset.Name,
                ["editable"] = false,
            },
            ["selectedKind"] = SelectedSocketId is null ? "mesh" : "socket",
            ["collapsedKeys"] = new JsonArray(CollapsedSocketNodes.Order(StringComparer.Ordinal).Select(Key => (JsonNode)Key).ToArray()),
            ["nodes"] = new JsonArray(StaticMesh!.Sockets.Select(Socket => (JsonNode)new JsonObject
            {
                ["socketId"] = Socket.SocketId,
                ["name"] = Socket.Name,
                ["parentSocketId"] = Socket.ParentSocketId,
            }).ToArray()),
        };
    }

    private IReadOnlyList<AssetDocumentDiagnostic> SocketDiagnostics()
    {
        return Diagnostic.Length == 0 ? [] : [new("error", "STATIC_MESH_SOCKET_AUTHORING_INVALID", Diagnostic)];
    }

    private JsonArray DescribeSocketDiagnostics()
    {
        return new JsonArray(SocketDiagnostics().Select(Value => (JsonNode)new JsonObject
        {
            ["code"] = Value.Code,
            ["severity"] = Value.Severity,
            ["objectId"] = Asset.Id,
            ["fieldPath"] = "sockets",
            ["message"] = Value.Message,
            ["documentRevision"] = Document.Revision,
        }).ToArray());
    }
}
