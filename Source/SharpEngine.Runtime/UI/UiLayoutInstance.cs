using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace SharpEngine.UI;

/// <summary>Per-use mutable UI state layered over a shared layout definition; it never saves source assets.</summary>
public sealed class UiLayoutInstance : IUiLayoutPart, IUiActionHandler, IDisposable
{
    private readonly XElement Definition;
    private readonly Dictionary<XElement, UiElement> SourceElements = [];
    private readonly Dictionary<string, UiElement> Elements = new(StringComparer.Ordinal);
    private ulong NextToken;
    private bool Disposed;
    private JsonObject Values = new();
    private IReadOnlyDictionary<string, IUiLayoutPart>? Parts;

    internal UiLayoutInstance(XElement Definition, string InstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InstanceId);
        this.InstanceId = InstanceId;
        this.Definition = new XElement(Definition);
        ActionPrefix = "ui.instance:" + Uri.EscapeDataString(InstanceId) + ":";
        Root = Wrap(this.Definition);
    }

    public string InstanceId
    {
        get;
    }
    public ulong StateRevision
    {
        get;
        private set;
    } = 1;
    public UiElement Root
    {
        get;
    }
    internal UiLayoutContext RenderContext
    {
        get;
        private set;
    }
    internal string ActionPrefix
    {
        get;
    }

    public UiElement? Find(string Id)
    {
        EnsureAlive();
        var Matches = Elements.Values.Where(Element => Element.Active && Element.Dynamic && Element.Id == Id).ToArray();
        var Source = OwnElements().Where(Element => Element.Attribute("id")?.Value == Id).ToArray();
        if (Matches.Length + Source.Length > 1)
        {
            throw new InvalidOperationException("UI_INSTANCE_ID_DUPLICATE: " + Id);
        }
        return Matches.FirstOrDefault() ?? (Source.Length == 0 ? null : Wrap(Source[0]));
    }

    public UiElement? FindByClass(string ClassName)
    {
        EnsureAlive();
        foreach (var Element in Elements.Values.Where(Element => Element.Active && Element.Dynamic))
        {
            if ((Element.GetAttribute("class") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ClassName, StringComparer.Ordinal))
            {
                return Element;
            }
        }
        var Source = OwnElements().FirstOrDefault(Element =>
        {
            var Found = SourceElements.TryGetValue(Element, out var Handle);
            var Classes = Found ? Handle!.GetAttribute("class") : Element.Attribute("class")?.Value;
            return (Classes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ClassName, StringComparer.Ordinal);
        });
        return Source is null ? null : Wrap(Source);
    }

    public void SetBindings(JsonObject Values, IReadOnlyDictionary<string, IUiLayoutPart>? Parts = null)
    {
        EnsureAlive();
        if (Parts is not null)
        {
            foreach (var Part in Parts.Values)
            {
                ValidateLayout(Part);
            }
        }
        this.Values = (JsonObject)Values.DeepClone();
        this.Parts = Parts;
        AdvanceRevision();
    }

    public string BuildLayout(UiLayoutContext Context) { return Render(Context, Values, Parts); }

    public string Render(UiLayoutContext Context, JsonObject Values,
        IReadOnlyDictionary<string, IUiLayoutPart>? Parts = null,
        Func<string, UiLayoutTemplate>? ResolveAsset = null)
    {
        EnsureAlive();
        if (Parts is not null)
        {
            foreach (var Part in Parts.Values)
            {
                ValidateLayout(Part);
            }
        }
        this.Parts = Parts;
        foreach (var Element in Elements.Values)
        {
            Element.WasRendered = false;
        }
        RenderContext = Context;
        var Markup = UiLayoutTemplate.FromElement(Definition).Render(Context, Values, Parts, ResolveAsset);
        var Output = XElement.Parse("<fragment>" + Markup + "</fragment>");
        var Targets = Output.Descendants().Where(Element => Element.Attribute("data-ui-instance-node") is not null).ToArray();
        foreach (var Target in Targets)
        {
            var Key = Target.Attribute("data-ui-instance-node")!.Value;
            var Prefix = InstanceId + ":";
            if (!Key.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var Found = Elements.TryGetValue(Key[Prefix.Length..], out var Element);
            if (Found && Element is not null && Element.Active && !Element.Dynamic)
            {
                Element.Apply(Target);
            }
        }
        foreach (var Child in Root.AddedChildren)
        {
            var Element = new XElement(Child.Element);
            Child.Apply(Element);
            Output.Add(Element);
        }
        return string.Concat(Output.Nodes().Select(Node => Node.ToString(SaveOptions.DisableFormatting)));
    }

    public bool TryHandleAction(string Action)
    {
        if (Disposed)
        {
            return false;
        }
        if (!Action.StartsWith(ActionPrefix, StringComparison.Ordinal))
        {
            var Handlers = new HashSet<IUiActionHandler>(ReferenceEqualityComparer.Instance);
            foreach (var Mount in Elements.Values.Where(Element => Element.Active && Element.MountedLayout is IUiActionHandler).ToArray())
            {
                var Handler = (IUiActionHandler)Mount.MountedLayout!;
                if (Mount.CanDispatch() && Handlers.Add(Handler) && Handler.TryHandleAction(Action))
                {
                    return true;
                }
            }
            foreach (var Handler in Parts?.Values.OfType<IUiActionHandler>() ?? [])
            {
                if (Handlers.Add(Handler) && Handler.TryHandleAction(Action))
                {
                    return true;
                }
            }
            return false;
        }
        var Payload = Action[ActionPrefix.Length..];
        var First = Payload.IndexOf(':');
        if (First < 0)
        {
            return true;
        }
        var Token = Payload[..First];
        var EventAndValue = Payload[(First + 1)..];
        var Second = EventAndValue.IndexOf(':');
        var Event = Second < 0 ? EventAndValue : EventAndValue[..Second];
        var Value = Second < 0 ? string.Empty : EventAndValue[(Second + 1)..];
        var Found = Elements.TryGetValue(Token, out var Element);
        if (Found && Element is not null && Element.Active)
        {
            Element.Dispatch(Event, Value);
        }
        return true;
    }

    public JsonObject Describe()
    {
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["instanceId"] = InstanceId,
            ["stateRevision"] = StateRevision,
            ["disposed"] = Disposed,
            ["elements"] = new JsonArray(Elements.Values.Where(Element => Element.Active && Element.Element.Name != "template" && (Element.Dynamic || Element.Changed))
                .Select(Element => (JsonNode)new JsonObject
                {
                    ["id"] = Element.Id,
                    ["elementId"] = Element.ElementId,
                    ["tag"] = Element.Element.Name.LocalName,
                    ["dynamic"] = Element.Dynamic,
                    ["visible"] = Element.Visible,
                    ["enabled"] = Element.Enabled,
                    ["text"] = Element.Text,
                    ["value"] = Element.GetAttribute("value"),
                    ["clickBound"] = Element.HasClickBinding,
                    ["changeBound"] = Element.HasChangeBinding,
                    ["attributes"] = new JsonObject(Element.Element.Attributes()
                        .Where(Attribute => Attribute.Name != "data-ui-instance-node")
                        .Select(Attribute => new KeyValuePair<string, JsonNode?>(Attribute.Name.LocalName,
                            JsonValue.Create(Element.GetAttribute(Attribute.Name.LocalName))))),
                }).ToArray()),
        };
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }
        foreach (var Element in Elements.Values)
        {
            Element.Release();
        }
        Elements.Clear();
        SourceElements.Clear();
        Parts = null;
        Values.Clear();
        Disposed = true;
        StateRevision++;
    }

    internal void Attach(UiElement Element)
    {
        EnsureAlive();
        var Nodes = Descendants(Element).ToArray();
        var Names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var Node in Nodes)
        {
            if (Node.Owner is not null)
            {
                throw new InvalidOperationException("UI_INSTANCE_CHILD_ALREADY_OWNED");
            }
            if (!Names.Add(Node.Id) || Find(Node.Id) is not null)
            {
                throw new InvalidOperationException("UI_INSTANCE_ID_DUPLICATE: " + Node.Id);
            }
            if (Node.MountedLayout is not null)
            {
                ValidateLayout(Node.MountedLayout);
            }
        }
        foreach (var Node in Nodes)
        {
            Register(Node);
            Node.Element.SetAttributeValue("id", "ui/" + Uri.EscapeDataString(InstanceId) + "/" + Uri.EscapeDataString(Node.Id) + "/" + Node.Token);
        }
    }

    internal void ValidateLayout(IUiLayoutPart Layout)
    {
        if (ReferenceEquals(Layout, this))
        {
            throw new InvalidOperationException("UI_INSTANCE_DEPENDENCY_CYCLE");
        }
        var Instance = Layout as UiLayoutInstance;
        if (Instance is not null && Instance.ContainsLayout(this))
        {
            throw new InvalidOperationException("UI_INSTANCE_DEPENDENCY_CYCLE");
        }
    }

    private bool ContainsLayout(UiLayoutInstance Target)
    {
        return Elements.Values.Where(Element => Element.Active).Select(Element => Element.MountedLayout)
            .Concat(Parts?.Values ?? []).OfType<UiLayoutInstance>().Any(Instance => ReferenceEquals(Instance, Target) || Instance.ContainsLayout(Target));
    }

    private static IEnumerable<UiElement> Descendants(UiElement Element)
    {
        yield return Element;
        foreach (var Child in Element.AddedChildren)
        {
            foreach (var Node in Descendants(Child))
            {
                yield return Node;
            }
        }
    }

    internal bool Remove(UiElement Element)
    {
        EnsureAlive();
        if (ReferenceEquals(Element, Root))
        {
            throw new InvalidOperationException("UI_INSTANCE_ROOT_CANNOT_BE_REMOVED");
        }
        if (Element.Dynamic)
        {
            Element.Parent!.AddedChildren.Remove(Element);
            Element.Parent = null;
        }
        else
        {
            var SourceNodes = SourceElements.Keys.Where(Node => Node.AncestorsAndSelf().Contains(Element.Element)).ToArray();
            foreach (var Node in SourceNodes)
            {
                var Handle = SourceElements[Node];
                foreach (var Child in Descendants(Handle).ToArray())
                {
                    Elements.Remove(Child.Token);
                }
                Handle.Release();
                SourceElements.Remove(Node);
            }
            Element.Element.Remove();
        }
        foreach (var Child in Descendants(Element).ToArray())
        {
            Elements.Remove(Child.Token);
        }
        Element.Release();
        AdvanceRevision();
        return true;
    }

    internal void EnsureAlive()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
    }

    internal void AdvanceRevision()
    {
        EnsureAlive();
        StateRevision++;
    }

    private UiElement Wrap(XElement Element)
    {
        var Found = SourceElements.TryGetValue(Element, out var Handle);
        if (Found)
        {
            return Handle!;
        }
        Handle = new UiElement(Element);
        Register(Handle);
        SourceElements.Add(Element, Handle);
        var Parent = Element.Parent;
        while (Parent is not null && Parent.Name.LocalName.StartsWith("ui-", StringComparison.Ordinal))
        {
            Parent = Parent.Parent;
        }
        if (Parent is not null)
        {
            Handle.Parent = Wrap(Parent);
        }
        return Handle;
    }

    private void Register(UiElement Element)
    {
        Element.Owner = this;
        Element.Active = true;
        Element.Token = (++NextToken).ToString(CultureInfo.InvariantCulture);
        Elements.Add(Element.Token, Element);
    }

    private IEnumerable<XElement> OwnElements()
    {
        return Definition.Descendants().Where(Element => !Element.Name.LocalName.StartsWith("ui-", StringComparison.Ordinal) &&
            !Element.Ancestors().Any(Ancestor => Ancestor.Name == "ui-slot" || Ancestor.Name == "ui-repeat" || Ancestor.Name == "ui-use"));
    }
}
