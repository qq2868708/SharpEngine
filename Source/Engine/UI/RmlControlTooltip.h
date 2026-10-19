#pragma once

#include <RmlUi/Core.h>
#include <algorithm>
#include <cmath>
#include <string>
#include <utility>

// Transient host-local presentation. Keep tooltips outside button and pane
// layout so icon widths and scroll clipping cannot constrain their text.
class RmlControlTooltip
{
public:
    static void Update(Rml::Context& Context, Rml::ElementDocument& Document)
    {
        auto* Tooltip = Document.GetElementById("editor-control-tooltip");
        auto* Anchor = Context.GetHoverElement();
        Rml::String Title;
        for (; Anchor; Anchor = Anchor->GetParentNode())
        {
            Title = Anchor->GetAttribute<Rml::String>("title", "");
            if (!Title.empty())
            {
                break;
            }
        }
        if (!Anchor)
        {
            if (Tooltip && Tooltip->GetAttribute<bool>("data-tooltip-open", false))
            {
                Tooltip->SetAttribute("data-tooltip-open", false);
                Tooltip->SetProperty("display", "none");
                Context.Update();
            }
            return;
        }

        bool LayoutChanged = false;
        if (!Tooltip)
        {
            auto Overlay = Document.CreateElement("span");
            Overlay->SetId("editor-control-tooltip");
            auto Text = Document.CreateElement("span");
            Text->SetId("editor-control-tooltip-text");
            Overlay->AppendChild(std::move(Text));
            Tooltip = Document.AppendChild(std::move(Overlay));
            LayoutChanged = true;
        }
        if (Tooltip->GetAttribute<Rml::String>("data-tooltip-title", "") != Title)
        {
            auto* Text = Tooltip->GetElementById("editor-control-tooltip-text");
            Text->SetInnerRML(Rml::StringUtilities::EncodeRml(Title));
            Tooltip->SetAttribute("data-tooltip-title", Title);
            LayoutChanged = true;
        }
        const auto Dimensions = Context.GetDimensions();
        constexpr int Edge = 8;
        const int MaxWidth = (std::max)(1, (std::min)(320, Dimensions.x - 2 * Edge));
        if (Tooltip->GetAttribute<int>("data-tooltip-max-width", 0) != MaxWidth)
        {
            Tooltip->SetProperty("max-width", std::to_string(MaxWidth) + "px");
            Tooltip->SetAttribute("data-tooltip-max-width", MaxWidth);
            LayoutChanged = true;
        }
        if (!Tooltip->GetAttribute<bool>("data-tooltip-open", false))
        {
            Tooltip->SetProperty("display", "block");
            Tooltip->SetAttribute("data-tooltip-open", true);
            LayoutChanged = true;
        }
        if (LayoutChanged)
        {
            Context.Update();
        }

        const auto Position = Anchor->GetAbsoluteOffset(Rml::BoxArea::Border);
        const auto AnchorSize = Anchor->GetBox().GetSize(Rml::BoxArea::Border);
        const auto Size = Tooltip->GetBox().GetSize(Rml::BoxArea::Border);
        const float MaxX = (std::max)(float(Edge), Dimensions.x - Edge - Size.x);
        const float MaxY = (std::max)(float(Edge), Dimensions.y - Edge - Size.y);
        const float X = std::clamp(Position.x, float(Edge), MaxX);
        float Y = Position.y + AnchorSize.y + 6;
        if (Y > MaxY)
        {
            Y = Position.y - Size.y - 6;
        }
        Y = std::clamp(Y, float(Edge), MaxY);
        Tooltip->SetOffset({std::round(X), std::round(Y)}, nullptr, true);
    }
};
