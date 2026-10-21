using System.Xml.Linq;

namespace SharpEngine.UI;

/// <summary>A handle owned by one layout instance. Native pointers never escape the UI host.</summary>
public class UiElement
{
    internal readonly XElement Element;
    internal readonly bool Dynamic;
    internal readonly string LogicalId;
    internal readonly Dictionary<string, string> AttributeOverrides = new(StringComparer.Ordinal);
    internal readonly List<UiElement> AddedChildren = [];
    internal UiLayoutInstance? Owner;
    internal UiElement? Parent;
    internal string Token = string.Empty;
    internal bool Active;
    internal bool Changed;
    internal bool WasRendered;
    internal bool HasClickBinding
    {
        get
        {
            return ClickHandlers is not null;
        }
    }
    internal bool HasChangeBinding
    {
        get
        {
            return ChangeHandlers is not null;
        }
    }
    internal string? TextOverride;
    private Action? ClickHandlers;
    private Action<string>? ChangeHandlers;
    private bool VisibleValue = true;
    private bool? EnabledOverride;
    private bool? RenderedEnabled;
    internal IUiLayoutPart? MountedLayout;
    internal UiLayoutContext? MountedContext;

    internal UiElement(XElement Element)
    {
        this.Element = Element;
        LogicalId = Element.Attribute("id")?.Value ?? string.Empty;
    }

    protected UiElement(string Tag, string Id, string Text = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        Element = new XElement(Tag);
        LogicalId = Id;
        Dynamic = true;
        Element.SetAttributeValue("id", Id);
        if (Tag != "input")
        {
            Element.Value = Text;
        }
    }

    public string Id
    {
        get
        {
            return LogicalId;
        }
    }
    public string ElementId
    {
        get
        {
            EnsureActive();
            if (Owner is null)
            {
                throw new InvalidOperationException("UI_INSTANCE_ELEMENT_NOT_ATTACHED");
            }
            return Element.Attribute("id")?.Value ?? string.Empty;
        }
    }
    public string Text
    {
        get
        {
            EnsureActive();
            return TextOverride ?? Element.Value;
        }
        set
        {
            EnsureActive();
            if (Element.HasElements)
            {
                throw new InvalidOperationException("UI_INSTANCE_TEXT_REQUIRES_LEAF");
            }
            TextOverride = value;
            if (!Dynamic)
            {
                Element.Value = string.Empty;
            }
            Touch();
        }
    }
    public bool Enabled
    {
        get
        {
            return EnabledOverride ?? RenderedEnabled ?? !Element.Attributes().Any(Attribute => Attribute.Name == "disabled");
        }
        set
        {
            EnsureActive();
            EnabledOverride = value;
            Touch();
        }
    }
    public bool Visible
    {
        get
        {
            return VisibleValue;
        }
        set
        {
            EnsureActive();
            VisibleValue = value;
            Touch();
        }
    }

    public event Action Clicked
    {
        add
        {
            EnsureActive();
            CheckActionConflict("data-action", "action");
            ClickHandlers += value;
            Touch();
        }
        remove
        {
            EnsureActive();
            ClickHandlers -= value;
            Touch();
        }
    }
    public event Action<string> ValueChanged
    {
        add
        {
            EnsureActive();
            if (Element.Name != "input")
            {
                throw new InvalidOperationException("UI_INSTANCE_VALUE_REQUIRES_INPUT");
            }
            CheckActionConflict("data-change-action");
            ChangeHandlers += value;
            Touch();
        }
        remove
        {
            EnsureActive();
            ChangeHandlers -= value;
            Touch();
        }
    }

