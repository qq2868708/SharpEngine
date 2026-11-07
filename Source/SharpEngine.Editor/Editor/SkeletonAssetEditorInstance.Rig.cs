using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpEngine.Animation;
using SharpEngine.Mathematics;

namespace SharpEngine.Editor;

internal sealed partial class SkeletonAssetEditorInstance
{
    private string SelectedKind = "bone";
    private string SelectedId = string.Empty;
    private string Diagnostic = string.Empty;

    private void EnsureSelection()
    {
        var Exists = SelectedKind switch
        {
            "socket" => Skeleton.Sockets.Any(Socket => Socket.SocketId == SelectedId),
            "virtual" => Skeleton.VirtualBones.Any(Bone => Bone.BoneId == SelectedId),
            _ => Skeleton.Bones.Any(Bone => Bone.BoneId == SelectedId),
        };
        if (!Exists)
        {
            SelectedKind = "bone";
            SelectedId = Skeleton.Bones[0].BoneId;
        }
    }

    private void EditRig(string Kind, Action<JsonObject> Mutation)
    {
        InteractionRevision++;
        var Changed = Document.Edit(Kind, Root =>
        {
            Root["schemaVersion"] = SkeletonAsset.CurrentSchemaVersion;
            Root["previewMeshId"] ??= null;
            Root["sockets"] ??= new JsonArray();
            Root["virtualBones"] ??= new JsonArray();
            Mutation(Root);
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
    }

    private void HandleRigAction(string Operation)
    {
        var Parts = Operation.Split(':', 3);
        if (Parts[0] == "select" && Parts.Length == 3)
        {
            if (Parts[1] is not ("bone" or "socket" or "virtual"))
            {
                Diagnostic = "Unsupported skeleton selection kind";
                return;
            }
            SelectedKind = Parts[1];
            SelectedId = Parts[2];
            EnsureSelection();
            return;
        }
        if (Operation.StartsWith("axis:", StringComparison.Ordinal))
        {
            var AxisParts = Operation.Split(':', 5);
            if (AxisParts.Length != 5 || SelectedKind != "socket" || Uri.UnescapeDataString(AxisParts[1]) != SelectedId ||
                AxisParts[2] is not ("position" or "rotation" or "scale"))
            {
                Diagnostic = "SKELETON_SELECTION: invalid or stale axis input";
                return;
            }
            EditRig("socket-axis", Root =>
            {
                var Node = Root["sockets"]!.AsArray().Single(Node => Node!["socketId"]!.GetValue<string>() == SelectedId)!.AsObject();
                EditorValueControls.SetVectorAxis(Node, AxisParts[2], int.Parse(AxisParts[3], CultureInfo.InvariantCulture),
                    float.Parse(AxisParts[4], NumberStyles.Float, CultureInfo.InvariantCulture), AxisParts[2] == "rotation");
            });
            return;
        }
        var Kind = SelectedKind;
        var Id = SelectedId;
        string? CreatedId = null;
        string? CreatedKind = null;
        EditRig(Operation, Root =>
        {
            var Sockets = Root["sockets"]!.AsArray();
            var VirtualBones = Root["virtualBones"]!.AsArray();
            var Parent = Kind == "socket" ? Skeleton.Sockets.Single(Socket => Socket.SocketId == Id).ParentBoneId : Id;
            if (Operation is "add-socket" or "add-virtual-bone")
            {
                CreatedKind = Operation == "add-socket" ? "socket" : "virtual";
                CreatedId = Guid.NewGuid().ToString("N");
                if (CreatedKind == "socket")
                {
                    Sockets.Add(new JsonObject
                    {
                        ["socketId"] = CreatedId,
                        ["name"] = UniqueName("Socket", Skeleton.Sockets.Select(Socket => Socket.Name)),
                        ["parentBoneId"] = Parent,
                        ["position"] = new JsonArray(0, 0, 0),
                        ["rotation"] = new JsonArray(0, 0, 0, 1),
                        ["scale"] = new JsonArray(1, 1, 1),
                    });
                }
                else
                {
                    VirtualBones.Add(new JsonObject
                    {
                        ["boneId"] = CreatedId,
                        ["name"] = UniqueName("VB", Skeleton.VirtualBones.Select(Bone => Bone.Name)),
                        ["sourceBoneId"] = Skeleton.Bones[0].BoneId,
                        ["targetBoneId"] = Parent,
                    });
                }
                return;
            }
            var Values = Kind == "socket" ? Sockets : VirtualBones;
            var IdKey = Kind == "socket" ? "socketId" : "boneId";
            var Node = Values.FirstOrDefault(Node => Node![IdKey]!.GetValue<string>() == Id)?.AsObject();
            if (Kind == "bone" || Node is null)
            {
                throw new InvalidDataException("Select a Socket or Virtual Bone; imported bones are read only");
            }
            if (Operation == "remove")
            {
                Values.Remove(Node);
            }
            else if (Operation == "duplicate")
            {
                var Copy = Node.DeepClone().AsObject();
                CreatedId = Guid.NewGuid().ToString("N");
                CreatedKind = Kind;
                Copy[IdKey] = CreatedId;
                Copy["name"] = UniqueName(Node["name"]!.GetValue<string>(), Values.Select(Value => Value!["name"]!.GetValue<string>()));
                Values.Add(Copy);
            }
            else if (Parts[0] == "field" && Parts.Length == 3)
            {
                var Key = Parts[1];
                var Text = Parts[2].Trim();
                var Allowed = Kind == "socket"
                    ? new[] { "name", "parentBoneId", "position", "rotation", "scale" }
                    : new[] { "name", "sourceBoneId", "targetBoneId" };
                if (!Allowed.Contains(Key, StringComparer.Ordinal))
                {
                    throw new InvalidDataException("Unsupported skeleton field: " + Key);
                }
                if (Key is "position" or "scale" or "rotation")
                {
                    var Numbers = Text.Split(',', StringSplitOptions.TrimEntries).Select(Value =>
                        float.Parse(Value, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
                    if (Numbers.Length != 3 || Numbers.Any(Value => !float.IsFinite(Value)))
                    {
                        throw new InvalidDataException("Enter three finite numbers separated by commas");
                    }
                    if (Key == "rotation")
                    {
                        var Rotation = QuaternionMath.Euler(new Vector3(Numbers[0], Numbers[1], Numbers[2]));
                        Node["rotation"] = new JsonArray(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W);
                    }
                    else
                    {
                        Node[Key] = new JsonArray(Numbers.Select(Value => JsonValue.Create(Value)).ToArray());
                    }
                }
                else
                {
                    Node[Key] = Text;
                }
            }
            else
            {
                throw new InvalidDataException("Unsupported skeleton operation: " + Operation);
            }
        });
        if (CreatedId is not null && Diagnostic.Length == 0)
        {
            SelectedKind = CreatedKind!;
            SelectedId = CreatedId;
        }
    }

    private static string UniqueName(string Prefix, IEnumerable<string> Existing)
    {
        var Names = Existing.ToHashSet(StringComparer.Ordinal);
        var Name = Prefix;
        var Index = 1;
        while (Names.Contains(Name))
        {
            Name = Prefix + "_" + Index.ToString(CultureInfo.InvariantCulture);
            Index++;
        }
        return Name;
    }

    private string BuildRigDetails()
    {
        var Markup = new StringBuilder("<h3>Selected " + AssetEditorInstanceUtilities.Escape(SelectedKind) + "</h3>");
        Markup.Append(AssetEditorInstanceUtilities.ReadOnlyField("Stable ID", SelectedId));
        if (Diagnostic.Length != 0)
        {
            Markup.Append("<div class=\"ui-diagnostic ui-diagnostic-error\">")
                .Append(AssetEditorInstanceUtilities.Escape(Diagnostic)).Append("</div>");
        }
        if (SelectedKind == "bone")
        {
            Markup.Append("<p>Imported bone: read only. Add a Socket or Virtual Bone using the toolbar.</p>");
            return Markup.ToString();
        }
        if (SelectedKind == "socket")
        {
            var Socket = Skeleton.Sockets.Single(Socket => Socket.SocketId == SelectedId);
            Field("Name", "name", Socket.Name);
            BoneField("Parent Bone", "parentBoneId", Socket.ParentBoneId);
            Markup.Append(EditorValueControls.Transform("skeletonField-", Socket.LocalTransform.Position,
                QuaternionMath.ToEuler(Socket.LocalTransform.Rotation), Socket.LocalTransform.Scale,
                (Field, Axis) => "editor.skeleton:axis:" + Uri.EscapeDataString(Socket.SocketId) + ":" + Field + ":" + Axis + ":", IsReadOnly));
        }
        else
        {
            var Bone = Skeleton.VirtualBones.Single(Bone => Bone.BoneId == SelectedId);
            Field("Name", "name", Bone.Name);
            BoneField("Source / Parent", "sourceBoneId", Bone.SourceBoneId);
            BoneField("Target", "targetBoneId", Bone.TargetBoneId);
        }
        Markup.Append("<button id=\"skeletonRemove\" class=\"asset-inline-button\" data-action=\"editor.skeleton:remove\"")
            .Append(IsReadOnly ? " disabled=\"disabled\"" : string.Empty).Append(">Remove Selected</button>");
        return Markup.ToString();

        void Field(string Label, string Key, string Value)
        {
            Markup.Append(AssetEditorInstanceUtilities.Field(Label, Value, "editor.skeleton:field:" + Key + ":",
                IsReadOnly, Id: "skeletonField-" + Key));
        }
        void BoneField(string Label, string Key, string Value)
        {
            var Options = Skeleton.Bones.Select(Bone => (Bone.BoneId, Bone.Name))
                .Concat(Skeleton.VirtualBones.Where(Bone => Bone.BoneId != SelectedId).Select(Bone => (Bone.BoneId, Bone.Name)));
            Markup.Append(SharpEngine.UI.UiValueEditors.Choice(Label, "skeletonField-" + Key, Value, Options,
                "editor.skeleton:field:" + Key + ":", IsReadOnly));
        }
    }
}
