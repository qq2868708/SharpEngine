using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpEngine.Assets;
using SharpEngine.Components;
using SharpEngine.UI;

namespace SharpEngine.Editor;

internal sealed partial class DataAssetEditorInstance
{
    private readonly HashSet<string> CollapsedStructures = new(StringComparer.Ordinal);

    private bool TryBuildDataStructure(string Label, Type Type, JsonNode? Value,
        string[] Path, JsonObject Root, out string Markup, bool ReadOnly = false)
    {
        Markup = string.Empty;
        var Members = DataAssetStructs.Members(Type);
        var Object = Value as JsonObject;
        if (Members.Count == 0 || Object is null)
        {
            return false;
        }
        var Id = "dataAsset-struct-" + Uri.EscapeDataString(PathJson(Path).ToJsonString());
        var Expanded = !CollapsedStructures.Contains(Id);
        var Children = new StringBuilder();
        if (Expanded)
        {
            object? TypedValue = null;
            foreach (var Member in Members)
            {
                var MemberPath = Path.Append(Member.Name).ToArray();
                var MemberId = Id + "-" + Uri.EscapeDataString(Member.Name);
                var MemberReadOnly = ReadOnly || IsReadOnly || Member.Set is null;
                var MemberValue = Object[Member.Name];
                if (Member.Set is null || !Object.ContainsKey(Member.Name))
                {
                    TypedValue ??= DataAssetSerializer.DeserializeValue(Type, Object);
                    MemberValue = DataAssetSerializer.SerializeValue(Member.PropertyType,
                        Member.Get!(TypedValue!));
                }
                Children.Append(BuildDataStructureMember(DataAssetStructs.DisplayName(Member),
                    Member.PropertyType, MemberValue, MemberPath, Root, MemberId,
                    MemberReadOnly, DataAssetStructs.Constraints(Member)));
            }
        }
        Markup = UiStructEditor.Render(Id, Label,
            Children.ToString(),
            "editor.data-asset-structure-fold:" + Uri.EscapeDataString(Id),
            Expanded, ReadOnly || IsReadOnly);
        return true;
    }

    private string BuildDataStructureMember(string Label, Type Type, JsonNode? Value,
        string[] Path, JsonObject Root, string Id, bool ReadOnly,
        DataPropertyAttribute? Constraints)
    {
        var HasStructure = TryBuildDataStructure(Label, Type, Value, Path, Root,
            out var Structure, ReadOnly);
        if (HasStructure)
        {
            return Structure;
        }
        if (CollectionEditing.ElementType(Type) is not null)
        {
            return BuildCollection(Label, Type, Value, Path, Root,
                Path.Take(1).ToArray(), ReadOnly);
        }
        if (MapEditing.ValueType(Type) is not null)
        {
            return BuildMap(Label, Type, Value, Path, Root, ReadOnly);
        }
        var Operation = Type == typeof(Color) ? "color" : "value";
        var Action = StructureBinding(Root, Path, Operation);
        if (IsNumber(Type))
        {
            var Input = new UiValueInput(Id, Value?.ToJsonString() ?? "null",
                Action, ReadOnly, IsInteger(Type) ? "integer" : "number");
            if (Constraints is not null && !double.IsNaN(Constraints.Minimum) &&
                !double.IsNaN(Constraints.Maximum))
            {
                return UiValueEditors.Range(Label, Input, Constraints.Minimum,
                    Constraints.Maximum, IsInteger(Type) ? 1 : 0.01);
            }
            return UiValueEditors.Number(Label, Input);
        }
        return MapEditing.ValueActionMarkup(Label, Id, Type, Value, Action,
            Axis => StructureBinding(Root, Path,
                "axis," + Axis.ToString(CultureInfo.InvariantCulture)),
            ReadOnly, BuildMapReferenceOptions(Type));
    }

    private string StructureBinding(JsonObject Root, string[] Path, string Operation)
    {
        return "editor.data-asset-structure:" + Uri.EscapeDataString(new JsonObject
        {
            ["path"] = PathJson(Path),
            ["expected"] = MapEditing.Fingerprint(Root[Path[0]]),
            ["operation"] = Operation,
        }.ToJsonString()) + ":";
    }

    private bool TryHandleStructureAction(string Action)
    {
        const string FoldPrefix = "editor.data-asset-structure-fold:";
        if (Action.StartsWith(FoldPrefix, StringComparison.Ordinal))
        {
            var Id = Uri.UnescapeDataString(Action[FoldPrefix.Length..]);
            if (!CollapsedStructures.Add(Id))
            {
                CollapsedStructures.Remove(Id);
            }
            PropertiesMarkup = null;
            return true;
        }
        const string Prefix = "editor.data-asset-structure:";
        if (!Action.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var Changed = Document.Edit("data-asset-structure", Root =>
        {
            var Parts = Action[Prefix.Length..].Split(':', 2);
            var Payload = JsonNode.Parse(Uri.UnescapeDataString(Parts[0]))!.AsObject();
            var Path = Payload["path"]!.AsArray().Select(Node => Node!.GetValue<string>()).ToArray();
            if (Path.Length == 0 || MapEditing.Fingerprint(Root[Path[0]]) !=
                Payload["expected"]!.GetValue<string>())
            {
                throw new InvalidDataException("REVISION_CONFLICT: structure changed while editing");
            }
            EnsureDataPathEditable(Path);
            var Type = Descriptor.Properties.Single(Property => Property.JsonName == Path[0]).PropertyType;
            MapEditing.SetValue(Root, Path, Payload["operation"]!.GetValue<string>(),
                Parts.Length > 1 ? Parts[1] : string.Empty, Type, GetDataPropertyType);
        }, out var Message);
        if (!Changed && Message.Length != 0)
        {
            Context.SetStatus(Message);
        }
        return true;
    }

    private void EnsureDataPathEditable(string[] Path)
    {
        var Type = Descriptor.Properties.Single(Property => Property.JsonName == Path[0]).PropertyType;
        foreach (var Segment in Path.Skip(1))
        {
            var Members = DataAssetStructs.Members(Type);
            if (Members.Count != 0)
            {
                var Member = Members.SingleOrDefault(Member => Member.Name == Segment) ??
                    throw new InvalidDataException("STRUCT_PATH: unknown serialized member");
                if (Member.Set is null)
                {
                    throw new InvalidDataException("STRUCT_READ_ONLY: this member is read only");
                }
                Type = Member.PropertyType;
            }
            else
            {
                Type = GetDataPropertyType(Type, Segment);
            }
        }
    }
}
