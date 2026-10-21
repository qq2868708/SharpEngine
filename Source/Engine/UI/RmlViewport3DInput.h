#pragma once

#include <RmlUi/Core.h>
#include <windows.h>
#include <functional>
#include <string>

// Host-local input adapter. It emits semantic actions and owns no World or asset state.
class RmlViewport3DInput
{
public:
    bool IsCapturing() const { return !ElementId.empty(); }
    const std::string& CapturedElementId() const { return ElementId; }

    void Cancel()
    {
        ElementId.clear();
        Button = -1;
    }

    bool ProcessEvent(Rml::Event& Event, HWND Window,
        const std::function<void(const std::string&)>& QueueAction)
    {
        auto* Viewport = Event.GetTargetElement();
        while (Viewport && !Viewport->HasAttribute("data-viewport3d"))
        {
            Viewport = Viewport->GetParentNode();
        }
        const auto EventId = Event.GetId();
        const int MouseX = Event.GetParameter<int>("mouse_x", 0);
        const int MouseY = Event.GetParameter<int>("mouse_y", 0);
        if (EventId == Rml::EventId::Mousedown && Viewport)
        {
            const int Pressed = Event.GetParameter<int>("button", -1);
            if (Pressed == 1 || Pressed == 2)
            {
                ElementId = Viewport->GetAttribute<Rml::String>("data-viewport3d", "");
                Button = Pressed;
                PointerX = MouseX;
                PointerY = MouseY;
                SetCapture(Window);
                Event.StopPropagation();
                return true;
            }
        }
        if (EventId == Rml::EventId::Mousemove && IsCapturing())
        {
            const int DeltaX = MouseX - PointerX;
            const int DeltaY = MouseY - PointerY;
            PointerX = MouseX;
            PointerY = MouseY;
            if (DeltaX != 0 || DeltaY != 0)
            {
                QueueAction("ui.viewport3d:" + ElementId +
                    (Button == 1 ? ":orbit:" : ":pan:") +
                    std::to_string(DeltaX) + ":" + std::to_string(DeltaY));
            }
            Event.StopPropagation();
            return true;
        }
        if (EventId == Rml::EventId::Mouseup && Event.GetParameter<int>("button", -1) == Button)
        {
            Cancel();
        }
        if (EventId == Rml::EventId::Mousescroll && Viewport)
        {
            const float Delta = -Event.GetParameter<float>("wheel_delta_y", 0);
            QueueAction("ui.viewport3d:" + Viewport->GetAttribute<Rml::String>("data-viewport3d", "") +
                ":zoom:0:" + std::to_string(Delta));
            Event.StopPropagation();
            return true;
        }
        return false;
    }

private:
    std::string ElementId;
    int Button{-1};
    int PointerX{};
    int PointerY{};
};
