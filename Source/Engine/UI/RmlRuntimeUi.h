#pragma once

#include <RmlUi/Core.h>
#include <RmlUi/Core/ElementText.h>
#include <string>
#include <vector>

// Retain runtime widget identities across the host's existing markup publication path.
class RmlRuntimeUi
{
public:
    struct Retained
    {
        std::vector<Rml::ElementPtr> Roots;
        Rml::String FocusId;
    };

    static Retained Capture(Rml::Element& Root, Rml::Context& Context)
    {
        Retained Result;
        auto* Focus = Context.GetFocusElement();
        for (auto* Element = Focus; Element && Element != &Root; Element = Element->GetParentNode())
        {
            if (Element->HasAttribute("data-ui-instance-node"))
            {
                Result.FocusId = Focus->GetId();
                break;
            }
        }
        CaptureChildren(Root, Result.Roots);
        return Result;
    }

    static void Restore(Rml::Element& Root, Retained& State)
    {
        for (auto& Previous : State.Roots)
        {
            const auto Key = Previous->GetAttribute<Rml::String>("data-ui-instance-node", "");
            auto* Placeholder = Find(Root, Key);
            if (!Placeholder || Placeholder->GetTagName() != Previous->GetTagName())
            {
                continue;
            }
            Patch(*Previous, *Placeholder);
            auto* Parent = Placeholder->GetParentNode();
            Parent->ReplaceChild(std::move(Previous), Placeholder);
        }
        if (!State.FocusId.empty())
        {
            auto* Focus = Root.GetElementById(State.FocusId);
            if (Focus)
            {
                Focus->Focus();
            }
        }
    }

private:
    static void CaptureChildren(Rml::Element& Parent, std::vector<Rml::ElementPtr>& Roots)
    {
        for (int Index = 0; Index < Parent.GetNumChildren();)
        {
            auto* Child = Parent.GetChild(Index);
            if (Child->HasAttribute("data-ui-instance-node"))
            {
                Roots.push_back(Parent.RemoveChild(Child));
            }
            else
            {
                CaptureChildren(*Child, Roots);
                ++Index;
            }
        }
    }

    static Rml::Element* Find(Rml::Element& Root, const Rml::String& Key)
    {
        if (Root.GetAttribute<Rml::String>("data-ui-instance-node", "") == Key)
        {
            return &Root;
        }
        for (int Index = 0; Index < Root.GetNumChildren(); ++Index)
        {
            auto* Found = Find(*Root.GetChild(Index), Key);
            if (Found)
            {
                return Found;
            }
        }
        return nullptr;
    }

    static bool SameIdentity(Rml::Element& Previous, Rml::Element& Desired)
    {
        return Previous.GetTagName() == Desired.GetTagName() && Previous.GetId() == Desired.GetId() &&
            Previous.GetAttribute<Rml::String>("data-ui-instance-node", "") ==
                Desired.GetAttribute<Rml::String>("data-ui-instance-node", "");
    }

    static void Patch(Rml::Element& Previous, Rml::Element& Desired)
    {
        if (Previous.GetTagName() == "#text")
        {
            auto& Text = static_cast<Rml::ElementText&>(Previous);
            const auto& NewText = static_cast<Rml::ElementText&>(Desired);
            if (Text.GetText() != NewText.GetText())
            {
                Text.SetText(NewText.GetText());
            }
            return;
        }
        std::vector<Rml::String> Remove;
        for (const auto& Attribute : Previous.GetAttributes())
        {
            if (!Desired.HasAttribute(Attribute.first))
            {
                Remove.push_back(Attribute.first);
            }
        }
        for (const auto& Name : Remove)
        {
            Previous.RemoveAttribute(Name);
        }
        for (const auto& Attribute : Desired.GetAttributes())
        {
            const auto Value = Attribute.second.Get<Rml::String>();
            if (!Previous.HasAttribute(Attribute.first) || Previous.GetAttribute<Rml::String>(Attribute.first, "") != Value)
            {
                Previous.SetAttribute(Attribute.first, Value);
            }
        }
        for (int Index = 0; Index < Desired.GetNumChildren(); ++Index)
        {
            auto* Next = Desired.GetChild(Index);
            Rml::Element* Match = nullptr;
            for (int Candidate = Index; Candidate < Previous.GetNumChildren(); ++Candidate)
            {
                auto* Child = Previous.GetChild(Candidate);
                if (SameIdentity(*Child, *Next))
                {
                    Match = Child;
                    break;
                }
            }
            if (!Match)
            {
                auto Copy = Next->Clone();
                if (Index < Previous.GetNumChildren())
                {
                    Previous.InsertBefore(std::move(Copy), Previous.GetChild(Index));
                }
                else
                {
                    Previous.AppendChild(std::move(Copy));
                }
            }
            else
            {
                if (Match != Previous.GetChild(Index))
                {
                    auto Child = Previous.RemoveChild(Match);
                    Previous.InsertBefore(std::move(Child), Previous.GetChild(Index));
                }
                Patch(*Match, *Next);
            }
        }
        while (Previous.GetNumChildren() > Desired.GetNumChildren())
        {
            Previous.RemoveChild(Previous.GetChild(Desired.GetNumChildren()));
        }
    }
};
