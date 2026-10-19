#pragma once
#include <RmlUi/Core.h>
#include <algorithm>
#include <cmath>
#include <iomanip>
#include <locale>
#include <sstream>
#include <string>

// Draft values live on observable DOM attributes; no component state changes
// until Apply emits the inspector's existing revision-checked field command.
class RmlColorPicker
{
    std::string DragPicker;
    std::string DragChannel;
    std::string ScrollPicker;

    static Rml::Element* Ancestor(Rml::Element* Element, const char* Attribute)
    {
        while (Element && !Element->HasAttribute(Attribute))
        {
            Element = Element->GetParentNode();
        }
        return Element;
    }

    static Rml::Element* Part(Rml::Element* Picker, const char* Suffix)
    {
        return Picker->GetElementById(Picker->GetId() + Suffix);
    }

    static void Update(Rml::Element* Picker, bool Changed)
    {
        const float H = Picker->GetAttribute<float>("data-color-h", 0);
        const float S = Picker->GetAttribute<float>("data-color-s", 0);
        const float V = Picker->GetAttribute<float>("data-color-v", 0);
        const float A = Picker->GetAttribute<float>("data-color-a", 1);
        const auto Channel = [H](float Offset)
        {
            const float K = std::fmod(Offset + H * 6, 6.0f);
            return 1 - std::clamp((std::min)(K, 4 - K), 0.0f, 1.0f);
        };
        const float HR = Channel(5), HG = Channel(3), HB = Channel(1);
        const float R = V * (1 - S + S * HR);
        const float G = V * (1 - S + S * HG);
        const float B = V * (1 - S + S * HB);
        const auto Rgb = [](float R, float G, float B)
        {
            return "rgb(" + std::to_string(static_cast<int>(std::round(R * 255))) + "," +
                std::to_string(static_cast<int>(std::round(G * 255))) + "," +
                std::to_string(static_cast<int>(std::round(B * 255))) + ")";
        };
        Part(Picker, "-sv")->SetProperty("background-color", Rgb(HR, HG, HB));
        Part(Picker, "-marker")->SetProperty("left", std::to_string(S * 100) + "%");
        Part(Picker, "-marker")->SetProperty("top", std::to_string((1 - V) * 100) + "%");
        Part(Picker, "-hue-marker")->SetProperty("left", std::to_string(H * 100) + "%");
        Part(Picker, "-preview")->SetProperty("border-left-color", Rgb(R, G, B));
        std::ostringstream Label;
        Label.imbue(std::locale::classic());
        Label << '#' << std::hex << std::uppercase << std::setfill('0')
              << std::setw(2) << static_cast<int>(std::round(R * 255))
              << std::setw(2) << static_cast<int>(std::round(G * 255))
              << std::setw(2) << static_cast<int>(std::round(B * 255))
              << std::dec << " / " << static_cast<int>(std::round(A * 100)) << '%';
        Part(Picker, "-preview")->SetInnerRML(Label.str());
        if (Changed)
        {
            std::ostringstream Json;
            Json.imbue(std::locale::classic());
            Json << std::setprecision(9) << '[' << R << ',' << G << ',' << B << ',' << A << ']';
            Picker->SetAttribute("data-color-value", Json.str());
        }
    }

    static void Initialize(Rml::Element* Picker)
    {
        const float R = std::clamp(Picker->GetAttribute<float>("data-color-r", 0), 0.0f, 1.0f);
        const float G = std::clamp(Picker->GetAttribute<float>("data-color-g", 0), 0.0f, 1.0f);
        const float B = std::clamp(Picker->GetAttribute<float>("data-color-b", 0), 0.0f, 1.0f);
        const float V = (std::max)({R, G, B});
        const float Delta = V - (std::min)({R, G, B});
        float H = 0;
        if (Delta > 0)
        {
            H = V == R ? (G - B) / Delta : V == G ? 2 + (B - R) / Delta : 4 + (R - G) / Delta;
            H = std::fmod(H / 6 + 1, 1.0f);
        }
        Picker->SetAttribute("data-color-h", H);
        Picker->SetAttribute("data-color-s", V > 0 ? Delta / V : 0);
        Picker->SetAttribute("data-color-v", V);
        Update(Picker, false);
    }

