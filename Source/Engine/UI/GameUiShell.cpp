#include "GameUiShell.h"
#include "RmlUiSourceUrl.h"
#include "UiObservation.h"
#include "../RenderCore/Private/RenderBackend.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <deque>
#include <sstream>
#include <vector>
#include "../Logging/EngineLog.h"
#include <RmlUi/Core.h>
#include <RmlUi_Platform_Win32.h>
#include <cstdio>
#include <string>

namespace
{
    class GameFileInterface final : public Rml::FileInterface
    {
    public:
        explicit GameFileInterface(std::filesystem::path root) : Root(std::move(root)) {}

        Rml::FileHandle Open(const Rml::String& path) override
        {
            std::filesystem::path requested = std::filesystem::u8path(path);
            if (requested.is_relative())
            {
                requested = Root / requested;
            }
            return reinterpret_cast<Rml::FileHandle>(_wfopen(requested.c_str(), L"rb"));
        }

        void Close(Rml::FileHandle file) override
        {
            if (file)
            {
                fclose(reinterpret_cast<FILE*>(file));
            }
        }

        size_t Read(void* buffer, size_t size, Rml::FileHandle file) override
        {
            return file ? fread(buffer, 1, size, reinterpret_cast<FILE*>(file)) : 0;
        }

        bool Seek(Rml::FileHandle file, long offset, int origin) override
        {
            return file && fseek(reinterpret_cast<FILE*>(file), offset, origin) == 0;
        }

        size_t Tell(Rml::FileHandle file) override
        {
            return file ? static_cast<size_t>(ftell(reinterpret_cast<FILE*>(file))) : 0;
        }

    private:
        std::filesystem::path Root;
    };

    class GameUiEvents final : public Rml::EventListener
    {
    public:
        explicit GameUiEvents(Rml::ElementDocument** document,
                              std::vector<std::string>* pending)
            : Document(document), Pending(pending) {}

        void ProcessEvent(Rml::Event& event) override
        {
            if (event.GetId() != Rml::EventId::Click || !Document || !*Document)
            {
                return;
            }

            // Resolve the semantic action from the nearest ancestor exactly like the
            // editor shell does, so nested markup inside a composed part works.
            for (auto* element = event.GetTargetElement(); element;
                 element = element->GetParentNode())
            {
                auto action = element->GetAttribute<Rml::String>("action", "");
                if (action.empty())
                {
                    action = element->GetAttribute<Rml::String>("data-action", "");
                }
                if (!action.empty())
                {
                    Pending->push_back(action.c_str());
                    return;
                }
            }
        }

    private:
        Rml::ElementDocument** Document = nullptr;
        std::vector<std::string>* Pending = nullptr;
    };

    // RmlUi's font-family default is empty, and an unresolved family produces
    // zero-width text. A project screen therefore needs a host default the way a
    // browser needs a user-agent stylesheet. The block is inserted BEFORE the
    // document's own styles, so any property the project declares wins by order.
    constexpr const char* UserAgentStyle =
        "<style>body { font-family: Segoe UI; }</style>";

    std::string InjectUserAgentStyle(std::string_view rml)
    {
        const std::string text(rml);
        const auto head = text.find("<head");
        if (head != std::string::npos)
        {
            const auto end = text.find('>', head);
            if (end != std::string::npos)
            {
                std::string result = text;
                result.insert(end + 1, UserAgentStyle);
                return result;
            }
        }
        const auto body = text.find("<body");
        if (body != std::string::npos)
        {
            std::string result = text;
            result.insert(body, std::string("<head>") + UserAgentStyle + "</head>");
            return result;
        }
        return text;
    }

} // namespace

struct GameUiShell::Impl
{
    explicit Impl(const std::filesystem::path& root)
        : Files(root), Events(&Document, &PendingActions) {}
    SystemInterface_Win32 System;
    TextInputMethodEditor_Win32 Ime;
    GameFileInterface Files;
    std::vector<std::string> PendingActions;
    GameUiEvents Events;
    std::function<void(const char*, const char*)> ActionHandler;
    Rml::Context* Context = nullptr;
    Rml::ElementDocument* Document = nullptr;
    HWND Window = nullptr;
    bool Ready = false;
    struct Frame
    {
        uint64_t Id = 0;
        uint64_t Revision = 0;
        uint32_t Width = 0;
        uint32_t Height = 0;
        std::vector<unsigned char> Pixels;
    };
    uint64_t FrameId = 0;
    uint64_t StateRevision = 0;
    std::string Semantic;
    std::deque<Frame> PinnedFrames;
    std::function<bool()> Present;
    uint32_t Buttons = 0;
    int InjectedModifiers = -1;