    public UiElement Add(UiElement Child)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(Child);
        if (Element.Name != "template" && Element.Name != "div" && Element.Name != "span" && Element.Name != "form")
        {
            throw new InvalidOperationException("UI_INSTANCE_CONTAINER_REQUIRED");
        }
        if (!Child.Dynamic || Child.Parent is not null || ReferenceEquals(Child, this))
        {
            throw new InvalidOperationException("UI_INSTANCE_CHILD_ALREADY_OWNED");
        }
        for (var Ancestor = this; Ancestor is not null; Ancestor = Ancestor.Parent)
        {
            if (ReferenceEquals(Ancestor, Child))
            {
                throw new InvalidOperationException("UI_INSTANCE_DEPENDENCY_CYCLE");
            }
        }
        if (Owner is not null)
        {
            Owner.Attach(Child);
        }
        Child.Parent = this;
        AddedChildren.Add(Child);
        Touch();
        return Child;
    }

    public UiElement AddLayout(string Id, IUiLayoutPart Layout, UiLayoutContext? Context = null)
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(Layout);
        Owner?.ValidateLayout(Layout);
        var Mount = new UiContainer(Id)
        {
            MountedLayout = Layout,
            MountedContext = Context,
        };
        Add(Mount);
        return Mount;
    }

    public bool Remove()
    {
        EnsureActive();
        if (Owner is null)
        {
            return false;
        }
        return Owner.Remove(this);
    }

    public void SetAttribute(string Name, string? Value)
    {
        EnsureActive();
        System.Xml.XmlConvert.VerifyNCName(Name);
        if (Name == "id" || Name.StartsWith("data-ui-instance-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("UI_INSTANCE_ID_IS_IMMUTABLE");
        }
        if ((Name == "data-action" || Name == "action") && ClickHandlers is not null ||
            Name == "data-change-action" && ChangeHandlers is not null)
        {
            throw new InvalidOperationException("UI_INSTANCE_EVENT_ALREADY_BOUND");
        }
        if (Value is null)
        {
            AttributeOverrides.Remove(Name);
            Element.Attribute(Name)?.Remove();
        }
        else if (Dynamic)
        {
            Element.SetAttributeValue(Name, Value);
        }
        else
        {
            AttributeOverrides[Name] = Value;
            Element.SetAttributeValue(Name, string.Empty);
        }
        Touch();
    }

    public string? GetAttribute(string Name)
    {
        EnsureActive();
        return AttributeOverrides.GetValueOrDefault(Name) ?? Element.Attribute(Name)?.Value;
    }

    internal void Apply(XElement Target)
    {
        WasRendered = true;
        foreach (var Pair in AttributeOverrides)
        {
            Target.SetAttributeValue(Pair.Key, Pair.Value);
        }
        if (TextOverride is not null)
        {
            Target.Value = TextOverride;
        }
        if (EnabledOverride is not null)
        {
            Target.SetAttributeValue("disabled", EnabledOverride.Value ? null : "disabled");
        }
        RenderedEnabled = Target.Attribute("disabled") is null;
        if (!VisibleValue)
        {
            Target.SetAttributeValue("style", (Target.Attribute("style")?.Value ?? string.Empty) + ";display:none;");
        }
        if (Owner is not null && Active && (Dynamic || Changed))
        {
            Target.SetAttributeValue("data-ui-instance-node", Owner.InstanceId + ":" + Token);
            if (ClickHandlers is not null)
            {
                Target.SetAttributeValue("data-action", Owner.ActionPrefix + Token + ":click");
            }
            if (ChangeHandlers is not null)
            {
                Target.SetAttributeValue("data-change-action", Owner.ActionPrefix + Token + ":change:");
            }
        }
        if (MountedLayout is not null)
        {
            var Context = MountedContext ?? Owner?.RenderContext ?? new UiLayoutContext(640, 480);
            var Fragment = XElement.Parse("<fragment>" + MountedLayout.BuildLayout(Context) + "</fragment>");
            Target.Add(Fragment.Nodes());
        }
        foreach (var Child in AddedChildren)
        {
            var Element = new XElement(Child.Element);
            Child.Apply(Element);
            Target.Add(Element);
        }
    }

    internal void Dispatch(string Event, string Value)
    {
        if (!CanDispatch())
        {
            return;
        }
        if (Event == "click")
        {
            ClickHandlers?.Invoke();
        }
        else if (Event == "change")
        {
            SetAttribute("value", Value);
            ChangeHandlers?.Invoke(Value);
        }
    }

    internal bool CanDispatch()
    {
        return Active && WasRendered && Enabled && Visible && !HasHiddenParent();
    }

    internal void Release()
    {
        Active = false;
        ClickHandlers = null;
        MountedLayout = null;
        MountedContext = null;
        ChangeHandlers = null;
        foreach (var Child in AddedChildren)
        {
            Child.Release();
        }
    }

    private void CheckActionConflict(params string[] Names)
    {
        if (Names.Any(Name => !string.IsNullOrEmpty(GetAttribute(Name))))
        {
            throw new InvalidOperationException("UI_INSTANCE_AUTHORED_ACTION_CONFLICT: Clear the action explicitly before binding a callback");
        }
    }

    private void Touch()
    {
        Changed = true;
        Owner?.AdvanceRevision();
        if (!Dynamic && Owner is not null && Element.Name != "template")
        {
            Element.SetAttributeValue("data-ui-instance-node", Owner.InstanceId + ":" + Token);
            if (Element.Attribute("id") is null)
            {
                Element.SetAttributeValue("id", "ui/" + Uri.EscapeDataString(Owner.InstanceId) + "/source/" + Token);
            }
        }
    }

    private bool HasHiddenParent()
    {
        for (var Ancestor = Parent; Ancestor is not null; Ancestor = Ancestor.Parent)
        {
            if (!Ancestor.Visible || !Ancestor.Enabled)
            {
                return true;
            }
        }
        return false;
    }

    private void EnsureActive()
    {
        Owner?.EnsureAlive();
        if (Owner is not null && !Active)
        {
            throw new InvalidOperationException("UI_INSTANCE_ELEMENT_REMOVED");
        }
    }
}

public sealed class UiContainer(string Id) : UiElement("div", Id);
public sealed class UiLabel(string Id, string Text) : UiElement("span", Id, Text);
public sealed class UiButton(string Id, string Text) : UiElement("button", Id, Text);
public sealed class UiTextField : UiElement
{
    public UiTextField(string Id, string Value = "") : base("input", Id)
    {
        SetAttribute("type", "text");
        SetAttribute("value", Value);
    }

    public string Value
    {
        get
        {
            return GetAttribute("value") ?? string.Empty;
        }
        set
        {
            SetAttribute("value", value);
        }
    }
}
