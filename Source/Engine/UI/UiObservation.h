#pragma once

#include <RmlUi/Core.h>
#include <RmlUi/Core/ElementText.h>
#include <RmlUi/Core/ElementUtilities.h>
#include <algorithm>
#include <iomanip>
#include <sstream>
#include <string_view>

// Derived, transient RmlUi observations. IDs never contain native handles.
namespace UiObservation
{
    inline std::string Quote(std::string_view value)
    {
        std::ostringstream result;
        result << '"';
        for (const unsigned char character : value)
        {
            if (character == '"' || character == '\\')
            {
                result << '\\' << character;
            }
            else if (character < 0x20)
            {
                result << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(character) << std::dec;
            }
            else
            {
                result << character;
            }
        }
        result << '"';
        return result.str();
    }

    inline std::string Identity(Rml::Element* element)
    {
        for (; element; element = element->GetParentNode())
        {
            if (!element->GetId().empty())
            {
                return "game/" + element->GetId();
            }
        }
        return {};
    }

    inline bool HasAncestor(Rml::Element* element, Rml::Element* ancestor)
    {
        for (; element; element = element->GetParentNode())
        {
            if (element == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    inline std::string Rect(float x, float y, float width, float height)
    {
        std::ostringstream result;
        result << "{\"x\":" << x << ",\"y\":" << y << ",\"width\":" << (std::max)(0.0f, width)
               << ",\"height\":" << (std::max)(0.0f, height) << '}';
        return result.str();
    }

    inline void AppendText(Rml::Element* element, std::string& text)
    {
        if (text.size() >= 4096)
        {
            return;
        }
        if (const auto* leaf = dynamic_cast<Rml::ElementText*>(element))
        {
            text += leaf->GetText();
            text.resize((std::min)(text.size(), size_t(4096)));
            return;
        }
        for (int index = 0; index < element->GetNumChildren(); ++index)
        {
            AppendText(element->GetChild(index), text);
        }
    }

    inline void AppendElements(std::ostream& result, Rml::Context& context, Rml::Element* element, std::string parentId,
                               bool& first)
    {
        auto nextParent = parentId;
        if (!element->GetId().empty())
        {
            const auto id = Identity(element);
            nextParent = id;
            const auto position = element->GetAbsoluteOffset(Rml::BoxArea::Border);
            const auto size = element->GetBox().GetSize(Rml::BoxArea::Border);
            const auto dimensions = context.GetDimensions();
            Rml::Rectanglei clip;
            const bool clipped = Rml::ElementUtilities::GetClippingRegion(element, clip);
            const float left = clipped ? (std::max)(0, clip.Left()) : 0;
            const float top = clipped ? (std::max)(0, clip.Top()) : 0;
            const float right = clipped ? (std::min)(dimensions.x, clip.Right()) : dimensions.x;
            const float bottom = clipped ? (std::min)(dimensions.y, clip.Bottom()) : dimensions.y;
            const bool visible = element->IsVisible(true) && size.x > 0 && size.y > 0 && position.x + size.x > left &&
                                 position.y + size.y > top && position.x < right && position.y < bottom;
            const bool enabled = !element->HasAttribute("disabled");
            const bool interactive =
                element->GetTagName() == "button" || element->GetTagName() == "input" || element->GetTagName() == "select";
            const bool hit =
                visible && enabled && interactive && HasAncestor(context.GetElementAtPoint(position + size * 0.5f), element);
            std::string text;
            AppendText(element, text);
            if (!first)
            {
                result << ',';
            }
            first = false;
            result << "{\"elementId\":" << Quote(id) << ",\"semanticId\":" << Quote(element->GetId())
                   << ",\"parentId\":" << (parentId.empty() ? "null" : Quote(parentId))
                   << ",\"role\":" << Quote(interactive ? element->GetTagName() : "text")
                   << ",\"bounds\":" << Rect(position.x, position.y, size.x, size.y)
                   << ",\"clip\":" << Rect(left, top, right - left, bottom - top)
                   << ",\"visible\":" << (visible ? "true" : "false") << ",\"enabled\":" << (enabled ? "true" : "false")
                   << ",\"focusable\":" << (element->GetComputedValues().focus() != Rml::Style::Focus::None ? "true" : "false")
                   << ",\"focused\":" << (context.GetFocusElement() == element ? "true" : "false")
                   << ",\"hovered\":" << (HasAncestor(context.GetHoverElement(), element) ? "true" : "false")
                   << ",\"zOrder\":" << element->GetZIndex() << ",\"hitTarget\":" << (hit ? "true" : "false")
                   << ",\"text\":" << Quote(text) << '}';
        }
        for (int index = 0; index < element->GetNumChildren(); ++index)
        {
            AppendElements(result, context, element->GetChild(index), nextParent, first);
        }
    }

    inline std::string DescribeHost(Rml::Context& context, Rml::Element* document)
    {
        const auto dimensions = context.GetDimensions();
        std::ostringstream result;
        result << "{\"hostId\":\"game\",\"kind\":\"runtime\",\"visible\":true,\"bounds\":"
               << Rect(0, 0, float(dimensions.x), float(dimensions.y))
               << ",\"contentScale\":" << Rml::ElementUtilities::GetDensityIndependentPixelRatio(document)
               << ",\"focusedElementId\":" << Quote(Identity(context.GetFocusElement()))
               << ",\"hoveredElementId\":" << Quote(Identity(context.GetHoverElement())) << ",\"elements\":[";
        bool first = true;
        AppendElements(result, context, document, {}, first);
        result << "]}";
        return result.str();
    }
} // namespace UiObservation
