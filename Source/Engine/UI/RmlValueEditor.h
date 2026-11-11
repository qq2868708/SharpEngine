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
        const bool PrimaryRelease = Event.GetId() == Rml::EventId::Mouseup &&
            Event.GetParameter<int>("button", -1) == 0;
        if (PrimaryRelease && Target && Target->GetOwnerDocument())
        {
            auto* Dragging = Target->GetOwnerDocument()->QuerySelector("[data-range-dragging]");
            if (Dragging)
            {
                Range = Dragging;
            }
        }
        if (Range)
        {
            return ProcessRange(Event, Range, PointerDown, Emit);
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
    static void PreviewRange(Rml::Element* Range, const Rml::String& Value)
    {
        Range->SetAttribute("data-range-value", Value);
        const auto InputId = Range->GetAttribute<Rml::String>("data-value-input", "");
        auto* Document = Range->GetOwnerDocument();
        auto* Input = Document && !InputId.empty() ? Document->GetElementById(InputId) : nullptr;
        if (Input && Input->HasAttribute("data-value-editor") && !Input->HasAttribute("disabled"))
        {
            Input->SetAttribute("value", Value);
        }
    }

    static bool ProcessRange(Rml::Event& Event, Rml::Element* Target,
        bool PointerDown, const std::function<void(const Rml::String&)>& Emit)
    {
        if (Target->HasAttribute("disabled"))
        {
            return true;
        }
        const auto Id = Event.GetId();
        const auto Value = Target->GetAttribute<Rml::String>("value", "");
        const bool PrimaryDown = Id == Rml::EventId::Mousedown &&
            Event.GetParameter<int>("button", -1) == 0;
        if ((Id == Rml::EventId::Focus || PrimaryDown || Id == Rml::EventId::Change) &&
            !Target->HasAttribute("data-range-editing"))
        {
            Target->SetAttribute("data-range-start", Value);
            Target->SetAttribute("data-range-value", Value);
            Target->SetAttribute("data-range-editing", true);
            Emit("ui.value-focus");
        }
        if (PrimaryDown)
        {
            Target->SetAttribute("data-range-dragging", true);
        }
        if (Id == Rml::EventId::Change)
        {
            // RmlUi dispatches Change before it updates the input's value attribute.
            if (!Target->HasAttribute("data-range-cancelled"))
            {
                PreviewRange(Target, Event.GetParameter<Rml::String>("value", Value));
            }
            return true;
        }
        const bool Cancel = Id == Rml::EventId::Keydown &&
            Event.GetParameter<int>("key_identifier", 0) == Rml::Input::KI_ESCAPE;
        const auto Key = Event.GetParameter<int>("key_identifier", 0);
        const bool SubmitKey = Id == Rml::EventId::Keyup &&
            (Key == Rml::Input::KI_LEFT || Key == Rml::Input::KI_RIGHT ||
             Key == Rml::Input::KI_UP || Key == Rml::Input::KI_DOWN || Key == Rml::Input::KI_RETURN);
        const bool Commit = Id == Rml::EventId::Blur || SubmitKey ||
            (Id == Rml::EventId::Mouseup && Event.GetParameter<int>("button", -1) == 0);
        if ((Cancel || Commit) && Target->HasAttribute("data-range-editing"))
        {
            const auto Start = Target->GetAttribute<Rml::String>("data-range-start", Value);
            const bool Cancelled = Cancel || Target->HasAttribute("data-range-cancelled");
            const auto Draft = Cancelled ? Start : Target->GetAttribute<Rml::String>("data-range-value", Value);
            if (Cancelled)
            {
                Target->SetAttribute("data-range-cancelled", true);
                Target->SetAttribute("value", Start);
            }
            PreviewRange(Target, Draft);
            if (!Cancelled && Draft != Start)
            {
                Emit(Target->GetAttribute<Rml::String>("data-value-commit", "") + Draft);
            }
            if (Cancel)
            {
                Event.StopPropagation();
            }
            // Keep a cancelled pointer gesture suppressed until its release.
            if (!Cancel || !Target->HasAttribute("data-range-dragging"))
            {
                Target->RemoveAttribute("data-range-dragging");
                Target->RemoveAttribute("data-range-editing");
                Target->RemoveAttribute("data-range-cancelled");
            }
            Emit(PointerDown && Id != Rml::EventId::Mouseup ? "ui.value-end-pointer" : "ui.value-end");
        }
        return Id == Rml::EventId::Change || Cancel || Commit;
    }
};