    const Frame* FindFrame(uint64_t id) const
    {
        for (const auto& frame : PinnedFrames)
        {
            if (frame.Id == id)
            {
                return &frame;
            }
        }
        return nullptr;
    }

    bool PinFrame()
    {
        if (FindFrame(FrameId))
        {
            return true;
        }
        Frame frame;
        frame.Id = FrameId;
        frame.Revision = StateRevision;
        auto* backend = GetActiveRenderBackend();
        if (!FrameId || !backend || !backend->ReadPixels(frame.Pixels, frame.Width, frame.Height))
        {
            return false;
        }
        PinnedFrames.push_back(std::move(frame));
        while (PinnedFrames.size() > 8)
        {
            PinnedFrames.pop_front();
        }
        return true;
    }

    std::string Failure(const char* code, const char* field, const char* expected, std::string_view actual) const
    {
        return "{\"accepted\":false,\"frameId\":" + std::to_string(FrameId) +
               ",\"beforeStateRevision\":" + std::to_string(StateRevision) +
               ",\"afterStateRevision\":" + std::to_string(StateRevision) +
               ",\"diagnostics\":[{\"code\":" + UiObservation::Quote(code) +
               ",\"severity\":\"error\",\"objectId\":\"game\",\"fieldPath\":" + UiObservation::Quote(field) +
               ",\"expected\":" + UiObservation::Quote(expected) + ",\"actual\":" + UiObservation::Quote(actual) +
               ",\"stateRevision\":" + std::to_string(StateRevision) + ",\"retryable\":true}]}";
    }

    std::string InputResult(uint64_t before, Rml::Element* target, std::string_view focusBefore)
    {
        const auto targetId = UiObservation::Identity(target);
        std::ostringstream path;
        path << '[';
        bool first = true;
        for (auto* element = target; element; element = element->GetParentNode())
        {
            if (!element->GetId().empty())
            {
                if (!first)
                {
                    path << ',';
                }
                first = false;
                path << UiObservation::Quote(UiObservation::Identity(element));
            }
        }
        path << ']';
        if (!Present || !Present() || !PinFrame())
        {
            return Failure("UI_RENDER_UNAVAILABLE", "frameId", "a completed presentation", "unavailable");
        }
        return "{\"accepted\":true,\"frameId\":" + std::to_string(FrameId) +
               ",\"beforeStateRevision\":" + std::to_string(before) + ",\"afterStateRevision\":" + std::to_string(StateRevision) +
               ",\"targetElementId\":" + UiObservation::Quote(targetId) + ",\"eventPath\":" + path.str() +
               ",\"focusBefore\":" + UiObservation::Quote(focusBefore) +
               ",\"focusAfter\":" + UiObservation::Quote(UiObservation::Identity(Context->GetFocusElement())) +
               ",\"diagnostics\":[]}";
    }
};

GameUiShell::GameUiShell() = default;
GameUiShell::~GameUiShell() { Shutdown(); }

bool GameUiShell::Initialize(HWND window, const std::filesystem::path& root, Rml::RenderInterface* renderer)
{
    if (State || !window || !renderer)
    {
        return false;
    }
    State = std::make_unique<Impl>(root);
    auto& state = *State;
    state.Window = window;
    state.System.SetWindow(window);
    Rml::SetSystemInterface(&state.System);
    Rml::SetFileInterface(&state.Files);
    Rml::SetRenderInterface(renderer);
    Rml::SetTextInputHandler(&state.Ime);
    if (!Rml::Initialise())
    {
        return false;
    }
    state.Ready = true;
    Rml::LoadFontFace("C:/Windows/Fonts/segoeui.ttf");
    Rml::LoadFontFace("C:/Windows/Fonts/segoeuib.ttf");
    Rml::LoadFontFace("C:/Windows/Fonts/msyh.ttc", true);
    RECT client{};
    GetClientRect(window, &client);
    state.Context = Rml::CreateContext("game", Rml::Vector2i(client.right, client.bottom), renderer, &state.Ime);
    if (!state.Context)
    {
        return false;
    }
    // The engine sample screen is only a fallback. Game code owns which project UI
    // asset is visible through SetDocument, so a missing sample must not be fatal.
    const auto documentPath = root / "Content/UI/Main.rml";
    if (std::filesystem::exists(documentPath))
    {
        const auto utf8Path = documentPath.u8string();
        state.Document = state.Context->LoadDocument(
            Rml::String(reinterpret_cast<const char*>(utf8Path.data()), utf8Path.size()));
    }
    if (state.Document)
    {
        state.Document->AddEventListener(Rml::EventId::Click, &state.Events);
        state.Document->Show();
        EngineLog::Info("RmlUi", "Game UI initialized document=" + documentPath.generic_string());
    }
    else
    {
        EngineLog::Info("RmlUi",
            "Game UI started without a document; game code selects the screen");
    }
    Update(client.right, client.bottom);
    return true;
}

