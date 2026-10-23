using System.Xml.Linq;

namespace SharpEngine.UI;

public sealed record UiMenuItem(string Id, string Label, string Action,
    bool Enabled = true, bool? Checked = null, string? Tooltip = null,
    IReadOnlyList<UiMenuItem>? Children = null);

public sealed record UiMenuSection(string Id, IReadOnlyList<UiMenuItem> Items, string? Label = null);

/// <summary>Presentation-only menu content. The caller owns commands, targets and popup lifetime.</summary>
public sealed class UiMenu : IUiLayoutPart
{
    private readonly UiMenuSection[] Sections;

    public UiMenu(IEnumerable<UiMenuSection> Sections)
    {
        this.Sections = Sections.Select(Section =>
            new UiMenuSection(Section.Id, Array.AsReadOnly(Section.Items.Select(Snapshot).ToArray()), Section.Label)).ToArray();
    }

    private static UiMenuItem Snapshot(UiMenuItem Item)
    {
        return Item with
        {
            Children = Item.Children is null ? null : Array.AsReadOnly(Item.Children.Select(Snapshot).ToArray()),
        };
    }

    public string BuildLayout(UiLayoutContext Context)
    {
        var Root = new XElement("div", new XAttribute("class", "menu-content"));
        var Submenus = new List<XElement>();
        var Written = false;
        foreach (var Section in Sections.Where(Section => Section.Items.Count > 0))
        {
            if (!string.IsNullOrWhiteSpace(Section.Label))
            {
                Root.Add(new XElement("div", new XAttribute("id", "menu-category-" + Section.Id),
                    new XAttribute("class", "menu-heading"),
                    new XElement("span", Section.Label),
                    new XElement("span", new XAttribute("class", "menu-heading-line"))));
            }
            else if (Written)
            {
                Root.Add(new XElement("div", new XAttribute("class", "menu-divider")));
            }
            foreach (var Item in Section.Items)
            {
                Root.Add(BuildItem(Item, 1, Submenus));
            }
            Written = true;
        }
        Root.Add(Submenus);
        return Root.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement BuildItem(UiMenuItem Item, int Depth, List<XElement> Submenus)
    {
        var Button = new XElement("button",
            new XAttribute("id", Item.Id),
            new XAttribute("class", "menu-command"));
        var HasChildren = Item.Children is { Count: > 0 };
        if (Item.Enabled)
        {
            if (HasChildren)
            {
                Button.SetAttributeValue("data-menu-submenu", Item.Id + "-submenu");
            }
            else
            {
                Button.SetAttributeValue("data-action", Item.Action);
            }
        }
        else
        {
            Button.SetAttributeValue("disabled", "true");
            Button.SetAttributeValue("class", "menu-command disabled");
        }
        Button.Add(new XElement("span", new XAttribute("class", "menu-check"),
            Item.Checked is null ? "" : Item.Checked.Value ? "[x]" : "[ ]"),
            new XElement("span", Item.Label));
        if (Item.Tooltip is not null)
        {
            Button.SetAttributeValue("title", Item.Tooltip);
        }
        if (HasChildren)
        {
            Button.SetAttributeValue("aria-haspopup", "true");
            Button.SetAttributeValue("aria-expanded", "false");
            Button.Add(new XElement("span", new XAttribute("class", "menu-submenu-arrow"), ">"));
            var Submenu = new XElement("div", new XAttribute("id", Item.Id + "-submenu"),
                new XAttribute("class", "menu-popup context-submenu"),
                new XAttribute("data-menu-depth", Depth),
                new XAttribute("data-menu-opener", Item.Id));
            foreach (var Child in Item.Children!)
            {
                Submenu.Add(BuildItem(Child, Depth + 1, Submenus));
            }
            Submenus.Add(Submenu);
        }
        return Button;
    }
}