    void Move(Rml::Element* Picker, Rml::Event& Event)
    {
        auto* Control = Part(Picker, DragChannel == "sv" ? "-sv" : "-hue");
        const auto Origin = Control->GetAbsoluteOffset();
        const auto Size = Control->GetBox().GetSize(Rml::BoxArea::Content);
        const float X = std::clamp((Event.GetParameter<float>("mouse_x", 0) - Origin.x) / (std::max)(1.0f, Size.x), 0.0f, 1.0f);
        if (DragChannel == "sv")
        {
            const float Y = std::clamp((Event.GetParameter<float>("mouse_y", 0) - Origin.y) / (std::max)(1.0f, Size.y), 0.0f, 1.0f);
            Picker->SetAttribute("data-color-s", X);
            Picker->SetAttribute("data-color-v", 1 - Y);
        }
        else
        {
            Picker->SetAttribute("data-color-h", X);
        }
        Update(Picker, true);
    }

public:
    void AfterLayout(Rml::ElementDocument* Document)
    {
        if (!ScrollPicker.empty())
        {
            auto* Picker = Document->GetElementById(ScrollPicker);
            if (Picker)
            {
                Part(Picker, "-panel")->ScrollIntoView(Rml::ScrollIntoViewOptions(Rml::ScrollAlignment::Nearest));
            }
            ScrollPicker.clear();
        }
    }

    bool ProcessEvent(Rml::Event& Event, Rml::ElementDocument* Document, std::string& Command)
    {
        auto* Target = Event.GetTargetElement();
        auto* Picker = Ancestor(Target, "data-color-picker");
        const auto Id = Event.GetId();
        if (!DragPicker.empty() && (Id == Rml::EventId::Mousemove || Id == Rml::EventId::Mouseup))
        {
            auto* Active = Document->GetElementById(DragPicker);
            if (Active)
            {
                Move(Active, Event);
            }
            if (Id == Rml::EventId::Mouseup || !Active)
            {
                DragPicker.clear();
            }
            return true;
        }
        if (!Picker)
        {
            return false;
        }
        if (Id == Rml::EventId::Keydown && Event.GetParameter<int>("key_identifier", 0) == Rml::Input::KI_ESCAPE)
        {
            Command = "editor.component-cancel";
            Event.StopPropagation();
            return true;
        }
        auto* Control = Ancestor(Target, "data-color-control");
        const auto Channel = Control ? Control->GetAttribute<Rml::String>("data-color-control", "") : "";
        if (Control && Id == Rml::EventId::Mousedown && Event.GetParameter<int>("button", -1) == 0 && Channel != "alpha")
        {
            DragPicker = Picker->GetId();
            DragChannel = Channel;
            Move(Picker, Event);
            Event.StopPropagation();
            return true;
        }
        if (Control && Id == Rml::EventId::Change && Channel == "alpha")
        {
            Picker->SetAttribute("data-color-a", std::clamp(Control->GetAttribute<float>("value", 1), 0.0f, 1.0f));
            Update(Picker, true);
            return true;
        }
        auto* Button = Ancestor(Target, "data-color-command");
        if (Button && Id == Rml::EventId::Click)
        {
            const auto Action = Button->GetAttribute<Rml::String>("data-color-command", "");
            if (Action == "open")
            {
                if (Picker->GetAttribute<bool>("data-color-open", false))
                {
                    Command = "editor.component-cancel";
                }
                else
                {
                    Initialize(Picker);
                    Picker->SetAttribute("data-color-open", true);
                    Part(Picker, "-panel")->SetProperty("display", "block");
                    ScrollPicker = Picker->GetId();
                    Command = "editor.component-focus";
                }
            }
            else if (Action == "apply")
            {
                Command = Picker->GetAttribute<Rml::String>("data-color-prefix", "") + Picker->GetAttribute<Rml::String>("data-color-value", "");
            }
            else
            {
                Command = "editor.component-cancel";
            }
            return true;
        }
        return false;
    }
};