bool GameUiShell::SetDocument(std::string_view rml, std::string_view sourcePath)
{
    if (!State || !State->Context || rml.empty())
    {
        return false;
    }
    auto& state = *State;
    if (state.Document)
    {
        state.Context->UnloadDocument(state.Document);
        state.Document = nullptr;
    }
    // The source URL keeps relative stylesheet links next to the authoritative
    // asset, which for a .sui package is the package file itself.
    const auto SourceUrl = RmlUiSourceUrl::FromFilePath(sourcePath);
    const auto Rml = InjectUserAgentStyle(rml);
    state.Document = state.Context->LoadDocumentFromMemory(
        Rml::String(Rml.data(), Rml.size()), SourceUrl.empty() ? "[runtime screen]" : SourceUrl);
    if (!state.Document)
    {
        return false;
    }
    state.Document->AddEventListener(Rml::EventId::Click, &state.Events);
    state.Document->Show();
    state.Semantic.clear();
    ++state.StateRevision;
    // No Update() here: when this runs from the action flush, the caller's Update is
    // already in progress and the new document is laid out immediately afterwards.
    EngineLog::Info("RmlUi", "Game UI document set source=" + std::string(sourcePath));
    return true;
}

bool GameUiShell::ClearDocument()
{
    if (!State || !State->Context)
    {
        return false;
    }
    auto& state = *State;
    if (!state.Document)
    {
        return true;
    }
    state.Context->UnloadDocument(state.Document);
    state.Document = nullptr;
    state.Semantic.clear();
    ++state.StateRevision;
    EngineLog::Info("RmlUi", "Game UI document cleared");
    return true;
}

bool GameUiShell::HasDocument() const { return State && State->Document; }

void GameUiShell::Shutdown()
{
    if (State && State->Ready)
    {
        Rml::Shutdown();
    }
    Rml::SetTextInputHandler(nullptr);
    State.reset();
}

void GameUiShell::Update(uint32_t width, uint32_t height)
{
    if (!State || !State->Context)
    {
        return;
    }
    // Actions are flushed before the context update so a recomposed document is
    // loaded outside RmlUi's event dispatch, never in the middle of it.
    if (!State->PendingActions.empty())
    {
        auto pending = std::move(State->PendingActions);
        State->PendingActions.clear();
        if (State->ActionHandler)
        {
            for (const auto& action : pending)
            {
                State->ActionHandler("game", action.c_str());
            }
        }
    }
    State->Context->SetDimensions(Rml::Vector2i(static_cast<int>(width), static_cast<int>(height)));
    if (State->Document)
    {
        if (auto* resolution = State->Document->GetElementById("resolution"))
        {
            resolution->SetInnerRML(std::to_string(width) + " x " + std::to_string(height));
        }
    }
    State->Context->Update();
}
void GameUiShell::Render()
{
    if (State && State->Context)
    {
        State->Context->Render();
    }
}

bool GameUiShell::HandleMessage(UINT message, WPARAM wParam, LPARAM lParam, bool GameplayCaptured)
{
    if (!State || !State->Context)
    {
        return false;
    }
    if (State->InjectedModifiers >= 0)
    {
        auto* context = State->Context;
        const auto injected = State->InjectedModifiers;
        const int modifiers = ((injected & 2) ? Rml::Input::KM_SHIFT : 0) | ((injected & 1) ? Rml::Input::KM_CTRL : 0) |
                              ((injected & 4) ? Rml::Input::KM_ALT : 0);
        const auto x = static_cast<int>(static_cast<short>(LOWORD(lParam)));
        const auto y = static_cast<int>(static_cast<short>(HIWORD(lParam)));
        if (message == WM_MOUSEMOVE)
        {
            return !context->ProcessMouseMove(x, y, modifiers);
        }
        if (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN)
        {
            return !context->ProcessMouseButtonDown(message == WM_LBUTTONDOWN ? 0 : message == WM_RBUTTONDOWN ? 1 : 2, modifiers);
        }
        if (message == WM_LBUTTONUP || message == WM_RBUTTONUP || message == WM_MBUTTONUP)
        {
            return !context->ProcessMouseButtonUp(message == WM_LBUTTONUP ? 0 : message == WM_RBUTTONUP ? 1 : 2, modifiers);
        }
        if (message == WM_KEYDOWN || message == WM_KEYUP)
        {
            const auto key = RmlWin32::ConvertKey(static_cast<int>(wParam));
            return message == WM_KEYDOWN ? !context->ProcessKeyDown(key, modifiers) : !context->ProcessKeyUp(key, modifiers);
        }
    }
    if (message == WM_LBUTTONUP && GameplayCaptured)
    {
        return !State->Context->ProcessMouseButtonUp(0, RmlWin32::GetKeyModifierState());
    }
    return !RmlWin32::WindowProcedure(State->Context, State->Ime, State->Window, message, wParam, lParam);
}

