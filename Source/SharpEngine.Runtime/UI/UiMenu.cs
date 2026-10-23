using System.Xml.Linq;

namespace SharpEngine.UI;

public sealed record UiMenuItem(string Id, string Label, string Action,
    bool Enabled = true, bool? Checked = null, string? Tooltip = null);

public sealed record UiMenuSection(string Id, IReadOnlyList<UiMenuItem> Items);

/// <summary>Presentation-only menu content. The caller owns commands, targets and popup lifetime.</summary>
public sealed class UiMenu : IUiLayoutPart
{
    private readonly UiMenuSection[] Sections;

    public UiMenu(IEnumerable<UiMenuSection> Sections)
    {
        this.Sections = Sections.Select(Section =>
            new UiMenuSection(Section.Id, Array.AsReadOnly(Section.Items.ToArray()))).ToArray();
    }

    public string BuildLayout(UiLayoutContext Context)
    {
        var Root = new XElement("div", new XAttribute("class", "menu-content"));
        var Written = false;
        foreach (var Section in Sections.Where(Section => Section.Items.Count > 0))
        {
            if (Written)
            {
                Root.Add(new XElement("div", new XAttribute("class", "menu-divider")));
            }
            foreach (var Item in Section.Items)
            {
                var Button = new XElement("button",
                    new XAttribute("id", Item.Id),
                    new XAttribute("class", "menu-command"),
                    new XAttribute("data-action", Item.Action),
                    new XElement("span", new XAttribute("class", "menu-check"),
                        Item.Checked is null ? "" : Item.Checked.Value ? "[x]" : "[ ]"),
                    new XElement("span", Item.Label));
                if (!Item.Enabled)
                {
                    Button.SetAttributeValue("disabled", "true");
                }
                if (Item.Tooltip is not null)
                {
                    Button.SetAttributeValue("title", Item.Tooltip);
                }
                Root.Add(Button);
            }
            Written = true;
        }
        return Root.ToString(SaveOptions.DisableFormatting);
    }
}
