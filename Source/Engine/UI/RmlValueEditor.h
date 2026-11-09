#pragma once

#include <RmlUi/Core.h>
#include <cmath>
#include <cstdlib>
#include <functional>

// Native input policy shared by Editor and Game. It emits UI events and caller-supplied
// commit actions; domain validation, transactions and state ownership remain above it.
class RmlValueEditor
{
public:
    static bool ProcessEvent(Rml::Event& Event, bool PointerDown, int Modifiers,
        const std::function<void(const Rml::String&)>& Emit)
    {
        auto* Target = Event.GetTargetElement();
        auto* Range = Target;
        while (Range && !Range->HasAttribute("data-value-range"))
        {
            Range = Range->GetParentNode();
        }
        if (Range)
        {
            return ProcessRange(Event, Range, Emit);
        }
        if (!Target || !Target->HasAttribute("data-value-editor"))
        {
            return false;
        }
        if (Target->HasAttribute("disabled"))
        {
            return true;
        }
        const auto EventId = Event.GetId();
        const auto Key = Event.GetParameter<int>("key_identifier", 0);
        if (EventId == Rml::EventId::Focus)
        {
            Target->SetAttribute("data-value-start", Target->GetAttribute<Rml::String>("value", ""));
            Emit("ui.value-focus");
        }
        const bool Cancel = EventId == Rml::EventId::Keydown && Key == Rml::Input::KI_ESCAPE;
        const bool SubmitKey = Key == Rml::Input::KI_RETURN && (Target->GetTagName() != "textarea" ||
            (Modifiers & Rml::Input::KM_CTRL) != 0 || Event.GetParameter<bool>("ctrl_key", false));
        const bool Commit = EventId == Rml::EventId::Blur || (EventId == Rml::EventId::Keydown && SubmitKey);
        if (Cancel || Commit)
        {
            const auto Start = Target->GetAttribute<Rml::String>("data-value-start", Target->GetAttribute<Rml::String>("data-original-value", ""));
            const auto Value = Target->GetAttribute<Rml::String>("value", "");
            if (Cancel)
            {
                Target->SetAttribute("value", Start);
            }
            else if (Value != Start)
            {
                const auto Kind = Target->GetAttribute<Rml::String>("data-value-editor", "text");
                char* End = nullptr;
                const double Number = std::strtod(Value.c_str(), &End);
                const bool Numeric = Kind == "number" || Kind == "integer";
                const bool Valid = !Numeric || (!Value.empty() && End == Value.c_str() + Value.size() &&
                    std::isfinite(Number) && (Kind != "integer" || std::trunc(Number) == Number));
                if (Valid)
                {
                    Emit(Target->GetAttribute<Rml::String>("data-value-commit", "") + Value);
                    Target->SetAttribute("data-value-start", Value);
                }
                else
                {
                    Target->SetAttribute("value", Start);
                    Emit("ui.value-invalid");
                }
            }
            Emit(EventId == Rml::EventId::Blur && PointerDown ? "ui.value-end-pointer" : "ui.value-end");
            if (EventId == Rml::EventId::Keydown)
            {
                Event.StopPropagation();
            }
            return true;
        }
        return EventId == Rml::EventId::Change;
    }

private:
    static bool ProcessRange(Rml::Event& Event, Rml::Element* Target,
        const std::function<void(const Rml::String&)>& Emit)
    {
        if (Target->HasAttribute("disabled"))
        {
            return true;
        }
        const auto Id = Event.GetId();
        const auto Value = Target->GetAttribute<Rml::String>("value", "");
        if (Id == Rml::EventId::Focus || Id == Rml::EventId::Mousedown)
        {
            Target->SetAttribute("data-range-start", Value);
            if (Id == Rml::EventId::Mousedown)
            {
                Target->SetAttribute("data-range-dragging", true);
            }
            Emit("ui.value-focus");
        }
        const bool Cancel = Id == Rml::EventId::Keydown &&
            Event.GetParameter<int>("key_identifier", 0) == Rml::Input::KI_ESCAPE;
        const bool Commit = Id == Rml::EventId::Blur || Id == Rml::EventId::Keyup ||
            (Id == Rml::EventId::Mouseup && Event.GetParameter<int>("button", -1) == 0);
        if (Cancel || Commit)
        {
            const auto Start = Target->GetAttribute<Rml::String>("data-range-start", Value);
            if (Cancel)
            {
                Target->SetAttribute("value", Start);
                Event.StopPropagation();
            }
            else if (Value != Start)
            {
                Emit(Target->GetAttribute<Rml::String>("data-value-commit", "") + Value);
                Target->SetAttribute("data-range-start", Value);
            }
            Target->RemoveAttribute("data-range-dragging");
            Emit("ui.value-end");
        }
        return Id == Rml::EventId::Change || Cancel || Commit;
    }
};