bool GameUiShell::IsFocused() const { return State && State->Context && State->Context->GetFocusElement(); }

uint32_t GameUiShell::InputModifiers(uint32_t nativeModifiers) const
{
    return State && State->InjectedModifiers >= 0 ? static_cast<uint32_t>(State->InjectedModifiers) : nativeModifiers;
}

void GameUiShell::SetPresentHandler(std::function<bool()> handler) { State->Present = std::move(handler); }

void GameUiShell::SetActionHandler(std::function<void(const char*, const char*)> handler)
{
    if (State)
    {
        State->ActionHandler = std::move(handler);
    }
}

void GameUiShell::CommitPresentation()
{
    if (!State || !State->Context || !GetActiveRenderBackend()->WasPresented())
    {
        return;
    }
    auto& state = *State;
    const auto semantic = state.Document
                              ? UiObservation::DescribeHost(*state.Context, state.Document)
                              : std::string{};
    if (semantic != state.Semantic)
    {
        state.Semantic = semantic;
        ++state.StateRevision;
    }
    ++state.FrameId;
}

std::string GameUiShell::DescribeUi(uint64_t layoutRevision, uint64_t documentRevision)
{
    auto& state = *State;
    if (!state.PinFrame())
    {
        return state.Failure("UI_SNAPSHOT_UNAVAILABLE", "frameId", "a completed presentation", "none");
    }
    return "{\"schemaVersion\":1,\"context\":\"runtime\",\"stateRevision\":" + std::to_string(state.StateRevision) +
           ",\"layoutRevision\":" + std::to_string(layoutRevision) + ",\"documentRevision\":" + std::to_string(documentRevision) +
           ",\"frameId\":" + std::to_string(state.FrameId) + ",\"hosts\":[" + state.Semantic + "],\"diagnostics\":[]}";
}

std::string GameUiShell::PointerInput(std::string_view hostId, uint32_t event, int32_t x, int32_t y, uint32_t button,
                                      uint32_t buttons, uint32_t modifiers, uint32_t pointerId, uint64_t expectedFrameId,
                                      float wheelX, float wheelY, std::string_view expectedElementId)
{
    auto& state = *State;
    if (hostId != "game")
    {
        return state.Failure("UI_HOST_NOT_FOUND", "hostId", "game", hostId);
    }
    const auto* frame = state.FindFrame(expectedFrameId);
    if (!frame || (frame->Revision != state.StateRevision && expectedElementId.empty()))
    {
        return state.Failure("UI_FRAME_STALE", "expectedFrameId", "a retained frame", std::to_string(expectedFrameId));
    }
    const auto dimensions = state.Context->GetDimensions();
    if (frame->Width != dimensions.x || frame->Height != dimensions.y)
    {
        return state.Failure("UI_FRAME_STALE", "expectedFrameId", "unchanged host dimensions", "resized");
    }
    if (x < 0 || y < 0 || x >= dimensions.x || y >= dimensions.y)
    {
        return state.Failure("UI_COORDINATE_OUT_OF_BOUNDS", "x,y", "host-local bounds",
                             std::to_string(x) + "," + std::to_string(y));
    }
    const uint32_t bit = button == 1 ? MK_LBUTTON : button == 2 ? MK_RBUTTON : button == 3 ? MK_MBUTTON : 0;
    if (pointerId != 0 || event > 4 || modifiers > 7 || (event == 1 && (!bit || (state.Buttons & bit))) ||
        (event == 2 && (!bit || !(state.Buttons & bit))) || (event != 1 && event != 2 && button != 0) || !std::isfinite(wheelX) ||
        !std::isfinite(wheelY) || (event != 4 && (wheelX != 0 || wheelY != 0)))
    {
        return state.Failure("UI_POINTER_SEQUENCE_INVALID", "event", "a valid pointer sequence", std::to_string(event));
    }
    // Use the same window/input route as physical input, including the Game
    // InputService. RmlUi performs clipping, hit testing, focus and bubbling.
    auto* target = state.Context->GetElementAtPoint(Rml::Vector2f(float(x), float(y)));
    const auto targetId = UiObservation::Identity(target);
    if (!expectedElementId.empty() && targetId != expectedElementId)
    {
        return state.Failure("UI_TARGET_CHANGED", "expectedElementId", "the current hit target", targetId);
    }
    const auto before = state.StateRevision;
    const auto focusBefore = UiObservation::Identity(state.Context->GetFocusElement());
    state.InjectedModifiers = static_cast<int>(modifiers);
    SendMessageW(state.Window, WM_MOUSEMOVE, state.Buttons, MAKELPARAM(x, y));
    if (event == 1)
    {
        state.Buttons |= bit;
        SendMessageW(state.Window,
                     button == 1   ? WM_LBUTTONDOWN
                     : button == 2 ? WM_RBUTTONDOWN
                                   : WM_MBUTTONDOWN,
                     state.Buttons, MAKELPARAM(x, y));
    }
    else if (event == 2)
    {
        state.Buttons &= ~bit;
        SendMessageW(state.Window,
                     button == 1   ? WM_LBUTTONUP
                     : button == 2 ? WM_RBUTTONUP
                                   : WM_MBUTTONUP,
                     state.Buttons, MAKELPARAM(x, y));
    }
    else if (event == 3)
    {
        state.Context->ProcessMouseLeave();
        for (int index = 0; index < 3; ++index)
        {
            if (state.Buttons & (index == 0 ? MK_LBUTTON : index == 1 ? MK_RBUTTON : MK_MBUTTON))
            {
                state.Context->ProcessMouseButtonUp(index, static_cast<int>(modifiers));
            }
        }
        state.Buttons = 0;
        ReleaseCapture();
    }
    else if (event == 4)
    {
        state.Context->ProcessMouseWheel(Rml::Vector2f(wheelX, wheelY), static_cast<int>(modifiers));
    }
    state.InjectedModifiers = -1;
    return state.InputResult(before, target, focusBefore);
}

std::string GameUiShell::KeyInput(std::string_view hostId, uint32_t event, uint32_t key, uint32_t modifiers,
                                  std::string_view text, uint64_t expectedFrameId)
{
    auto& state = *State;
    if (hostId != "game")
    {
        return state.Failure("UI_HOST_NOT_FOUND", "hostId", "game", hostId);
    }
    const auto* frame = state.FindFrame(expectedFrameId);
    if (!frame || frame->Revision != state.StateRevision)
    {
        return state.Failure("UI_FRAME_STALE", "expectedFrameId", "current stateRevision", std::to_string(expectedFrameId));
    }
    if (event > 2 || modifiers > 7 || key > 255)
    {
        return state.Failure("UI_KEY_SEQUENCE_INVALID", "event,key", "a supported key event", std::to_string(event));
    }
    auto* target = state.Context->GetFocusElement();
    if (!target)
    {
        return state.Failure("UI_TARGET_NOT_INTERACTIVE", "focus", "a focused element", "none");
    }
    const auto before = state.StateRevision;
    const auto focusBefore = UiObservation::Identity(target);
    state.InjectedModifiers = static_cast<int>(modifiers);
    if (event < 2)
    {
        SendMessageW(state.Window, event == 0 ? WM_KEYDOWN : WM_KEYUP, key, 0);
    }
    else
    {
        state.Context->ProcessTextInput(Rml::String(text));
    }
    state.InjectedModifiers = -1;
    return state.InputResult(before, target, focusBefore);
}

uint32_t GameUiShell::ReadFrame(uint64_t frameId, std::string_view hostId, unsigned char* pixels, uint32_t capacity,
                                uint32_t& width, uint32_t& height) const
{
    width = 0;
    height = 0;
    const auto* frame = State->FindFrame(frameId);
    if (!frame || hostId != "game" || frame->Pixels.size() > UINT32_MAX)
    {
        return 0;
    }
    width = frame->Width;
    height = frame->Height;
    const auto required = static_cast<uint32_t>(frame->Pixels.size());
    if (pixels && capacity >= required)
    {
        memcpy(pixels, frame->Pixels.data(), required);
    }
    return required;
}
