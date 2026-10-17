#include "RmlUiShell.h"
#include <RmlUi/Core.h>
#include <RmlUi/Core/Box.h>
#include <RmlUi/Core/ElementInstancer.h>
#include <RmlUi/Core/ElementText.h>
#include <RmlUi/Core/ElementUtilities.h>
#include <dwmapi.h>
#include <shobjidl.h>
#include "RmlUi_Platform_Win32.h"
#include "RmlUi_Renderer_GL2.h"
#include <gl/GL.h>
#include <windowsx.h>
#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstring>
#include <deque>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <string>
#include <utility>
#include <vector>

#pragma comment(lib, "dwmapi.lib")

namespace
{
    constexpr wchar_t HostProperty[] = L"SharpEngine.RmlUiHost";
    constexpr wchar_t DockWindowClass[] = L"SharpEngineDockWindow";
    constexpr wchar_t DockGhostClass[] = L"SharpEngineDockGhost";

    std::string JsonString(std::string_view value)
    {
        std::ostringstream result;
        result << '"';
        for (const unsigned char character : value)
        {
            switch (character)
            {
                case '"': result << "\\\""; break;
                case '\\': result << "\\\\"; break;
                case '\b': result << "\\b"; break;
                case '\f': result << "\\f"; break;
                case '\n': result << "\\n"; break;
                case '\r': result << "\\r"; break;
                case '\t': result << "\\t"; break;
                default:
                    if (character < 0x20)
                    {
                        result << "\\u" << std::hex << std::setw(4)
                               << std::setfill('0') << static_cast<int>(character)
                               << std::dec << std::setfill(' ');
                    }
                    else result << static_cast<char>(character);
                    break;
            }
        }
        result << '"';
        return result.str();
    }

    std::string JsonNumber(float value)
    {
        if (!std::isfinite(value)) return "0";
        std::ostringstream result;
        result << std::fixed << std::setprecision(3) << value;
        auto text = result.str();
        while (text.size() > 1 && text.back() == '0') text.pop_back();
        if (!text.empty() && text.back() == '.') text.pop_back();
        return text;
    }

    std::string Utf8File(const std::filesystem::path& path)
    {
        const auto utf8 = path.u8string();
        return {reinterpret_cast<const char*>(utf8.data()), utf8.size()};
    }

    std::wstring Utf8ToWide(const char* text)
    {
        if (!text) return {};
        const int length = MultiByteToWideChar(CP_UTF8, 0, text, -1, nullptr, 0);
        if (length <= 1) return {};
        std::wstring result(static_cast<size_t>(length), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text, -1, result.data(), length);
        result.resize(static_cast<size_t>(length - 1));
        return result;
    }

    bool IsValidHostId(std::string_view id)
    {
        if (id.empty() || id.size() > 128) return false;
        return std::all_of(id.begin(), id.end(), [](unsigned char character) {
            return std::isalnum(character) || character == '.' ||
                   character == '_' || character == '-';
        });
    }

    std::wstring PaneDisplayName(std::string_view pane)
    {
        if (pane == "viewport") return L"Viewport";
        if (pane == "hierarchy") return L"Hierarchy";
        if (pane == "content") return L"Content Manager";
        if (pane == "inspector") return L"Inspector";
        if (pane == "console") return L"Console";
        return Utf8ToWide(std::string(pane).c_str());
    }

    LRESULT CALLBACK DockGhostProcedure(HWND window, UINT message,
                                        WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_NCHITTEST) return HTTRANSPARENT;
        if (message == WM_ERASEBKGND) return 1;
        if (message == WM_PAINT)
        {
            PAINTSTRUCT paint{};
            const HDC dc = BeginPaint(window, &paint);
            RECT client{};
            GetClientRect(window, &client);
            const HBRUSH background = CreateSolidBrush(RGB(25, 31, 39));
            const HBRUSH header = CreateSolidBrush(RGB(31, 38, 48));
            const HPEN border = CreatePen(PS_SOLID, 2, RGB(112, 169, 223));
            FillRect(dc, &client, background);
            RECT headerBounds = client;
            headerBounds.bottom = (std::min)(client.bottom, 34L);
            FillRect(dc, &headerBounds, header);
            const HGDIOBJ previousPen = SelectObject(dc, border);
            const HGDIOBJ previousBrush = SelectObject(dc, GetStockObject(NULL_BRUSH));
            Rectangle(dc, client.left, client.top, client.right, client.bottom);
            SelectObject(dc, previousBrush);
            SelectObject(dc, previousPen);
            wchar_t title[256]{};
            GetWindowTextW(window, title, 256);
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, RGB(225, 233, 243));
            RECT textBounds{12, 0, (std::max)(12L, client.right - 12), headerBounds.bottom};
            DrawTextW(dc, title, -1, &textBounds,
                      DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS);
            DeleteObject(border);
            DeleteObject(header);
            DeleteObject(background);
            EndPaint(window, &paint);
            return 0;
        }
        if (message == WM_CLOSE)
        {
            ShowWindow(window, SW_HIDE);
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }

    Rml::String ChooseProjectPath(HWND window, bool create)
    {
        IFileDialog* dialog = nullptr;
        const HRESULT created = create
            ? CoCreateInstance(CLSID_FileSaveDialog, nullptr, CLSCTX_INPROC_SERVER,
                               IID_PPV_ARGS(&dialog))
            : CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER,
                               IID_PPV_ARGS(&dialog));
        if (FAILED(created)) return {};
        DWORD options{};
        dialog->GetOptions(&options);
        dialog->SetOptions(options | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST |
                           FOS_NOCHANGEDIR | FOS_NOTESTFILECREATE);
        dialog->SetTitle(create
            ? L"New Project - choose parent folder and enter a project name"
            : L"Open SharpEngine Project");
        dialog->SetOkButtonLabel(create ? L"Create project" : L"Open project");
        if (create)
        {
            dialog->SetFileName(L"NewGame");
            dialog->SetFileNameLabel(L"Project name:");
        }
        else
        {
            const COMDLG_FILTERSPEC filter{L"SharpEngine project", L"*.sharpproject"};
            dialog->SetFileTypes(1, &filter);
        }
        Rml::String path;
        if (SUCCEEDED(dialog->Show(window)))
        {
            IShellItem* item = nullptr;
            if (SUCCEEDED(dialog->GetResult(&item)))
            {
                PWSTR wide = nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &wide)))
                {
                    path = RmlWin32::ConvertToUTF8(wide);
                    CoTaskMemFree(wide);
                }
                item->Release();
            }
        }
        dialog->Release();
        return path;
    }

    LRESULT BorderlessHitTest(HWND window, POINT point)
    {
        RECT client{};
        GetClientRect(window, &client);
        if (!IsZoomed(window))
        {
            const UINT dpi = GetDpiForWindow(window);
            const int padding = GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
            const int borderX = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + padding;
            const int borderY = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + padding;
            const bool left = point.x < borderX;
            const bool right = point.x >= client.right - borderX;
            const bool top = point.y < borderY;
            const bool bottom = point.y >= client.bottom - borderY;
            if (top && left) return HTTOPLEFT;
            if (top && right) return HTTOPRIGHT;
            if (bottom && left) return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
        }
        return HTCLIENT;
    }

    LRESULT CALLBACK DockWindowProcedure(HWND window, UINT message,
                                         WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_NCHITTEST)
        {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            ScreenToClient(window, &point);
            return BorderlessHitTest(window, point);
        }
        if (message == WM_NCCALCSIZE) return 0;
        if (message == WM_GETMINMAXINFO)
        {
            MONITORINFO monitor{sizeof(MONITORINFO)};
            if (GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST),
                                &monitor))
            {
                auto* bounds = reinterpret_cast<MINMAXINFO*>(lParam);
                bounds->ptMaxPosition.x = monitor.rcWork.left - monitor.rcMonitor.left;
                bounds->ptMaxPosition.y = monitor.rcWork.top - monitor.rcMonitor.top;
                bounds->ptMaxSize.x = monitor.rcWork.right - monitor.rcWork.left;
                bounds->ptMaxSize.y = monitor.rcWork.bottom - monitor.rcWork.top;
                return 0;
            }
        }
        if (message == WM_ERASEBKGND) return 1;
        if (message == WM_PAINT)
        {
            PAINTSTRUCT paint{};
            BeginPaint(window, &paint);
            EndPaint(window, &paint);
            return 0;
        }
        if (message == WM_CLOSE)
        {
            ShowWindow(window, SW_HIDE);
            return 0;
        }
        if (message == WM_DESTROY) return 0;
        return DefWindowProcW(window, message, wParam, lParam);
    }
}

class CountedRenderer : public RenderInterface_GL2
{
public:
    uint32_t DrawCalls{};
    void RenderGeometry(Rml::CompiledGeometryHandle handle,
                        Rml::Vector2f translation,
                        Rml::TextureHandle texture) override
    {
        ++DrawCalls;
        RenderInterface_GL2::RenderGeometry(handle, translation, texture);
    }
};

class Win32FileInterface : public Rml::FileInterface
{
public:
    Rml::FileHandle Open(const Rml::String& path) override
    {
        return reinterpret_cast<Rml::FileHandle>(
            _wfopen(RmlWin32::ConvertToUTF16(path).c_str(), L"rb"));
    }
    void Close(Rml::FileHandle file) override
    {
        fclose(reinterpret_cast<FILE*>(file));
    }
    size_t Read(void* buffer, size_t size, Rml::FileHandle file) override
    {
        return fread(buffer, 1, size, reinterpret_cast<FILE*>(file));
    }
    bool Seek(Rml::FileHandle file, long offset, int origin) override
    {
        return fseek(reinterpret_cast<FILE*>(file), offset, origin) == 0;
    }
    size_t Tell(Rml::FileHandle file) override
    {
        return ftell(reinterpret_cast<FILE*>(file));
    }
};

class ViewportElement : public Rml::Element
{
public:
    explicit ViewportElement(const Rml::String& tag) : Rml::Element(tag) {}
    static thread_local RmlUiShell* RenderTarget;
protected:
    void OnRender() override
    {
        if (!RenderTarget) return;
        const auto position = GetAbsoluteOffset();
        const auto size = GetBox().GetSize();
        RenderTarget->RenderViewport(
            {position.x, position.y, size.x, size.y});
    }
};
thread_local RmlUiShell* ViewportElement::RenderTarget = nullptr;
static Rml::ElementInstancerGeneric<ViewportElement> GViewportInstancer;

struct RmlUiShell::Impl
{
    struct Host : Rml::EventListener
    {
        Impl* Owner{};
        std::string Id;
        std::string ContextName;
        HWND Window{};
        HDC Dc{};
        WNDPROC OriginalProcedure{};
        Rml::Context* Context{};
        Rml::ElementDocument* Document{};
        TextInputMethodEditor_Win32 Ime;
        Rml::Element* OpenPopup{};
        bool OwnsWindow{};
        bool Reported{};
        bool PendingDestroy{};
        std::string HierarchySource;
        Rml::Element* HierarchyDropTarget{};
        std::string HierarchyDropClass;

        void ProcessEvent(Rml::Event& event) override;
    };

    HWND MainWindow{};
    std::filesystem::path EngineRoot;
    SystemInterface_Win32 System;
    Win32FileInterface Files;
    CountedRenderer Renderer;
    HGLRC Gl{};
    int PixelFormat{};
    PIXELFORMATDESCRIPTOR PixelFormatDescription{};
    bool Ready{};
    bool ComInitialized{};
    int CallbackDepth{};
    int WindowProcedureDepth{};
    std::vector<std::unique_ptr<Host>> Hosts;
    std::function<void(const char*, const char*)> Action;
    std::function<void(int, int)> Pointer;
    std::function<void(int, int, uint32_t, uint32_t, int32_t)> ViewportInput;
    std::function<void(uint32_t, bool, bool)> ViewportKey;
    std::vector<UiSceneVertex> Triangles;
    std::vector<UiSceneVertex> Lines;
    UiViewportRect SceneRect{};
    std::vector<std::pair<std::string, std::string>> DeferredActions;
    HWND DockGhostWindow{};

    struct FrameHost
    {
        std::string HostId;
        uint32_t Width{};
        uint32_t Height{};
        std::vector<unsigned char> Pixels;
    };
    struct FrameSet
    {
        uint64_t FrameId{};
        uint64_t StateRevision{};
        std::vector<FrameHost> Hosts;
    };
    uint64_t StateRevision{};
    uint64_t FrameId{};
    std::string LastSemanticUi;
    std::string LastSnapshot;
    FrameSet CurrentFrame;
    std::deque<FrameSet> PinnedFrames;
    uint32_t McpButtons{};
    int InjectedModifiers{-1};
    float InjectedWheelX{};
    float InjectedWheelY{};
    std::string LastInputTarget;
    std::vector<std::string> LastEventPath;

    struct DockDragState
    {
        Host* SourceHost{};
        Rml::Element* SourceElement{};
        Rml::Element* PreviewElement{};
        Rml::Element* InsertElement{};
        Rml::Element* GuideElement{};
        Host* TargetHost{};
        POINT StartScreen{};
        std::string Pane;
        std::string Target;
        std::string Zone;
        int GhostWidth{420};
        int GhostHeight{260};
        int Index{-1};
        bool Pending{};
        bool Active{};
        bool InsideAnyHost{};
    } DockDrag;

    struct SplitterDragState
    {
        struct PreviewNode
        {
            Rml::Element* SplitElement{};
            Rml::Element* FirstElement{};
            Rml::Element* SecondElement{};
            Rml::Element* SplitterElement{};
            std::string Node;
            std::string Orientation;
            float InitialRatio{0.5f};
            float Boundary{};
            float PreviewRatio{0.5f};
        };

        Host* HostWindow{};
        Rml::Element* Element{};
        Rml::Element* SplitElement{};
        Rml::Element* FirstElement{};
        Rml::Element* SecondElement{};
        std::vector<PreviewNode> PreviewNodes;
        std::string Node;
        std::string Orientation;
        float InitialRatio{0.5f};
        float Ratio{};
        float AxisStart{};
        float AxisEnd{};
        float MinimumBoundary{};
        float MaximumBoundary{};
        bool Active{};
    } SplitterDrag;

    HWND ViewportCaptureWindow{};

    Host* FindHost(std::string_view id) const
    {
        const auto found = std::find_if(Hosts.begin(), Hosts.end(),
            [id](const auto& host) { return host->Id == id; });
        return found == Hosts.end() ? nullptr : found->get();
    }

    Host* FindMainHost() const { return FindHost("main"); }

    static Rml::Element* FindAttributeElement(Rml::Element* element,
                                               const char* attribute)
    {
        for (; element; element = element->GetParentNode())
            if (element->HasAttribute(attribute)) return element;
        return nullptr;
    }

    static Rml::Element* FindDescendantWithAttribute(Rml::Element* root,
                                                      const char* attribute,
                                                      std::string_view value)
    {
        if (!root) return nullptr;
        for (int index = 0; index < root->GetNumChildren(); ++index)
        {
            auto* child = root->GetChild(index);
            const auto attributeValue = child->GetAttribute<Rml::String>(
                attribute, "");
            if (attributeValue == value) return child;
            if (auto* nested = FindDescendantWithAttribute(
                    child, attribute, value)) return nested;
        }
        return nullptr;
    }

    static Rml::Element* FindChildWithAttribute(Rml::Element* root,
                                                 const char* attribute,
                                                 std::string_view value)
    {
        if (!root) return nullptr;
        for (int index = 0; index < root->GetNumChildren(); ++index)
        {
            auto* child = root->GetChild(index);
            if (child->GetAttribute<Rml::String>(attribute, "") == value)
                return child;
        }
        return nullptr;
    }

    static Rml::Element* FindChildWithAttribute(Rml::Element* root,
                                                 const char* attribute)
    {
        if (!root) return nullptr;
        for (int index = 0; index < root->GetNumChildren(); ++index)
        {
            auto* child = root->GetChild(index);
            if (child->HasAttribute(attribute)) return child;
        }
        return nullptr;
    }

    static Rml::Element* FindHierarchyRoot(Rml::Element* element)
    {
        for (; element; element = element->GetParentNode())
            if (element->GetId() == "hierarchyItems" ||
                element->HasAttribute("data-hierarchy-root")) return element;
        return nullptr;
    }

    static std::string Attribute(Rml::Element* element, const char* name)
    {
        return element
            ? element->GetAttribute<Rml::String>(name, "") : std::string{};
    }

    static float FloatAttribute(Rml::Element* element, const char* name,
                                float fallback)
    {
        const auto value = Attribute(element, name);
        if (value.empty()) return fallback;
        try { return std::stof(value); }
        catch (...) { return fallback; }
    }

    static bool HasAncestor(Rml::Element* element, const Rml::Element* ancestor)
    {
        for (; element; element = element->GetParentNode())
            if (element == ancestor) return true;
        return false;
    }

    static std::string ElementIdentity(const Host& host, Rml::Element* element)
    {
        if (!element) return {};
        const std::string prefix = "host/" + host.Id;
        if (element == host.Document) return prefix;
        if (const auto node = Attribute(element, "data-dock-splitter-node");
            !node.empty()) return prefix + "/split/" + node + "/handle";
        if (const auto node = Attribute(element, "data-dock-split-node");
            !node.empty()) return prefix + "/split/" + node;
        if (const auto pane = Attribute(element, "data-pane-id");
            !pane.empty()) return prefix + "/pane/" + pane;
        if (const auto pane = Attribute(element, "data-dock-pane");
            !pane.empty()) return prefix + "/pane/" + pane + "/tab";
        if (const auto group = Attribute(element, "data-dock-group");
            !group.empty()) return prefix + "/group/" + group;
        if (!element->GetId().empty()) return prefix + "/id/" + element->GetId();
        auto action = Attribute(element, "data-action");
        if (action.empty()) action = Attribute(element, "action");
        if (!action.empty()) return prefix + "/action/" + action;
        return {};
    }

    static std::string ElementRole(Rml::Element* element)
    {
        if (!element) return "unknown";
        if (element->HasAttribute("data-dock-splitter-node")) return "splitter";
        if (element->HasAttribute("data-dock-split-node")) return "split";
        if (element->HasAttribute("data-pane-id")) return "pane";
        if (element->HasAttribute("data-dock-pane")) return "tab";
        if (element->HasAttribute("data-dock-group")) return "tab-group";
        const auto& tag = element->GetTagName();
        if (tag == "button")
            for (auto* parent = element->GetParentNode(); parent;
                 parent = parent->GetParentNode())
                if (parent->GetId() == "contextMenu" ||
                    parent->IsClassSet("menu-popup")) return "menu-item";
        if (tag == "button") return "button";
        if (tag == "input" || tag == "textarea" || tag == "select") return "input";
        if (tag == "viewport") return "viewport";
        if (tag == "span" || tag == "label" || tag == "p") return "text";
        if (element->GetId().find("Menu") != std::string::npos ||
            element->IsClassSet("menu-popup")) return "menu";
        return element->GetParentNode() ? "group" : "host";
    }

    static bool IsInteractive(Rml::Element* element)
    {
        if (!element) return false;
        const auto role = ElementRole(element);
        return role == "splitter" || role == "tab" || role == "button" ||
            role == "input" || role == "viewport" || role == "menu-item" ||
            element->HasAttribute("action") || element->HasAttribute("data-action");
    }

    static std::string SemanticId(Rml::Element* element)
    {
        for (const char* attribute : {"data-dock-splitter-node",
                 "data-dock-split-node", "data-pane-id", "data-dock-pane",
                 "data-dock-group"})
        {
            const auto value = Attribute(element, attribute);
            if (!value.empty()) return value;
        }
        return element ? element->GetId() : std::string{};
    }

    static void AppendText(Rml::Element* element, std::string& text)
    {
        if (!element || text.size() >= 4096) return;
        if (const auto* textElement = dynamic_cast<Rml::ElementText*>(element))
        {
            if (!text.empty() && !std::isspace(static_cast<unsigned char>(text.back())))
                text.push_back(' ');
            text.append(textElement->GetText());
            if (text.size() > 4096) text.resize(4096);
            return;
        }
        for (int index = 0; index < element->GetNumChildren(); ++index)
            AppendText(element->GetChild(index), text);
    }

    static void CollectDirectSemanticChildren(const Host& host,
                                               Rml::Element* element,
                                               std::vector<std::string>& ids)
    {
        if (!element) return;
        for (int index = 0; index < element->GetNumChildren(); ++index)
        {
            auto* child = element->GetChild(index);
            const auto id = ElementIdentity(host, child);
            if (!id.empty()) ids.push_back(id);
            else CollectDirectSemanticChildren(host, child, ids);
        }
    }

    static std::string RectJson(float x, float y, float width, float height)
    {
        return "{\"x\":" + JsonNumber(x) + ",\"y\":" + JsonNumber(y) +
            ",\"width\":" + JsonNumber((std::max)(0.0f, width)) +
            ",\"height\":" + JsonNumber((std::max)(0.0f, height)) + "}";
    }

    std::string ElementJson(const Host& host, Rml::Element* element,
                            std::string_view parentId) const
    {
        const auto id = ElementIdentity(host, element);
        const auto position = element->GetAbsoluteOffset(Rml::BoxArea::Border);
        const auto size = element->GetBox().GetSize(Rml::BoxArea::Border);
        Rml::Rectanglei clip{};
        const bool hasClip = Rml::ElementUtilities::GetClippingRegion(element, clip);
        RECT client{};
        GetClientRect(host.Window, &client);
        const float clipX = hasClip ? static_cast<float>(clip.Left()) : 0.0f;
        const float clipY = hasClip ? static_cast<float>(clip.Top()) : 0.0f;
        const float clipWidth = hasClip ? static_cast<float>(clip.Size().x) :
            static_cast<float>(client.right);
        const float clipHeight = hasClip ? static_cast<float>(clip.Size().y) :
            static_cast<float>(client.bottom);
        const bool visible = element->IsVisible(true) && size.x > 0 && size.y > 0 &&
            position.x + size.x > clipX && position.y + size.y > clipY &&
            position.x < clipX + clipWidth && position.y < clipY + clipHeight;
        const bool enabled = !element->HasAttribute("disabled");
        const bool focused = host.Context && host.Context->GetFocusElement() == element;
        const bool hovered = host.Context &&
            HasAncestor(host.Context->GetHoverElement(), element);
        const bool focusable = element->GetComputedValues().focus() != Rml::Style::Focus::None;
        const bool selected = element->IsClassSet("active") ||
            element->IsClassSet("selected") || Attribute(element, "aria-selected") == "true";
        const bool checked = element->HasAttribute("checked") ||
            element->IsClassSet("checked") || Attribute(element, "aria-checked") == "true";
        const bool expanded = element->HasAttribute("open") ||
            Attribute(element, "aria-expanded") == "true";
        bool hitTarget = false;
        if (visible && enabled && IsInteractive(element) && host.Context &&
            element->GetComputedValues().pointer_events() != Rml::Style::PointerEvents::None)
        {
            auto* hit = host.Context->GetElementAtPoint(
                {position.x + size.x * 0.5f, position.y + size.y * 0.5f});
            hitTarget = HasAncestor(hit, element);
        }
        std::vector<std::string> children;
        CollectDirectSemanticChildren(host, element, children);
        const auto role = ElementRole(element);
        std::string text;
        if (role == "text" || role == "button" || role == "tab")
            AppendText(element, text);
        const auto value = role == "input" ? Attribute(element, "value") : std::string{};
        auto action = Attribute(element, "data-action");
        if (action.empty()) action = Attribute(element, "action");
        std::ostringstream json;
        json << "{\"elementId\":" << JsonString(id)
             << ",\"parentId\":" << (parentId.empty() ? "null" : JsonString(parentId))
             << ",\"role\":" << JsonString(role)
             << ",\"semanticId\":" << JsonString(SemanticId(element))
             << ",\"bounds\":" << RectJson(position.x, position.y, size.x, size.y)
             << ",\"clip\":" << RectJson(clipX, clipY, clipWidth, clipHeight)
             << ",\"visible\":" << (visible ? "true" : "false")
             << ",\"enabled\":" << (enabled ? "true" : "false")
             << ",\"focusable\":" << (focusable ? "true" : "false")
             << ",\"focused\":" << (focused ? "true" : "false")
             << ",\"hovered\":" << (hovered ? "true" : "false")
             << ",\"selected\":" << (selected ? "true" : "false")
             << ",\"checked\":" << (checked ? "true" : "false")
             << ",\"expanded\":" << (expanded ? "true" : "false")
             << ",\"zOrder\":" << JsonNumber(element->GetZIndex())
             << ",\"hitTarget\":" << (hitTarget ? "true" : "false")
             << ",\"text\":" << (text.empty() ? "null" : JsonString(text))
             << ",\"value\":" << (role == "input" ? JsonString(value) : "null")
             << ",\"actions\":[";
        if (role == "splitter") json << JsonString("resize");
        else if (!action.empty()) json << JsonString(action);
        json << "],\"children\":[";
        for (size_t index = 0; index < children.size(); ++index)
        {
            if (index) json << ',';
            json << JsonString(children[index]);
        }
        json << "]}";
        return json.str();
    }

    void CollectElementJson(const Host& host, Rml::Element* element,
                            std::string parentId,
                            std::vector<std::string>& elements) const
    {
        if (!element) return;
        const auto id = ElementIdentity(host, element);
        const auto nextParent = id.empty() ? parentId : id;
        if (!id.empty()) elements.push_back(ElementJson(host, element, parentId));
        for (int index = 0; index < element->GetNumChildren(); ++index)
            CollectElementJson(host, element->GetChild(index), nextParent, elements);
    }

    std::vector<std::string> EventPath(const Host& host,
                                       Rml::Element* target) const
    {
        std::vector<std::string> path;
        for (auto* element = target; element; element = element->GetParentNode())
        {
            const auto id = ElementIdentity(host, element);
            if (!id.empty()) path.push_back(id);
        }
        return path;
    }

    static std::string StableAncestorId(const Host& host, Rml::Element* element)
    {
        for (; element; element = element->GetParentNode())
        {
            const auto id = ElementIdentity(host, element);
            if (!id.empty()) return id;
        }
        return {};
    }

    std::string InteractionJson(const Host& host) const
    {
        if (SplitterDrag.Active && SplitterDrag.HostWindow == &host)
        {
            const float initialBoundary = SplitterDrag.AxisStart +
                (SplitterDrag.AxisEnd - SplitterDrag.AxisStart) *
                    SplitterDrag.InitialRatio;
            const float previewBoundary = SplitterDrag.AxisStart +
                (SplitterDrag.AxisEnd - SplitterDrag.AxisStart) *
                    SplitterDrag.Ratio;
            std::ostringstream result;
            result << "{\"kind\":\"splitter-resize\",\"phase\":\"preview\""
                   << ",\"pointerId\":0,\"sourceElementId\":"
                   << JsonString(ElementIdentity(host, SplitterDrag.Element))
                   << ",\"targetNodeId\":" << JsonString(SplitterDrag.Node)
                   << ",\"orientation\":" << JsonString(SplitterDrag.Orientation)
                   << ",\"initialBoundary\":" << JsonNumber(initialBoundary)
                   << ",\"previewBoundary\":" << JsonNumber(previewBoundary)
                   << ",\"previewRatios\":[";
            bool first = true;
            for (const auto& node : SplitterDrag.PreviewNodes)
            {
                if (!first) result << ',';
                first = false;
                result << "{\"nodeId\":" << JsonString(node.Node)
                       << ",\"ratio\":" << JsonNumber(node.PreviewRatio) << '}';
            }
            result << "]}";
            return result.str();
        }
        if ((DockDrag.Pending || DockDrag.Active) && DockDrag.SourceHost == &host)
        {
            std::ostringstream result;
            result << "{\"kind\":\"dock-drag\",\"phase\":"
                   << JsonString(DockDrag.Active ? "preview" : "pending")
                   << ",\"pointerId\":0,\"sourceElementId\":"
                   << JsonString(StableAncestorId(host, DockDrag.SourceElement))
                   << ",\"paneId\":" << JsonString(DockDrag.Pane)
                   << ",\"targetHostId\":"
                   << (DockDrag.TargetHost ? JsonString(DockDrag.TargetHost->Id) : "null")
                   << ",\"targetNodeId\":"
                   << (DockDrag.Target.empty() ? "null" : JsonString(DockDrag.Target))
                   << ",\"zone\":"
                   << (DockDrag.Zone.empty() ? "null" : JsonString(DockDrag.Zone))
                   << '}';
            return result.str();
        }
        return "null";
    }

    std::string HostJson(const Host& host) const
    {
        RECT client{};
        GetClientRect(host.Window, &client);
        std::vector<std::string> elements;
        CollectElementJson(host, host.Document, {}, elements);
        const auto focused = StableAncestorId(host,
            host.Context ? host.Context->GetFocusElement() : nullptr);
        const auto hovered = StableAncestorId(host,
            host.Context ? host.Context->GetHoverElement() : nullptr);
        std::string capture;
        if (SplitterDrag.Active && SplitterDrag.HostWindow == &host)
            capture = ElementIdentity(host, SplitterDrag.Element);
        else if ((DockDrag.Pending || DockDrag.Active) && DockDrag.SourceHost == &host)
            capture = StableAncestorId(host, DockDrag.SourceElement);
        const bool presented = host.Context && host.Window &&
            (!host.OwnsWindow || IsWindowVisible(host.Window));
        std::ostringstream result;
        result << "{\"hostId\":" << JsonString(host.Id)
               << ",\"kind\":" << JsonString(host.OwnsWindow ? "floating" : "main")
               << ",\"bounds\":" << RectJson(0, 0,
                    static_cast<float>(client.right), static_cast<float>(client.bottom))
               << ",\"contentScale\":"
               << JsonNumber(host.Document ?
                    Rml::ElementUtilities::GetDensityIndependentPixelRatio(host.Document) : 1.0f)
               << ",\"visible\":" << (presented ? "true" : "false")
               << ",\"focusedElementId\":"
               << (focused.empty() ? "null" : JsonString(focused))
               << ",\"hoveredElementId\":"
               << (hovered.empty() ? "null" : JsonString(hovered))
               << ",\"pointerCapture\":";
        if (capture.empty()) result << "null";
        else result << "{\"pointerId\":0,\"elementId\":" << JsonString(capture) << '}';
        result << ",\"elements\":[";
        for (size_t index = 0; index < elements.size(); ++index)
        {
            if (index) result << ',';
            result << elements[index];
        }
        result << "],\"interaction\":" << InteractionJson(host) << '}';
        return result.str();
    }

    std::string BuildSemanticUi() const
    {
        std::ostringstream result;
        result << "{\"hosts\":[";
        bool first = true;
        for (const auto& host : Hosts)
        {
            if (!host->Context || !host->Document || !host->Window) continue;
            if (host->OwnsWindow && !IsWindowVisible(host->Window)) continue;
            if (!first) result << ',';
            first = false;
            result << HostJson(*host);
        }
        result << "],\"diagnostics\":[]}";
        return result.str();
    }

    void RefreshSemanticUi()
    {
        const auto semantic = BuildSemanticUi();
        if (semantic != LastSemanticUi)
        {
            LastSemanticUi = semantic;
            ++StateRevision;
        }
    }

    FrameHost CaptureFrameHost(const Host& host) const
    {
        RECT client{};
        GetClientRect(host.Window, &client);
        FrameHost frame;
        frame.HostId = host.Id;
        frame.Width = static_cast<uint32_t>((std::max)(0L, client.right));
        frame.Height = static_cast<uint32_t>((std::max)(0L, client.bottom));
        if (frame.Width == 0 || frame.Height == 0) return frame;
        frame.Pixels.resize(static_cast<size_t>(frame.Width) * frame.Height * 4);
        glReadBuffer(GL_FRONT);
        glReadPixels(0, 0, static_cast<GLsizei>(frame.Width),
                     static_cast<GLsizei>(frame.Height), 0x80E1,
                     GL_UNSIGNED_BYTE, frame.Pixels.data());
        return frame;
    }

    static FrameHost FrameHostMetadata(const Host& host)
    {
        RECT client{};
        GetClientRect(host.Window, &client);
        FrameHost frame;
        frame.HostId = host.Id;
        frame.Width = static_cast<uint32_t>((std::max)(0L, client.right));
        frame.Height = static_cast<uint32_t>((std::max)(0L, client.bottom));
        return frame;
    }

    void PinCurrentFrame()
    {
        if (CurrentFrame.FrameId == 0) return;
        if (!PinnedFrames.empty() &&
            PinnedFrames.back().FrameId == CurrentFrame.FrameId) return;
        FrameSet captured = CurrentFrame;
        for (auto& frameHost : captured.Hosts)
        {
            auto* host = FindHost(frameHost.HostId);
            if (!host || !SetCurrentHost(*host)) continue;
            frameHost = CaptureFrameHost(*host);
        }
        CurrentFrame = captured;
        PinnedFrames.push_back(std::move(captured));
        while (PinnedFrames.size() > 8) PinnedFrames.pop_front();
        if (auto* main = FindMainHost()) SetCurrentHost(*main);
    }

    const FrameHost* FindFrame(uint64_t frameId, std::string_view hostId) const
    {
        const auto findHost = [hostId](const FrameSet& frame) -> const FrameHost* {
            const auto found = std::find_if(frame.Hosts.begin(), frame.Hosts.end(),
                [hostId](const FrameHost& host) { return host.HostId == hostId; });
            return found == frame.Hosts.end() ? nullptr : &*found;
        };
        if (CurrentFrame.FrameId == frameId)
            if (const auto* host = findHost(CurrentFrame)) return host;
        for (auto frame = PinnedFrames.rbegin(); frame != PinnedFrames.rend(); ++frame)
            if (frame->FrameId == frameId)
                if (const auto* host = findHost(*frame)) return host;
        return nullptr;
    }

    int ModifierState() const
    {
        return InjectedModifiers >= 0 ? InjectedModifiers :
            RmlWin32::GetKeyModifierState();
    }

    const FrameSet* FindFrameSet(uint64_t frameId) const
    {
        if (CurrentFrame.FrameId == frameId) return &CurrentFrame;
        for (auto frame = PinnedFrames.rbegin(); frame != PinnedFrames.rend(); ++frame)
            if (frame->FrameId == frameId) return &*frame;
        return nullptr;
    }

    bool SetCurrentHost(Host& host)
    {
        System.SetWindow(host.Window);
        return !Gl || (host.Dc && wglMakeCurrent(host.Dc, Gl) != FALSE);
    }

    void HidePopups(Host& host)
    {
        if (host.OpenPopup)
        {
            host.OpenPopup->SetAttribute("open", false);
            host.OpenPopup->SetProperty("display", "none");
            host.OpenPopup = nullptr;
        }
        if (host.Document)
            if (auto* menu = host.Document->GetElementById("contextMenu"))
                menu->SetProperty("display", "none");
    }

    void ClearHierarchyDrop(Host& host)
    {
        if (host.HierarchyDropTarget && !host.HierarchyDropClass.empty())
        {
            host.HierarchyDropTarget->SetClass(host.HierarchyDropClass, false);
            host.HierarchyDropTarget->RemoveAttribute("data-hierarchy-drop");
        }
        host.HierarchyDropTarget = nullptr;
        host.HierarchyDropClass.clear();
    }

    struct HierarchyDrop
    {
        Rml::Element* Element{};
        std::string Target;
        std::string Placement;
    };

    HierarchyDrop ResolveHierarchyDrop(Host& host, Rml::Element* target,
                                       int mouseY)
    {
        if (auto* row = FindAttributeElement(target, "data-hierarchy-entity"))
        {
            const auto offset = row->GetAbsoluteOffset();
            const auto size = row->GetBox().GetSize();
            const float relative = size.y > 0
                ? (static_cast<float>(mouseY) - offset.y) / size.y : 0.5f;
            const char* placement = relative < 0.25f ? "before"
                : relative > 0.75f ? "after" : "inside";
            return {row, Attribute(row, "data-hierarchy-entity"), placement};
        }
        if (auto* root = FindHierarchyRoot(target))
            return {root, "", "root"};
        return {};
    }

    void UpdateHierarchyDrop(Host& host, Rml::Element* target, int mouseY)
    {
        const auto drop = ResolveHierarchyDrop(host, target, mouseY);
        const std::string dropClass = drop.Placement.empty()
            ? std::string{} : "hierarchy-drop-" + drop.Placement;
        if (drop.Element == host.HierarchyDropTarget &&
            dropClass == host.HierarchyDropClass) return;
        ClearHierarchyDrop(host);
        if (drop.Element)
        {
            host.HierarchyDropTarget = drop.Element;
            host.HierarchyDropClass = dropClass;
            drop.Element->SetClass(dropClass, true);
            drop.Element->SetAttribute("data-hierarchy-drop", drop.Placement);
        }
    }

    void ClearDockPreview()
    {
        if (DockDrag.PreviewElement)
        {
            DockDrag.PreviewElement->SetClass("split", false);
            DockDrag.PreviewElement->SetClass("merge", false);
            DockDrag.PreviewElement->SetProperty("display", "none");
        }
        if (DockDrag.InsertElement)
            DockDrag.InsertElement->SetProperty("display", "none");
        if (DockDrag.GuideElement)
        {
            DockDrag.GuideElement->SetClass("float", false);
            DockDrag.GuideElement->SetProperty("display", "none");
        }
        DockDrag.PreviewElement = nullptr;
        DockDrag.InsertElement = nullptr;
        DockDrag.GuideElement = nullptr;
        DockDrag.TargetHost = nullptr;
        DockDrag.Target.clear();
        DockDrag.Zone.clear();
        DockDrag.Index = -1;
    }

    void HideDockGhost()
    {
        if (DockGhostWindow) ShowWindow(DockGhostWindow, SW_HIDE);
    }

    void UpdateDockGhost(POINT screen)
    {
        if (!DockGhostWindow)
        {
            WNDCLASSW windowClass{};
            windowClass.lpfnWndProc = DockGhostProcedure;
            windowClass.hInstance = GetModuleHandleW(nullptr);
            windowClass.lpszClassName = DockGhostClass;
            if (!RegisterClassW(&windowClass) &&
                GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return;
            const auto title = PaneDisplayName(DockDrag.Pane);
            DockGhostWindow = CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED |
                    WS_EX_TRANSPARENT,
                DockGhostClass, title.c_str(), WS_POPUP,
                screen.x + 18, screen.y + 18,
                DockDrag.GhostWidth, DockDrag.GhostHeight,
                MainWindow, nullptr, GetModuleHandleW(nullptr), nullptr);
            if (!DockGhostWindow) return;
            SetLayeredWindowAttributes(DockGhostWindow, 0, 210, LWA_ALPHA);
        }
        else
        {
            const auto title = PaneDisplayName(DockDrag.Pane);
            SetWindowTextW(DockGhostWindow, title.c_str());
        }
        SetWindowPos(DockGhostWindow, HWND_TOPMOST,
            screen.x + 18, screen.y + 18,
            DockDrag.GhostWidth, DockDrag.GhostHeight,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    static void SetPreviewRect(Rml::Element* preview,
                               const char* left, const char* top,
                               const char* width, const char* height)
    {
        preview->SetProperty("left", left);
        preview->SetProperty("top", top);
        preview->SetProperty("width", width);
        preview->SetProperty("height", height);
        preview->SetProperty("display", "block");
    }

    static void SetPixelProperty(Rml::Element* element,
                                 const char* property, float value)
    {
        std::ostringstream text;
        text << std::fixed << std::setprecision(1) << value << "px";
        element->SetProperty(property, text.str());
    }

    static void SetNumberProperty(Rml::Element* element,
                                  const char* property, float value)
    {
        if (!element) return;
        std::ostringstream text;
        text << std::fixed << std::setprecision(6) << value;
        element->SetProperty(property, text.str());
    }

    static void SetPercentProperty(Rml::Element* element,
                                   const char* property, float value)
    {
        if (!element) return;
        std::ostringstream text;
        text << std::fixed << std::setprecision(4) << value << "%";
        element->SetProperty(property, text.str());
    }

    static bool IsHorizontalSplit(std::string_view orientation)
    {
        return orientation == "horizontal" || orientation == "x";
    }

    Rml::Element* DirectChildSplit(Rml::Element* part) const
    {
        return FindChildWithAttribute(part, "data-dock-split-node");
    }

    SplitterDragState::PreviewNode* FindSplitterPreviewNode(
        Rml::Element* splitElement)
    {
        const auto found = std::find_if(SplitterDrag.PreviewNodes.begin(),
            SplitterDrag.PreviewNodes.end(), [splitElement](const auto& node) {
                return node.SplitElement == splitElement;
            });
        return found == SplitterDrag.PreviewNodes.end() ? nullptr : &*found;
    }

    void CollectSplitterPreviewNodes(Rml::Element* splitElement)
    {
        if (!splitElement) return;
        auto* first = FindChildWithAttribute(
            splitElement, "data-dock-split-part", "first");
        auto* second = FindChildWithAttribute(
            splitElement, "data-dock-split-part", "second");
        auto* splitter = FindChildWithAttribute(
            splitElement, "data-dock-splitter-node");
        if (!first || !second || !splitter) return;
        const auto orientation = Attribute(
            splitter, "data-dock-splitter-orientation");
        const float ratio = FloatAttribute(
            splitElement, "data-dock-split-ratio", 0.5f);
        const auto offset = splitElement->GetAbsoluteOffset();
        const auto size = splitElement->GetBox().GetSize();
        const bool targetHorizontal = IsHorizontalSplit(SplitterDrag.Orientation);
        const float start = targetHorizontal ? offset.x : offset.y;
        const float extent = targetHorizontal ? size.x : size.y;
        SplitterDrag.PreviewNodes.push_back({
            splitElement, first, second, splitter,
            Attribute(splitter, "data-dock-splitter-node"), orientation,
            ratio, start + extent * ratio, ratio});
        CollectSplitterPreviewNodes(DirectChildSplit(first));
        CollectSplitterPreviewNodes(DirectChildSplit(second));
    }

    bool CanPreserveSplitterPositions(Rml::Element* splitElement,
                                      float axisStart, float axisEnd,
                                      float targetBoundary)
    {
        auto* node = FindSplitterPreviewNode(splitElement);
        if (!node) return true;
        const bool sameOrientation = IsHorizontalSplit(node->Orientation) ==
            IsHorizontalSplit(SplitterDrag.Orientation);
        if (sameOrientation)
        {
            const float extent = axisEnd - axisStart;
            if (extent <= 0.0f) return false;
            const float boundary = splitElement == SplitterDrag.SplitElement
                ? targetBoundary : node->Boundary;
            const float ratio = (boundary - axisStart) / extent;
            if (ratio < 0.05f || ratio > 0.95f) return false;
            return CanPreserveSplitterPositions(DirectChildSplit(node->FirstElement),
                       axisStart, boundary, targetBoundary) &&
                CanPreserveSplitterPositions(DirectChildSplit(node->SecondElement),
                       boundary, axisEnd, targetBoundary);
        }
        return CanPreserveSplitterPositions(DirectChildSplit(node->FirstElement),
                   axisStart, axisEnd, targetBoundary) &&
            CanPreserveSplitterPositions(DirectChildSplit(node->SecondElement),
                   axisStart, axisEnd, targetBoundary);
    }

    void ApplySplitterPreviewTree(Rml::Element* splitElement,
                                  float axisStart, float axisEnd,
                                  float targetBoundary)
    {
        auto* node = FindSplitterPreviewNode(splitElement);
        if (!node) return;
        const bool sameOrientation = IsHorizontalSplit(node->Orientation) ==
            IsHorizontalSplit(SplitterDrag.Orientation);
        if (sameOrientation)
        {
            const float extent = (std::max)(axisEnd - axisStart, 1.0f);
            const float requestedBoundary = splitElement == SplitterDrag.SplitElement
                ? targetBoundary : node->Boundary;
            node->PreviewRatio = std::clamp(
                (requestedBoundary - axisStart) / extent, 0.05f, 0.95f);
            const float boundary = axisStart + extent * node->PreviewRatio;
            SetNumberProperty(node->FirstElement, "flex-grow", node->PreviewRatio);
            SetNumberProperty(node->SecondElement, "flex-grow",
                1.0f - node->PreviewRatio);
            SetPercentProperty(node->SplitterElement,
                IsHorizontalSplit(node->Orientation) ? "left" : "top",
                node->PreviewRatio * 100.0f);
            ApplySplitterPreviewTree(DirectChildSplit(node->FirstElement),
                axisStart, boundary, targetBoundary);
            ApplySplitterPreviewTree(DirectChildSplit(node->SecondElement),
                boundary, axisEnd, targetBoundary);
            return;
        }
        node->PreviewRatio = node->InitialRatio;
        ApplySplitterPreviewTree(DirectChildSplit(node->FirstElement),
            axisStart, axisEnd, targetBoundary);
        ApplySplitterPreviewTree(DirectChildSplit(node->SecondElement),
            axisStart, axisEnd, targetBoundary);
    }

    void CalculateSplitterPreviewBounds()
    {
        const float extent = SplitterDrag.AxisEnd - SplitterDrag.AxisStart;
        const float initialBoundary = SplitterDrag.AxisStart +
            extent * SplitterDrag.InitialRatio;
        float minimum = SplitterDrag.AxisStart + extent * 0.05f;
        float maximum = SplitterDrag.AxisStart + extent * 0.95f;
        if (!CanPreserveSplitterPositions(
                SplitterDrag.SplitElement, SplitterDrag.AxisStart, SplitterDrag.AxisEnd,
                minimum))
        {
            float rejected = minimum;
            float accepted = initialBoundary;
            for (int iteration = 0; iteration < 24; ++iteration)
            {
                const float candidate = (rejected + accepted) * 0.5f;
                if (CanPreserveSplitterPositions(SplitterDrag.SplitElement,
                        SplitterDrag.AxisStart, SplitterDrag.AxisEnd, candidate))
                    accepted = candidate;
                else rejected = candidate;
            }
            minimum = accepted;
        }
        if (!CanPreserveSplitterPositions(SplitterDrag.SplitElement,
                SplitterDrag.AxisStart, SplitterDrag.AxisEnd, maximum))
        {
            float accepted = initialBoundary;
            float rejected = maximum;
            for (int iteration = 0; iteration < 24; ++iteration)
            {
                const float candidate = (accepted + rejected) * 0.5f;
                if (CanPreserveSplitterPositions(SplitterDrag.SplitElement,
                        SplitterDrag.AxisStart, SplitterDrag.AxisEnd, candidate))
                    accepted = candidate;
                else rejected = candidate;
            }
            maximum = accepted;
        }
        SplitterDrag.MinimumBoundary = minimum;
        SplitterDrag.MaximumBoundary = maximum;
    }

    void ApplySplitterPreview(float ratio)
    {
        const float extent = SplitterDrag.AxisEnd - SplitterDrag.AxisStart;
        const float boundary = std::clamp(
            SplitterDrag.AxisStart + extent * ratio,
            SplitterDrag.MinimumBoundary, SplitterDrag.MaximumBoundary);
        SplitterDrag.Ratio = extent > 0.0f
            ? (boundary - SplitterDrag.AxisStart) / extent
            : SplitterDrag.InitialRatio;
        ApplySplitterPreviewTree(SplitterDrag.SplitElement,
            SplitterDrag.AxisStart, SplitterDrag.AxisEnd, boundary);
    }

    void CancelSplitterDrag()
    {
        if (SplitterDrag.Active)
            ApplySplitterPreview(SplitterDrag.InitialRatio);
        if (SplitterDrag.Element)
        {
            SplitterDrag.Element->SetClass("dock-splitter-dragging", false);
            SplitterDrag.Element->RemoveAttribute("data-dock-resize-preview");
        }
        SplitterDrag = {};
    }

    void ShowDockGuide(Rml::Element* group, bool floating)
    {
        auto* guide = FindDescendantWithAttribute(
            group, "data-dock-preview", "guide");
        if (!guide) return;
        const auto groupSize = group->GetBox().GetSize();
        const float width = (std::max)(1.0f, groupSize.x);
        const float height = (std::max)(1.0f, groupSize.y - 34.0f);
        SetPixelProperty(guide, "left", 0.0f);
        SetPixelProperty(guide, "top", 34.0f);
        SetPixelProperty(guide, "width", width);
        SetPixelProperty(guide, "height", height);
        guide->SetClass("float", floating);
        guide->SetProperty("display", "block");

        const float dx = width * 0.25f;
        const float dy = height * 0.25f;
        const float length = std::sqrt(dx * dx + dy * dy);
        const float angle = std::atan2(dy, dx) * 180.0f /
            3.14159265358979323846f;
        struct LineLayout
        {
            const char* Id;
            float Left;
            float Top;
            float Angle;
        };
        const LineLayout lines[] = {
            {"nw", 0.0f, 0.0f, angle},
            {"ne", width * 0.75f, height * 0.25f, -angle},
            {"sw", 0.0f, height, -angle},
            {"se", width * 0.75f, height * 0.75f, angle},
        };
        for (const auto& line : lines)
        {
            auto* element = FindDescendantWithAttribute(
                guide, "data-dock-guide-line", line.Id);
            if (!element) continue;
            SetPixelProperty(element, "left", line.Left);
            SetPixelProperty(element, "top", line.Top);
            SetPixelProperty(element, "width", length);
            std::ostringstream transform;
            transform << std::fixed << std::setprecision(2)
                      << "rotate(" << line.Angle << "deg)";
            element->SetProperty("transform", transform.str());
        }
        DockDrag.GuideElement = guide;
    }

    void ShowSplitPreview(Rml::Element* group, std::string_view edge)
    {
        auto* preview = FindDescendantWithAttribute(
            group, "data-dock-preview", "result");
        if (!preview) return;
        preview->SetClass("split", true);
        preview->SetClass("merge", false);
        if (edge == "left") SetPreviewRect(preview, "0px", "0px", "25%", "100%");
        else if (edge == "right") SetPreviewRect(preview, "75%", "0px", "25%", "100%");
        else if (edge == "top") SetPreviewRect(preview, "0px", "0px", "100%", "25%");
        else SetPreviewRect(preview, "0px", "75%", "100%", "25%");
        DockDrag.PreviewElement = preview;
    }

    static int CalculateTabInsertion(Rml::Element* group,
                                     Rml::Element* tabBar,
                                     float mouseX, float& insertionX)
    {
        const auto groupOffset = group->GetAbsoluteOffset();
        int insertionIndex = 0;
        insertionX = tabBar->GetAbsoluteOffset().x - groupOffset.x + 6.0f;
        for (int childIndex = 0; childIndex < tabBar->GetNumChildren(); ++childIndex)
        {
            auto* child = tabBar->GetChild(childIndex);
            if (!child->HasAttribute("data-dock-pane")) continue;
            const auto offset = child->GetAbsoluteOffset();
            const auto size = child->GetBox().GetSize();
            if (mouseX < offset.x + size.x * 0.5f)
            {
                insertionX = offset.x - groupOffset.x;
                return insertionIndex;
            }
            insertionX = offset.x + size.x - groupOffset.x;
            ++insertionIndex;
        }
        return insertionIndex;
    }

    int ShowMergePreview(Rml::Element* group, Rml::Element* tabBar,
                         float mouseX)
    {
        auto* preview = FindDescendantWithAttribute(
            group, "data-dock-preview", "result");
        auto* insertion = FindDescendantWithAttribute(
            group, "data-dock-preview", "tab-insert");
        if (!preview || !insertion) return -1;
        preview->SetClass("merge", true);
        preview->SetClass("split", false);
        SetPreviewRect(preview, "0px", "0px", "100%", "100%");

        float insertionX = 0.0f;
        const int insertionIndex = CalculateTabInsertion(
            group, tabBar, mouseX, insertionX);
        std::ostringstream left;
        left << std::fixed << std::setprecision(1) << insertionX << "px";
        insertion->SetProperty("left", left.str());
        insertion->SetProperty("display", "block");
        DockDrag.PreviewElement = preview;
        DockDrag.InsertElement = insertion;
        return insertionIndex;
    }

    void CancelDockDrag()
    {
        ClearDockPreview();
        HideDockGhost();
        if (DockDrag.SourceElement)
            DockDrag.SourceElement->SetClass("dock-dragging", false);
        for (const auto& host : Hosts)
            if (host->Document)
                host->Document->SetClass("dock-dragging", false);
        DockDrag = {};
    }

    Host* HostAtScreenPoint(POINT point, POINT* clientPoint = nullptr)
    {
        const HWND root = GetAncestor(WindowFromPoint(point), GA_ROOT);
        for (const auto& host : Hosts)
        {
            if (!host->Window || !IsWindowVisible(host->Window) ||
                host->Window != root) continue;
            if (clientPoint)
            {
                *clientPoint = point;
                ScreenToClient(host->Window, clientPoint);
            }
            return host.get();
        }
        return nullptr;
    }

    void BeginPointerDrag(Host& host, Rml::Element* target, POINT screen)
    {
        if (auto* splitter = FindAttributeElement(
                target, "data-dock-splitter-node"))
        {
            SplitterDrag.HostWindow = &host;
            SplitterDrag.Element = splitter;
            SplitterDrag.SplitElement = FindAttributeElement(
                splitter, "data-dock-split-node");
            SplitterDrag.FirstElement = FindChildWithAttribute(
                SplitterDrag.SplitElement, "data-dock-split-part", "first");
            SplitterDrag.SecondElement = FindChildWithAttribute(
                SplitterDrag.SplitElement, "data-dock-split-part", "second");
            SplitterDrag.Node = Attribute(splitter, "data-dock-splitter-node");
            SplitterDrag.Orientation = Attribute(
                splitter, "data-dock-splitter-orientation");
            SplitterDrag.InitialRatio = FloatAttribute(
                SplitterDrag.SplitElement, "data-dock-split-ratio", 0.5f);
            SplitterDrag.Ratio = SplitterDrag.InitialRatio;
            SplitterDrag.Active = !SplitterDrag.Node.empty() &&
                SplitterDrag.SplitElement && SplitterDrag.FirstElement &&
                SplitterDrag.SecondElement;
            if (!SplitterDrag.Active)
            {
                SplitterDrag = {};
                return;
            }
            const auto offset = SplitterDrag.SplitElement->GetAbsoluteOffset();
            const auto size = SplitterDrag.SplitElement->GetBox().GetSize();
            const bool horizontal = IsHorizontalSplit(SplitterDrag.Orientation);
            SplitterDrag.AxisStart = horizontal ? offset.x : offset.y;
            SplitterDrag.AxisEnd = SplitterDrag.AxisStart +
                (horizontal ? size.x : size.y);
            CollectSplitterPreviewNodes(SplitterDrag.SplitElement);
            if (SplitterDrag.PreviewNodes.empty() ||
                SplitterDrag.AxisEnd <= SplitterDrag.AxisStart)
            {
                SplitterDrag = {};
                return;
            }
            CalculateSplitterPreviewBounds();
            splitter->SetClass("dock-splitter-dragging", true);
            SetCapture(host.Window);
            return;
        }
        auto* pane = FindAttributeElement(target, "data-dock-pane");
        if (!pane) return;
        DockDrag.SourceHost = &host;
        DockDrag.SourceElement = pane;
        DockDrag.StartScreen = screen;
        DockDrag.Pane = Attribute(pane, "data-dock-pane");
        DockDrag.Pending = !DockDrag.Pane.empty();
        if (DockDrag.Pending) SetCapture(host.Window);
    }

    void UpdateDockDrag(POINT screen)
    {
        if (!DockDrag.Pending && !DockDrag.Active) return;
        if (DockDrag.Pending)
        {
            const int thresholdX = GetSystemMetrics(SM_CXDRAG);
            const int thresholdY = GetSystemMetrics(SM_CYDRAG);
            if (std::abs(screen.x - DockDrag.StartScreen.x) < thresholdX &&
                std::abs(screen.y - DockDrag.StartScreen.y) < thresholdY) return;
            DockDrag.Pending = false;
            DockDrag.Active = true;
            if (DockDrag.SourceElement)
                DockDrag.SourceElement->SetClass("dock-dragging", true);
            if (auto* group = FindAttributeElement(
                    DockDrag.SourceElement, "data-dock-group"))
            {
                const auto size = group->GetBox().GetSize();
                DockDrag.GhostWidth = std::clamp(
                    static_cast<int>(size.x), 280, 560);
                DockDrag.GhostHeight = std::clamp(
                    static_cast<int>(size.y), 160, 360);
            }
            for (const auto& host : Hosts)
                if (host->Document &&
                    (DockDrag.Pane != "viewport" || host->Id == "main"))
                    host->Document->SetClass("dock-dragging", true);
        }
        UpdateDockGhost(screen);

        POINT client{};
        Host* targetHost = HostAtScreenPoint(screen, &client);
        DockDrag.InsideAnyHost = targetHost != nullptr;
        Rml::Element* group = nullptr;
        std::string zone;
        std::string target;
        int index = -1;
        if (targetHost && targetHost->Context)
        {
            auto* hit = targetHost->Context->GetElementAtPoint(
                {static_cast<float>(client.x), static_cast<float>(client.y)});
            group = FindAttributeElement(hit, "data-dock-group");
            if (group)
            {
                auto* tabBar = FindAttributeElement(hit, "data-dock-tabbar");
                if (tabBar)
                {
                    zone = "center";
                    target = Attribute(group, "data-dock-group");
                    float insertionX = 0.0f;
                    index = CalculateTabInsertion(group, tabBar,
                        static_cast<float>(client.x), insertionX);
                }
                else
                {
                    const auto offset = group->GetAbsoluteOffset();
                    const auto size = group->GetBox().GetSize();
                    const float bodyTop = offset.y + 34.0f;
                    const float bodyHeight = (std::max)(1.0f, size.y - 34.0f);
                    const float normalizedX = std::clamp(
                        (static_cast<float>(client.x) - offset.x) /
                            (std::max)(1.0f, size.x), 0.0f, 1.0f);
                    const float normalizedY = std::clamp(
                        (static_cast<float>(client.y) - bodyTop) /
                            bodyHeight, 0.0f, 1.0f);
                    const float distances[] = {
                        normalizedX, 1.0f - normalizedX,
                        normalizedY, 1.0f - normalizedY};
                    const auto nearest = std::min_element(
                        std::begin(distances), std::end(distances));
                    if (*nearest <= 0.22f)
                    {
                        static constexpr const char* edges[] = {
                            "left", "right", "top", "bottom"};
                        zone = edges[std::distance(std::begin(distances), nearest)];
                        target = Attribute(group, "data-dock-node");
                    }
                    else zone = "detach";
                }
            }
            else zone = "detach";
        }
        else zone = "detach";
        if (DockDrag.Pane == "viewport" &&
            (zone == "detach" || !targetHost || targetHost->Id != "main"))
        {
            group = nullptr;
            targetHost = nullptr;
            zone.clear();
            target.clear();
            index = -1;
        }
        if (targetHost == DockDrag.TargetHost && zone == DockDrag.Zone &&
            target == DockDrag.Target && index == DockDrag.Index) return;
        auto* preview = DockDrag.PreviewElement;
        auto* insertion = DockDrag.InsertElement;
        auto* guide = DockDrag.GuideElement;
        if (preview || insertion)
        {
            if (preview)
            {
                preview->SetClass("split", false);
                preview->SetClass("merge", false);
                preview->SetProperty("display", "none");
            }
            if (insertion) insertion->SetProperty("display", "none");
        }
        if (guide)
        {
            guide->SetClass("float", false);
            guide->SetProperty("display", "none");
        }
        DockDrag.PreviewElement = nullptr;
        DockDrag.InsertElement = nullptr;
        DockDrag.GuideElement = nullptr;
        DockDrag.TargetHost = zone == "detach" ? nullptr : targetHost;
        DockDrag.Zone = zone;
        DockDrag.Target = target;
        DockDrag.Index = index;
        if (group) ShowDockGuide(group, zone == "detach");
        if (zone == "center" && group)
        {
            auto* hit = targetHost->Context->GetElementAtPoint(
                {static_cast<float>(client.x), static_cast<float>(client.y)});
            auto* tabBar = FindAttributeElement(hit, "data-dock-tabbar");
            if (tabBar)
                DockDrag.Index = ShowMergePreview(group, tabBar,
                    static_cast<float>(client.x));
        }
        else if (zone != "detach" && !zone.empty() && group)
            ShowSplitPreview(group, zone);
    }

    void FinishDockDrag()
    {
        if (!DockDrag.Active)
        {
            CancelDockDrag();
            return;
        }
        Host* source = DockDrag.SourceHost;
        Rml::String action;
        if (DockDrag.TargetHost)
        {
            action = Rml::String("editor.dock-drop:") + DockDrag.Pane + ":" +
                source->Id + ":" + DockDrag.TargetHost->Id + ":" +
                DockDrag.Target + ":" + DockDrag.Zone + ":" +
                std::to_string(DockDrag.Index);
        }
        else if (DockDrag.Pane != "viewport" &&
                 (DockDrag.Zone == "detach" || !DockDrag.InsideAnyHost))
        {
            action = Rml::String("editor.dock-drop:") + DockDrag.Pane + ":" +
                source->Id + ":none:none:detach:-1";
        }
        CancelDockDrag();
        if (source && !action.empty()) QueueAction(*source, action);
    }

    float UpdateSplitterRatio(POINT screen)
    {
        if (!SplitterDrag.Active || !SplitterDrag.HostWindow ||
            !SplitterDrag.SplitElement) return 0.0f;
        POINT client = screen;
        ScreenToClient(SplitterDrag.HostWindow->Window, &client);
        const bool horizontal = SplitterDrag.Orientation == "horizontal" ||
            SplitterDrag.Orientation == "x";
        const float extent = SplitterDrag.AxisEnd - SplitterDrag.AxisStart;
        const float coordinate = static_cast<float>(horizontal ? client.x : client.y);
        const float requestedRatio = extent > 0.0f
            ? (coordinate - SplitterDrag.AxisStart) / extent
            : SplitterDrag.InitialRatio;
        ApplySplitterPreview(requestedRatio);
        if (SplitterDrag.Element)
        {
            std::ostringstream value;
            value << std::fixed << std::setprecision(6) << SplitterDrag.Ratio;
            SplitterDrag.Element->SetAttribute(
                "data-dock-resize-preview", value.str());
        }
        return SplitterDrag.Ratio;
    }

    void FinishSplitterDrag(POINT screen)
    {
        if (!SplitterDrag.Active) return;
        UpdateSplitterRatio(screen);
        auto* host = SplitterDrag.HostWindow;
        std::ostringstream action;
        if (host)
        {
            action << "editor.dock-resize:" << host->Id << ":";
            bool first = true;
            for (const auto& node : SplitterDrag.PreviewNodes)
            {
                if (IsHorizontalSplit(node.Orientation) !=
                    IsHorizontalSplit(SplitterDrag.Orientation)) continue;
                if (!first) action << ',';
                first = false;
                action << node.Node << '=' << std::fixed << std::setprecision(6)
                       << node.PreviewRatio;
            }
        }
        if (SplitterDrag.Element)
        {
            SplitterDrag.Element->SetClass("dock-splitter-dragging", false);
            SplitterDrag.Element->RemoveAttribute("data-dock-resize-preview");
        }
        SplitterDrag = {};
        if (host && !action.str().empty()) QueueAction(*host, action.str());
    }

    void EmitAction(Host& host, const Rml::String& action)
    {
        if (!Action) return;
        ++CallbackDepth;
        Action(host.Id.c_str(), action.c_str());
        --CallbackDepth;
    }

    void QueueAction(const Host& host, const Rml::String& action)
    {
        DeferredActions.emplace_back(host.Id, action);
    }

    void FlushDeferredActions()
    {
        auto actions = std::move(DeferredActions);
        DeferredActions.clear();
        for (const auto& [hostId, action] : actions)
            if (auto* host = FindHost(hostId)) EmitAction(*host, action);
    }

    void ProcessEvent(Host& host, Rml::Event& event)
    {
        Rml::Element* target = event.GetTargetElement();
        if (event.GetId() == Rml::EventId::Dragstart)
        {
            auto* source = FindAttributeElement(target, "data-hierarchy-entity");
            host.HierarchySource = Attribute(source, "data-hierarchy-entity");
            return;
        }
        if (event.GetId() == Rml::EventId::Dragover ||
            event.GetId() == Rml::EventId::Drag)
        {
            if (host.HierarchySource.empty()) return;
            const int x = event.GetParameter<int>("mouse_x", 0);
            const int y = event.GetParameter<int>("mouse_y", 0);
            auto* source = event.GetParameter<void*>("drag_element", nullptr);
            auto* hit = event.GetId() == Rml::EventId::Drag
                ? host.Context->GetElementAtPoint(
                    {static_cast<float>(x), static_cast<float>(y)},
                    static_cast<Rml::Element*>(source))
                : target;
            UpdateHierarchyDrop(host, hit, y);
            return;
        }
        if (event.GetId() == Rml::EventId::Dragdrop)
        {
            if (host.HierarchySource.empty()) return;
            const int y = event.GetParameter<int>("mouse_y", 0);
            const auto drop = ResolveHierarchyDrop(host, target, y);
            const auto source = host.HierarchySource;
            ClearHierarchyDrop(host);
            if (!drop.Placement.empty())
                QueueAction(host, Rml::String("editor.hierarchy-drop:") +
                    source + ":" + drop.Target + ":" + drop.Placement);
            return;
        }
        if (event.GetId() == Rml::EventId::Dragend)
        {
            ClearHierarchyDrop(host);
            host.HierarchySource.clear();
            return;
        }
        if (event.GetId() == Rml::EventId::Change && target)
        {
            const auto value = target->GetAttribute<Rml::String>("value", "");
            if (target->GetId() == "projectSearch" ||
                target->GetId() == "hierarchySearch")
            {
                EmitAction(host, Rml::String(target->GetId() == "projectSearch"
                    ? "editor.search-assets:" : "editor.search-hierarchy:") + value);
                return;
            }
            const auto changeAction = target->GetAttribute<Rml::String>(
                "data-change-action", "");
            if (!changeAction.empty())
            {
                EmitAction(host, changeAction + value);
                return;
            }
        }
        if (event.GetId() == Rml::EventId::Mouseup &&
            event.GetParameter<int>("button", -1) == 1)
        {
            for (auto* element = target; element; element = element->GetParentNode())
            {
                const auto kind = element->GetAttribute<Rml::String>(
                    "data-context-kind", "");
                if (kind.empty()) continue;
                const auto id = element->GetAttribute<Rml::String>(
                    "data-context-id", "");
                const auto source = element->GetAttribute<Rml::String>(
                    "data-context-source", "");
                const auto x = event.GetParameter<int>("mouse_x", 0);
                const auto y = event.GetParameter<int>("mouse_y", 0);
                HidePopups(host);
                const auto request = Rml::String("editor.context:") +
                    std::to_string(x) + ":" + std::to_string(y) + ":" +
                    kind + ":" + source + ":" + id;
                EmitAction(host, request);
                return;
            }
            return;
        }
        if (event.GetId() != Rml::EventId::Click) return;
        while (target)
        {
            const auto popupId = target->GetAttribute<Rml::String>(
                "data-popup-id", "");
            if (!popupId.empty())
            {
                auto* popup = host.Document
                    ? host.Document->GetElementById(popupId) : nullptr;
                const bool wasOpen = popup && popup == host.OpenPopup;
                HidePopups(host);
                if (popup && !wasOpen)
                {
                    popup->SetAttribute("open", true);
                    popup->SetProperty("display", "block");
                    host.OpenPopup = popup;
                }
                return;
            }
            auto action = target->GetAttribute<Rml::String>("data-action", "");
            if (action.empty())
                action = target->GetAttribute<Rml::String>("action", "");
            if (!action.empty())
            {
                HidePopups(host);
                if (action == "editor.new-project" ||
                    action == "editor.open-project" ||
                    action == "editor.command:core.new-project" ||
                    action == "editor.command:core.open-project")
                {
                    const bool create = action == "editor.new-project" ||
                        action == "editor.command:core.new-project";
                    const auto path = ChooseProjectPath(host.Window, create);
                    if (!path.empty())
                        EmitAction(host, Rml::String(create
                            ? "editor.create-project:" : "editor.open-project:") + path);
                    return;
                }
                EmitAction(host, action);
                std::cout << "[RmlUi] host=" << host.Id
                          << " action=" << action << std::endl;
                return;
            }
            target = target->GetParentNode();
        }
        HidePopups(host);
    }

    Rml::Element* FindUniqueElement(const char* id, Host** foundHost = nullptr)
    {
        if (!id) return nullptr;
        Rml::Element* result = nullptr;
        Host* owner = nullptr;
        for (const auto& host : Hosts)
        {
            if (!host->Document) continue;
            if (auto* element = host->Document->GetElementById(id))
            {
                if (result)
                {
                    std::cerr << "[RmlUi] element id is not unique across hosts: "
                              << id << std::endl;
                    return nullptr;
                }
                result = element;
                owner = host.get();
            }
        }
        if (foundHost) *foundHost = owner;
        return result;
    }

    bool DestroyHostNow(std::string_view id)
    {
        const auto found = std::find_if(Hosts.begin(), Hosts.end(),
            [id](const auto& host) { return host->Id == id; });
        if (found == Hosts.end() || !(*found)->OwnsWindow) return false;
        auto& host = **found;
        if (DockDrag.SourceHost == &host || DockDrag.TargetHost == &host)
            CancelDockDrag();
        if (SplitterDrag.HostWindow == &host)
            CancelSplitterDrag();
        SetCurrentHost(host);
        if (host.Context)
        {
            Rml::RemoveContext(host.ContextName);
            host.Context = nullptr;
            host.Document = nullptr;
        }
        if (auto* main = FindMainHost(); main && main->Dc)
            wglMakeCurrent(main->Dc, Gl);
        if (host.Window)
        {
            if (host.OriginalProcedure)
                SetWindowLongPtrW(host.Window, GWLP_WNDPROC,
                    reinterpret_cast<LONG_PTR>(host.OriginalProcedure));
            RemovePropW(host.Window, HostProperty);
            if (host.Dc) ReleaseDC(host.Window, host.Dc);
            DestroyWindow(host.Window);
        }
        std::cout << "[RmlUi] dock host destroyed id=" << host.Id << std::endl;
        Hosts.erase(found);
        return true;
    }

    void FlushPendingDestroy()
    {
        if (CallbackDepth != 0) return;
        for (size_t index = Hosts.size(); index > 0; --index)
            if (Hosts[index - 1]->PendingDestroy)
                DestroyHostNow(Hosts[index - 1]->Id);
    }
};

void RmlUiShell::Impl::Host::ProcessEvent(Rml::Event& event)
{
    Owner->ProcessEvent(*this, event);
}

RmlUiShell::RmlUiShell() : State(std::make_unique<Impl>()) {}
RmlUiShell::~RmlUiShell() { Shutdown(); }

bool RmlUiShell::Initialize(HWND window, const std::filesystem::path& engineRoot)
{
    auto& state = *State;
    if (state.Ready || !window) return false;
    state.MainWindow = window;
    state.EngineRoot = engineRoot;
    state.ComInitialized = SUCCEEDED(
        CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED));

    auto host = std::make_unique<Impl::Host>();
    host->Owner = &state;
    host->Id = "main";
    host->ContextName = "editor.main";
    host->Window = window;
    host->Dc = GetDC(window);
    if (!host->Dc) return false;
    state.Hosts.push_back(std::move(host));
    auto& main = *state.Hosts.back();

    auto& format = state.PixelFormatDescription;
    format.nSize = sizeof(format);
    format.nVersion = 1;
    format.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    format.iPixelType = PFD_TYPE_RGBA;
    format.cColorBits = 32;
    format.cDepthBits = 24;
    format.cStencilBits = 8;
    state.PixelFormat = ChoosePixelFormat(main.Dc, &format);
    if (!state.PixelFormat ||
        !SetPixelFormat(main.Dc, state.PixelFormat, &format)) return false;
    state.Gl = wglCreateContext(main.Dc);
    if (!state.Gl || !wglMakeCurrent(main.Dc, state.Gl)) return false;

    state.System.SetWindow(window);
    Rml::SetSystemInterface(&state.System);
    Rml::SetRenderInterface(&state.Renderer);
    Rml::SetFileInterface(&state.Files);
    if (!Rml::Initialise()) return false;
    state.Ready = true;
    Rml::Factory::RegisterElementInstancer("viewport", &GViewportInstancer);

    RECT client{};
    GetClientRect(window, &client);
    main.Context = Rml::CreateContext(main.ContextName,
        {client.right, client.bottom}, &state.Renderer, &main.Ime);
    if (!main.Context) return false;
    const bool regular = Rml::LoadFontFace("C:/Windows/Fonts/segoeui.ttf");
    Rml::LoadFontFace("C:/Windows/Fonts/segoeuib.ttf");
    Rml::LoadFontFace("C:/Windows/Fonts/msyh.ttc", true);
    if (!regular)
        Rml::LoadFontFace(Utf8File(engineRoot /
            "External/RmlUi/Samples/assets/LatoLatin-Regular.ttf"));
    const auto documentPath = Utf8File(engineRoot / "Content/UI/Rml/Editor.rml");
    main.Document = main.Context->LoadDocument(documentPath);
    if (!main.Document) return false;
    main.Document->AddEventListener(Rml::EventId::Click, &main);
    main.Document->AddEventListener(Rml::EventId::Change, &main);
    main.Document->AddEventListener(Rml::EventId::Mouseup, &main);
    main.Document->AddEventListener(Rml::EventId::Dragstart, &main, true);
    main.Document->AddEventListener(Rml::EventId::Dragover, &main, true);
    main.Document->AddEventListener(Rml::EventId::Dragdrop, &main, true);
    main.Document->AddEventListener(Rml::EventId::Dragend, &main, true);
    main.Document->AddEventListener(Rml::EventId::Drag, &main, true);
    main.Document->Show();
    main.Context->Update();
    SetPropW(window, HostProperty, &main);
    main.OriginalProcedure = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(
        window, GWLP_WNDPROC,
        reinterpret_cast<LONG_PTR>(&RmlUiShell::WindowProcedure)));
    std::cout << "[RmlUi] version=6.1 context=editor.main document="
              << documentPath << std::endl;
    return true;
}

HWND RmlUiShell::CreateDockWindow(std::string_view hostId,
                                  const char* titleUtf8)
{
    auto& state = *State;
    if (!state.Ready || !IsValidHostId(hostId) || hostId == "main") return nullptr;
    if (auto* existing = state.FindHost(hostId))
    {
        if (titleUtf8) SetWindowTextW(existing->Window, Utf8ToWide(titleUtf8).c_str());
        ShowWindow(existing->Window, SW_SHOW);
        return existing->Window;
    }

    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = DockWindowProcedure;
    windowClass.style = CS_DBLCLKS | CS_OWNDC;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = DockWindowClass;
    if (!RegisterClassW(&windowClass) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
        return nullptr;

    auto host = std::make_unique<Impl::Host>();
    host->Owner = &state;
    host->Id.assign(hostId);
    host->ContextName = "editor.dock." + host->Id;
    host->OwnsWindow = true;
    RECT mainBounds{};
    GetWindowRect(state.MainWindow, &mainBounds);
    const std::wstring title = Utf8ToWide(titleUtf8);
    host->Window = CreateWindowExW(
        WS_EX_TOOLWINDOW, DockWindowClass,
        title.empty() ? L"SharpEngine" : title.c_str(),
        WS_OVERLAPPEDWINDOW, mainBounds.left + 64, mainBounds.top + 64,
        720, 520, state.MainWindow, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!host->Window) return nullptr;
    const COLORREF noBorder = DWMWA_COLOR_NONE;
    DwmSetWindowAttribute(host->Window, DWMWA_BORDER_COLOR,
                          &noBorder, sizeof(noBorder));
    host->Dc = GetDC(host->Window);
    if (!host->Dc || !SetPixelFormat(host->Dc, state.PixelFormat,
                                     &state.PixelFormatDescription))
    {
        if (host->Dc) ReleaseDC(host->Window, host->Dc);
        DestroyWindow(host->Window);
        return nullptr;
    }
    if (!state.SetCurrentHost(*host))
    {
        ReleaseDC(host->Window, host->Dc);
        DestroyWindow(host->Window);
        return nullptr;
    }
    RECT client{};
    GetClientRect(host->Window, &client);
    host->Context = Rml::CreateContext(host->ContextName,
        {client.right, client.bottom}, &state.Renderer, &host->Ime);
    if (!host->Context)
    {
        if (auto* main = state.FindMainHost(); main && main->Dc)
            wglMakeCurrent(main->Dc, state.Gl);
        ReleaseDC(host->Window, host->Dc);
        DestroyWindow(host->Window);
        return nullptr;
    }
    const auto documentPath = Utf8File(
        state.EngineRoot / "Content/UI/Rml/DockWindow.rml");
    host->Document = host->Context->LoadDocument(documentPath);
    if (!host->Document)
    {
        Rml::RemoveContext(host->ContextName);
        if (auto* main = state.FindMainHost(); main && main->Dc)
            wglMakeCurrent(main->Dc, state.Gl);
        ReleaseDC(host->Window, host->Dc);
        DestroyWindow(host->Window);
        return nullptr;
    }
    host->Document->AddEventListener(Rml::EventId::Click, host.get());
    host->Document->AddEventListener(Rml::EventId::Change, host.get());
    host->Document->AddEventListener(Rml::EventId::Mouseup, host.get());
    host->Document->AddEventListener(Rml::EventId::Dragstart, host.get(), true);
    host->Document->AddEventListener(Rml::EventId::Dragover, host.get(), true);
    host->Document->AddEventListener(Rml::EventId::Dragdrop, host.get(), true);
    host->Document->AddEventListener(Rml::EventId::Dragend, host.get(), true);
    host->Document->AddEventListener(Rml::EventId::Drag, host.get(), true);
    host->Document->Show();
    host->Context->Update();
    SetPropW(host->Window, HostProperty, host.get());
    host->OriginalProcedure = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(
        host->Window, GWLP_WNDPROC,
        reinterpret_cast<LONG_PTR>(&RmlUiShell::WindowProcedure)));
    const HWND result = host->Window;
    state.Hosts.push_back(std::move(host));
    ShowWindow(result, SW_SHOW);
    UpdateWindow(result);
    std::cout << "[RmlUi] dock host created id=" << hostId
              << " document=" << documentPath << std::endl;
    return result;
}

bool RmlUiShell::DestroyDockWindow(std::string_view hostId)
{
    auto& state = *State;
    auto* host = state.FindHost(hostId);
    if (!host || !host->OwnsWindow) return false;
    if (state.CallbackDepth)
    {
        host->PendingDestroy = true;
        ShowWindow(host->Window, SW_HIDE);
        return true;
    }
    return state.DestroyHostNow(hostId);
}

void RmlUiShell::Shutdown()
{
    auto& state = *State;
    state.CancelDockDrag();
    if (state.DockGhostWindow)
    {
        DestroyWindow(state.DockGhostWindow);
        state.DockGhostWindow = nullptr;
    }
    state.CallbackDepth = 0;
    while (state.Hosts.size() > 1)
        state.DestroyHostNow(state.Hosts.back()->Id);
    HDC mainDc = nullptr;
    HWND mainWindow = nullptr;
    if (!state.Hosts.empty())
    {
        auto& main = *state.Hosts.front();
        mainDc = main.Dc;
        mainWindow = main.Window;
        state.SetCurrentHost(main);
        if (main.Context)
        {
            Rml::RemoveContext(main.ContextName);
            main.Context = nullptr;
            main.Document = nullptr;
        }
        if (main.Window && main.OriginalProcedure)
            SetWindowLongPtrW(main.Window, GWLP_WNDPROC,
                reinterpret_cast<LONG_PTR>(main.OriginalProcedure));
        if (main.Window) RemovePropW(main.Window, HostProperty);
    }
    state.Action = {};
    state.Pointer = {};
    state.ViewportInput = {};
    state.ViewportKey = {};
    if (state.Ready)
    {
        Rml::Shutdown();
        state.Ready = false;
    }
    if (mainDc && mainWindow) ReleaseDC(mainWindow, mainDc);
    state.Hosts.clear();
    if (state.Gl)
    {
        wglMakeCurrent(nullptr, nullptr);
        wglDeleteContext(state.Gl);
        state.Gl = nullptr;
    }
    if (state.ComInitialized)
    {
        CoUninitialize();
        state.ComInitialized = false;
    }
    state.MainWindow = nullptr;
}

LRESULT CALLBACK RmlUiShell::WindowProcedure(HWND window, UINT message,
                                             WPARAM wParam, LPARAM lParam)
{
    auto* host = reinterpret_cast<Impl::Host*>(GetPropW(window, HostProperty));
    if (!host || !host->Owner)
        return DefWindowProcW(window, message, wParam, lParam);
    auto& state = *host->Owner;
    struct WindowProcedureScope
    {
        Impl& State;
        explicit WindowProcedureScope(Impl& state) : State(state)
        {
            ++State.WindowProcedureDepth;
        }
        ~WindowProcedureScope()
        {
            if (--State.WindowProcedureDepth == 0)
                State.FlushDeferredActions();
        }
    } procedureScope(state);
    state.System.SetWindow(window);
    const auto callOriginal = [&]() {
        return host->OriginalProcedure
            ? CallWindowProcW(host->OriginalProcedure, window, message, wParam, lParam)
            : DefWindowProcW(window, message, wParam, lParam);
    };

    if (host->OwnsWindow && message == WM_CLOSE)
    {
        ShowWindow(window, SW_HIDE);
        state.EmitAction(*host, "window.closed");
        return 0;
    }
    if (!host->OwnsWindow && message == WM_CLOSE)
    {
        state.EmitAction(*host, "window.close-requested");
        return 0;
    }
    if (message == WM_NCHITTEST)
    {
        const LRESULT hit = callOriginal();
        if (hit != HTCLIENT) return hit;
        POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
        ScreenToClient(window, &point);
        for (auto* element = host->Context->GetElementAtPoint(
                 {static_cast<float>(point.x), static_cast<float>(point.y)});
             element; element = element->GetParentNode())
        {
            if (element->GetTagName() == "button" ||
                element->GetTagName() == "input" ||
                element->HasAttribute("action") ||
                element->HasAttribute("data-action")) return HTCLIENT;
            if (element->GetId() == "menubar" ||
                element->GetId() == "titlebar") return HTCAPTION;
        }
        return HTCLIENT;
    }
    if (message == WM_ERASEBKGND) return 1;
    if (message == WM_PAINT)
    {
        PAINTSTRUCT paint{};
        BeginPaint(window, &paint);
        EndPaint(window, &paint);
        return 0;
    }
    if (message == WM_KEYDOWN &&
        (GetKeyState(VK_CONTROL) & 0x8000) != 0 &&
        (wParam == 'Z' || wParam == 'Y'))
    {
        bool textInputFocused = false;
        for (auto* element = host->Context->GetFocusElement(); element;
             element = element->GetParentNode())
        {
            const auto& tag = element->GetTagName();
            if (tag == "input" || tag == "select" || tag == "textarea")
            {
                textInputFocused = true;
                break;
            }
        }
        if (!textInputFocused)
        {
            const bool redo = wParam == 'Y' ||
                ((GetKeyState(VK_SHIFT) & 0x8000) != 0 && wParam == 'Z');
            state.EmitAction(*host, redo ? "editor.redo" : "editor.undo");
            return 0;
        }
    }
    if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN ||
        message == WM_KEYUP || message == WM_SYSKEYUP ||
        message == WM_KILLFOCUS)
    {
        bool viewportFocused = false;
        if (message != WM_KILLFOCUS)
            for (auto* element = host->Context->GetFocusElement(); element;
                 element = element->GetParentNode())
                if (element->GetId() == "viewportCanvas")
                {
                    viewportFocused = true;
                    break;
                }
        if (state.ViewportKey)
        {
            const bool pressed = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            state.ViewportKey(message == WM_KILLFOCUS
                ? 0u : static_cast<uint32_t>(wParam), pressed, viewportFocused);
        }
    }
    const bool buttonMessage = message == WM_LBUTTONDOWN ||
        message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN ||
        message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
        message == WM_MBUTTONUP;
    if (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN ||
        message == WM_MBUTTONDOWN) SetCapture(window);
    if (buttonMessage || message == WM_MOUSEMOVE || message == WM_MOUSEWHEEL)
    {
        int x = static_cast<short>(LOWORD(lParam));
        int y = static_cast<short>(HIWORD(lParam));
        if (message == WM_MOUSEWHEEL)
        {
            POINT point{GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)};
            ScreenToClient(window, &point);
            x = point.x;
            y = point.y;
        }
        host->Context->ProcessMouseMove(x, y, state.ModifierState());
        POINT screen{x, y};
        ClientToScreen(window, &screen);
        bool viewport = false;
        Rml::Element* viewportElement = nullptr;
        for (auto* element = host->Context->GetHoverElement(); element;
             element = element->GetParentNode())
            if (element->GetId() == "viewportCanvas")
            {
                viewport = true;
                viewportElement = element;
                break;
            }
        const bool buttonDown = message == WM_LBUTTONDOWN ||
            message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN;
        if (buttonDown && viewport)
            state.ViewportCaptureWindow = window;
        if (message == WM_LBUTTONDOWN)
        {
            state.BeginPointerDrag(*host, host->Context->GetHoverElement(), screen);
        }
        if (message == WM_MOUSEMOVE)
        {
            if (state.SplitterDrag.Active) state.UpdateSplitterRatio(screen);
            else state.UpdateDockDrag(screen);
        }
        if (message == WM_LBUTTONUP)
        {
            if (state.SplitterDrag.Active) state.FinishSplitterDrag(screen);
            else
            {
                state.UpdateDockDrag(screen);
                state.FinishDockDrag();
            }
        }
        const bool captured = state.ViewportCaptureWindow == window;
        if (viewport || captured)
        {
            uint32_t buttons = static_cast<uint32_t>(wParam) &
                (MK_LBUTTON | MK_RBUTTON | MK_MBUTTON | MK_SHIFT | MK_CONTROL);
            if (message == WM_LBUTTONUP) buttons |= MK_LBUTTON;
            if (message == WM_RBUTTONUP) buttons |= MK_RBUTTON;
            if (message == WM_MBUTTONUP) buttons |= MK_MBUTTON;
            uint32_t inputEvent = 0;
            if (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN ||
                message == WM_MBUTTONDOWN) inputEvent = 1;
            else if (message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
                message == WM_MBUTTONUP) inputEvent = 2;
            else if (message == WM_MOUSEWHEEL) inputEvent = 3;
            const int32_t wheel = message == WM_MOUSEWHEEL
                ? GET_WHEEL_DELTA_WPARAM(wParam) : 0;
            if (state.ViewportInput)
                state.ViewportInput(x, y, inputEvent, buttons, wheel);
            if (message == WM_LBUTTONDOWN && viewport)
            {
                if (viewportElement) viewportElement->Focus();
                if (state.Pointer) state.Pointer(x, y);
            }
        }
        if ((message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
             message == WM_MBUTTONUP) &&
            !(static_cast<uint32_t>(wParam) &
              (MK_LBUTTON | MK_RBUTTON | MK_MBUTTON)))
            state.ViewportCaptureWindow = nullptr;
    }
    if (message == WM_LBUTTONUP || message == WM_RBUTTONUP ||
        message == WM_MBUTTONUP) ReleaseCapture();
    if (message == WM_CAPTURECHANGED &&
        reinterpret_cast<HWND>(lParam) != window)
    {
        state.ViewportCaptureWindow = nullptr;
        state.CancelDockDrag();
        state.CancelSplitterDrag();
    }
    bool propagate = true;
    if (state.InjectedModifiers >= 0 &&
        (buttonMessage || message == WM_MOUSEMOVE || message == WM_MOUSEWHEEL))
    {
        const int modifiers = state.InjectedModifiers;
        if (message == WM_LBUTTONDOWN) propagate = host->Context->ProcessMouseButtonDown(0, modifiers);
        else if (message == WM_RBUTTONDOWN) propagate = host->Context->ProcessMouseButtonDown(1, modifiers);
        else if (message == WM_MBUTTONDOWN) propagate = host->Context->ProcessMouseButtonDown(2, modifiers);
        else if (message == WM_LBUTTONUP) propagate = host->Context->ProcessMouseButtonUp(0, modifiers);
        else if (message == WM_RBUTTONUP) propagate = host->Context->ProcessMouseButtonUp(1, modifiers);
        else if (message == WM_MBUTTONUP) propagate = host->Context->ProcessMouseButtonUp(2, modifiers);
        else if (message == WM_MOUSEWHEEL)
            propagate = host->Context->ProcessMouseWheel(
                {state.InjectedWheelX, state.InjectedWheelY}, modifiers);
    }
    else
        propagate = RmlWin32::WindowProcedure(
            host->Context, host->Ime, window, message, wParam, lParam);
    if (!propagate) return 0;
    return callOriginal();
}

bool RmlUiShell::Render()
{
    auto& state = *State;
    if (!state.Ready) return false;
    state.FlushPendingDestroy();
    bool presented = true;
    Impl::FrameSet frame;
    frame.FrameId = state.FrameId + 1;
    for (const auto& hostPointer : state.Hosts)
    {
        auto& host = *hostPointer;
        if (!host.Context || !host.Window ||
            (host.OwnsWindow && !IsWindowVisible(host.Window))) continue;
        RECT client{};
        GetClientRect(host.Window, &client);
        if (client.right <= 0 || client.bottom <= 0) continue;
        if (!state.SetCurrentHost(host))
        {
            std::cerr << "[RmlUi] could not bind OpenGL context for host="
                      << host.Id << std::endl;
            presented = false;
            continue;
        }
        host.Context->SetDimensions({client.right, client.bottom});
        host.Context->Update();
        state.Renderer.SetViewport(client.right, client.bottom);
        state.Renderer.DrawCalls = 0;
        state.Renderer.BeginFrame();
        state.Renderer.Clear();
        ViewportElement::RenderTarget = host.Id == "main" ? this : nullptr;
        host.Context->Render();
        ViewportElement::RenderTarget = nullptr;
        state.Renderer.EndFrame();
        const bool hostPresented = SwapBuffers(host.Dc) != FALSE;
        presented = presented && hostPresented;
        if (hostPresented) frame.Hosts.push_back(Impl::FrameHostMetadata(host));
        if (hostPresented && !host.Reported)
        {
            std::cout << "[RmlUi] first-frame presented host=" << host.Id
                      << " ui-draw-calls=" << state.Renderer.DrawCalls;
            if (host.Id == "main")
            {
                UiViewportRect viewport{};
                GetViewport(viewport);
                std::cout << " viewport=" << viewport.X << "," << viewport.Y
                          << "," << viewport.Width << "," << viewport.Height
                          << " scene-triangles=" << state.Triangles.size() / 3
                          << " scene-lines=" << state.Lines.size() / 2;
            }
            std::cout << std::endl;
            host.Reported = true;
        }
    }
    if (presented && !frame.Hosts.empty())
    {
        state.FrameId = frame.FrameId;
        state.CurrentFrame = std::move(frame);
        state.RefreshSemanticUi();
        state.CurrentFrame.StateRevision = state.StateRevision;
    }
    return presented;
}

void RmlUiShell::RenderViewport(const UiViewportRect& viewport)
{
    auto& state = *State;
    auto* host = state.FindMainHost();
    if (!host || (state.Triangles.empty() && state.Lines.empty())) return;
    RECT client{};
    GetClientRect(host->Window, &client);
    glPushAttrib(GL_ALL_ATTRIB_BITS);
    glPushClientAttrib(GL_CLIENT_ALL_ATTRIB_BITS);
    glMatrixMode(GL_PROJECTION);
    glPushMatrix();
    glMatrixMode(GL_MODELVIEW);
    glPushMatrix();
    glViewport(static_cast<int>(viewport.X),
        client.bottom - static_cast<int>(viewport.Y + viewport.Height),
        static_cast<int>(viewport.Width), static_cast<int>(viewport.Height));
    glEnable(GL_SCISSOR_TEST);
    glScissor(static_cast<int>(viewport.X),
        client.bottom - static_cast<int>(viewport.Y + viewport.Height),
        static_cast<int>(viewport.Width), static_cast<int>(viewport.Height));
    glDisable(GL_TEXTURE_2D);
    glDisable(GL_BLEND);
    glDisable(GL_STENCIL_TEST);
    glClearColor(0.055f, 0.065f, 0.08f, 1);
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
    glMatrixMode(GL_PROJECTION);
    glLoadIdentity();
    glOrtho(0, viewport.Width, viewport.Height, 0, 0, -1);
    glMatrixMode(GL_MODELVIEW);
    glLoadIdentity();
    glEnable(GL_DEPTH_TEST);
    glDepthFunc(GL_LEQUAL);
    glEnable(GL_POLYGON_OFFSET_FILL);
    glPolygonOffset(1.0f, 1.0f);
    glBegin(GL_TRIANGLES);
    for (const auto& vertex : state.Triangles)
    {
        glColor4f(vertex.R, vertex.G, vertex.B, vertex.A);
        glVertex3f(vertex.X, vertex.Y, vertex.Z);
    }
    glEnd();
    glDisable(GL_POLYGON_OFFSET_FILL);
    glBegin(GL_LINES);
    for (const auto& vertex : state.Lines)
    {
        glColor4f(vertex.R, vertex.G, vertex.B, vertex.A);
        glVertex3f(vertex.X, vertex.Y, vertex.Z);
    }
    glEnd();
    glDisable(GL_DEPTH_TEST);
    glDisable(GL_SCISSOR_TEST);
    glMatrixMode(GL_MODELVIEW);
    glPopMatrix();
    glMatrixMode(GL_PROJECTION);
    glPopMatrix();
    glPopClientAttrib();
    glPopAttrib();
}

void RmlUiShell::SetText(const char* id, const char* utf8)
{
    if (!id || !utf8) return;
    if (auto* element = State->FindUniqueElement(id))
    {
        const Rml::String text = Rml::StringUtilities::EncodeRml(utf8);
        if (element->GetInnerRML() != text) element->SetInnerRML(text);
    }
}

void RmlUiShell::SetMarkup(const char* id, const char* markup)
{
    if (!id || !markup) return;
    Impl::Host* host = nullptr;
    if (auto* element = State->FindUniqueElement(id, &host))
    {
        if (host && (strcmp(id, "topMenus") == 0 ||
            strcmp(id, "toolbarLeft") == 0 ||
            strcmp(id, "toolbarCenter") == 0 ||
            strcmp(id, "toolbarRight") == 0)) host->OpenPopup = nullptr;
        element->SetInnerRML(markup);
    }
}

bool RmlUiShell::SetHostMarkup(std::string_view hostId, const char* elementId,
                               const char* markup)
{
    if (!elementId || !markup) return false;
    auto* host = State->FindHost(hostId);
    if (!host || !host->Document) return false;
    auto* element = host->Document->GetElementById(elementId);
    if (!element) return false;
    element->SetInnerRML(markup);
    return true;
}

void RmlUiShell::SetProperty(const char* id, const char* name, const char* value)
{
    if (!id || !name || !value) return;
    if (auto* element = State->FindUniqueElement(id))
        element->SetProperty(name, value);
}

HWND RmlUiShell::GetHostWindow(std::string_view hostId) const
{
    if (auto* host = State->FindHost(hostId)) return host->Window;
    return nullptr;
}

bool RmlUiShell::GetViewport(UiViewportRect& rect) const
{
    const auto* host = State->FindMainHost();
    if (!host || !host->Document) return false;
    if (auto* element = host->Document->GetElementById("viewportCanvas"))
    {
        const auto position = element->GetAbsoluteOffset();
        const auto size = element->GetBox().GetSize();
        rect = {position.x, position.y, size.x, size.y};
        return true;
    }
    return false;
}

void RmlUiShell::SubmitScene(const UiViewportRect& rect,
                             const UiSceneVertex* triangles,
                             int triangleVertices,
                             const UiSceneVertex* lines,
                             int lineVertices)
{
    auto& state = *State;
    state.SceneRect = rect;
    if (triangles && triangleVertices > 0)
        state.Triangles.assign(triangles, triangles + triangleVertices);
    else state.Triangles.clear();
    if (lines && lineVertices > 0)
        state.Lines.assign(lines, lines + lineVertices);
    else state.Lines.clear();
}

void RmlUiShell::SetActionHandler(
    std::function<void(const char*, const char*)> handler)
{
    State->Action = std::move(handler);
}
void RmlUiShell::SetPointerHandler(std::function<void(int, int)> handler)
{
    State->Pointer = std::move(handler);
}
void RmlUiShell::SetViewportInputHandler(
    std::function<void(int, int, uint32_t, uint32_t, int32_t)> handler)
{
    State->ViewportInput = std::move(handler);
}
void RmlUiShell::SetViewportKeyHandler(
    std::function<void(uint32_t, bool, bool)> handler)
{
    State->ViewportKey = std::move(handler);
}
bool RmlUiShell::IsInitialized() const { return State->Ready; }

std::string RmlUiShell::DescribeUi(uint64_t layoutRevision,
                                   uint64_t documentRevision)
{
    auto& state = *State;
    if (!state.Ready || state.FrameId == 0 || state.LastSemanticUi.empty())
    {
        return "{\"schemaVersion\":1,\"stateRevision\":" +
            std::to_string(state.StateRevision) +
            ",\"layoutRevision\":" + std::to_string(layoutRevision) +
            ",\"documentRevision\":" + std::to_string(documentRevision) +
            ",\"frameId\":0,\"hosts\":[],\"diagnostics\":[{\"code\":\"UI_SNAPSHOT_UNAVAILABLE\",\"severity\":\"error\",\"objectId\":null,\"fieldPath\":null,\"expected\":\"one completed presentation\",\"actual\":\"none\",\"stateRevision\":" +
            std::to_string(state.StateRevision) +
            ",\"retryable\":true,\"message\":\"No completed RmlUi frame is available\"}]}";
    }
    state.PinCurrentFrame();
    return "{\"schemaVersion\":1,\"stateRevision\":" +
        std::to_string(state.StateRevision) +
        ",\"layoutRevision\":" + std::to_string(layoutRevision) +
        ",\"documentRevision\":" + std::to_string(documentRevision) +
        ",\"frameId\":" + std::to_string(state.FrameId) + ',' +
        state.LastSemanticUi.substr(1);
}

std::string RmlUiShell::PointerInput(std::string_view hostId, uint32_t event,
                                     int32_t x, int32_t y, uint32_t button,
                                     uint32_t buttons, uint32_t modifiers,
                                     uint32_t pointerId,
                                     uint64_t expectedFrameId,
                                     float wheelX, float wheelY,
                                     std::string_view expectedElementId)
{
    auto& state = *State;
    const auto failure = [&](std::string_view code, std::string_view field,
                             std::string_view expected, std::string_view actual,
                             bool retryable) {
        return "{\"accepted\":false,\"beforeStateRevision\":" +
            std::to_string(state.StateRevision) +
            ",\"afterStateRevision\":" + std::to_string(state.StateRevision) +
            ",\"frameId\":" + std::to_string(state.FrameId) +
            ",\"targetElementId\":null,\"eventPath\":[],\"capture\":null,\"diagnostics\":[{\"code\":" +
            JsonString(code) + ",\"severity\":\"error\",\"objectId\":" +
            JsonString(hostId) + ",\"fieldPath\":" + JsonString(field) +
            ",\"expected\":" + JsonString(expected) + ",\"actual\":" +
            JsonString(actual) + ",\"stateRevision\":" +
            std::to_string(state.StateRevision) + ",\"retryable\":" +
            (retryable ? "true" : "false") + "}]}";
    };
    if (pointerId != 0)
        return failure("UI_POINTER_SEQUENCE_INVALID", "pointerId", "0",
                       std::to_string(pointerId), false);
    auto* host = state.FindHost(hostId);
    if (!host || !host->Context || !host->Window)
        return failure("UI_HOST_NOT_FOUND", "hostId", "a live host",
                       std::string(hostId), true);
    const auto* expectedFrame = state.FindFrameSet(expectedFrameId);
    const bool continuingCapture = state.McpButtons != 0 &&
        ((state.SplitterDrag.Active && state.SplitterDrag.HostWindow == host) ||
         ((state.DockDrag.Active || state.DockDrag.Pending) &&
          state.DockDrag.SourceHost == host));
    if ((!expectedFrame && !(event == 3 && continuingCapture)) ||
        (expectedFrame && expectedFrame->StateRevision != state.StateRevision &&
         !continuingCapture && expectedElementId.empty()))
        return failure("UI_FRAME_STALE", "expectedFrameId",
                       "a retained frame at the current stateRevision or the active capture",
                       std::to_string(expectedFrameId), true);
    RECT client{};
    GetClientRect(host->Window, &client);
    if (x < 0 || y < 0 || x >= client.right || y >= client.bottom)
        return failure("UI_COORDINATE_OUT_OF_BOUNDS", "x,y", "host-local bounds",
                       std::to_string(x) + "," + std::to_string(y), false);
    if (!std::isfinite(wheelX) || !std::isfinite(wheelY) ||
        (event != 4 && (wheelX != 0.0f || wheelY != 0.0f)))
        return failure("UI_POINTER_SEQUENCE_INVALID", "wheelX,wheelY",
                       "finite values used only by a wheel event",
                       JsonNumber(wheelX) + "," + JsonNumber(wheelY), false);

    state.System.SetWindow(host->Window);
    state.InjectedModifiers = static_cast<int>(modifiers);
    state.InjectedWheelX = wheelX;
    state.InjectedWheelY = wheelY;
    SendMessageW(host->Window, WM_MOUSEMOVE,
        static_cast<WPARAM>(buttons), MAKELPARAM(x, y));
    Rml::Element* target = host->Context->GetHoverElement();
    if (state.SplitterDrag.Active && state.SplitterDrag.HostWindow == host)
        target = state.SplitterDrag.Element;
    else if ((state.DockDrag.Active || state.DockDrag.Pending) &&
             state.DockDrag.SourceHost == host)
        target = state.DockDrag.SourceElement;
    const auto targetId = Impl::StableAncestorId(*host, target);
    if (!expectedElementId.empty() && targetId != expectedElementId)
    {
        state.InjectedModifiers = -1;
        return failure("UI_TARGET_CHANGED", "expectedElementId",
                       std::string(expectedElementId), targetId, true);
    }
    Rml::Element* interactive = target;
    while (interactive && !Impl::IsInteractive(interactive))
        interactive = interactive->GetParentNode();
    if (event == 1 && !interactive)
    {
        state.InjectedModifiers = -1;
        return failure("UI_TARGET_NOT_INTERACTIVE", "x,y",
                       "an interactive hit target", targetId, false);
    }

    const auto buttonBit = button == 1 ? static_cast<uint32_t>(MK_LBUTTON) :
        button == 2 ? static_cast<uint32_t>(MK_RBUTTON) :
        button == 3 ? static_cast<uint32_t>(MK_MBUTTON) : 0u;
    UINT message{};
    if (event == 0) message = WM_MOUSEMOVE;
    else if (event == 1)
    {
        if (!buttonBit || (state.McpButtons & buttonBit) != 0)
        {
            state.InjectedModifiers = -1;
            return failure("UI_POINTER_SEQUENCE_INVALID", "event", "a new button down",
                           std::to_string(button), false);
        }
        const HWND capture = GetCapture();
        if (capture && capture != host->Window)
        {
            state.InjectedModifiers = -1;
            return failure("UI_POINTER_CAPTURE_CONFLICT", "hostId",
                           std::string(hostId), "another host", true);
        }
        state.McpButtons |= buttonBit;
        message = button == 1 ? WM_LBUTTONDOWN :
            button == 2 ? WM_RBUTTONDOWN : WM_MBUTTONDOWN;
    }
    else if (event == 2)
    {
        if (!buttonBit || (state.McpButtons & buttonBit) == 0)
        {
            state.InjectedModifiers = -1;
            return failure("UI_POINTER_SEQUENCE_INVALID", "event", "a matching button up",
                           std::to_string(button), false);
        }
        message = button == 1 ? WM_LBUTTONUP :
            button == 2 ? WM_RBUTTONUP : WM_MBUTTONUP;
    }
    else if (event == 3)
    {
        if (GetCapture() == host->Window)
            SendMessageW(host->Window, WM_CAPTURECHANGED, 0, 0);
        state.McpButtons = 0;
    }
    else if (event == 4) message = WM_MOUSEWHEEL;
    else
    {
        state.InjectedModifiers = -1;
        return failure("UI_POINTER_SEQUENCE_INVALID", "event", "move, down, up, cancel, or wheel",
                       std::to_string(event), false);
    }

    const auto beforeRevision = state.StateRevision;
    if (event != 3)
    {
        WPARAM wParam = static_cast<WPARAM>(buttons | state.McpButtons);
        LPARAM lParam = MAKELPARAM(x, y);
        if (event == 4)
        {
            POINT screen{x, y};
            ClientToScreen(host->Window, &screen);
            const auto delta = static_cast<short>(std::lround(-wheelY * WHEEL_DELTA));
            wParam = MAKEWPARAM(buttons | state.McpButtons,
                               static_cast<uint16_t>(delta));
            lParam = MAKELPARAM(screen.x, screen.y);
        }
        SendMessageW(host->Window, message, wParam, lParam);
        if (event == 2) state.McpButtons &= ~buttonBit;
    }
    state.InjectedModifiers = -1;
    state.InjectedWheelX = 0;
    state.InjectedWheelY = 0;
    if (!Render())
        return failure("UI_RENDER_UNAVAILABLE", "frameId", "a completed presentation",
                       std::to_string(state.FrameId), true);
    state.PinCurrentFrame();
    target = host->Context->GetHoverElement();
    if (state.SplitterDrag.Active && state.SplitterDrag.HostWindow == host)
        target = state.SplitterDrag.Element;
    const auto resolvedId = Impl::StableAncestorId(*host, target);
    const auto path = state.EventPath(*host, target);
    std::string capture;
    if (state.SplitterDrag.Active && state.SplitterDrag.HostWindow == host)
        capture = Impl::ElementIdentity(*host, state.SplitterDrag.Element);
    else if ((state.DockDrag.Active || state.DockDrag.Pending) &&
             state.DockDrag.SourceHost == host)
        capture = Impl::StableAncestorId(*host, state.DockDrag.SourceElement);
    std::ostringstream result;
    result << "{\"accepted\":true,\"beforeStateRevision\":" << beforeRevision
           << ",\"afterStateRevision\":" << state.StateRevision
           << ",\"frameId\":" << state.FrameId
           << ",\"targetElementId\":"
           << (resolvedId.empty() ? "null" : JsonString(resolvedId))
           << ",\"eventPath\":[";
    for (size_t index = 0; index < path.size(); ++index)
    {
        if (index) result << ',';
        result << JsonString(path[index]);
    }
    result << "],\"capture\":";
    if (capture.empty()) result << "null";
    else result << "{\"pointerId\":0,\"elementId\":" << JsonString(capture) << '}';
    result << ",\"diagnostics\":[]}";
    return result.str();
}

std::string RmlUiShell::KeyInput(std::string_view hostId, uint32_t event,
                                 uint32_t key, uint32_t modifiers,
                                 std::string_view text,
                                 uint64_t expectedFrameId)
{
    auto& state = *State;
    const auto failure = [&](std::string_view code, std::string_view message,
                             bool retryable) {
        return "{\"accepted\":false,\"beforeStateRevision\":" +
            std::to_string(state.StateRevision) +
            ",\"afterStateRevision\":" + std::to_string(state.StateRevision) +
            ",\"frameId\":" + std::to_string(state.FrameId) +
            ",\"targetElementId\":null,\"eventPath\":[],\"focusBefore\":null,\"focusAfter\":null,\"diagnostics\":[{\"code\":" +
            JsonString(code) + ",\"severity\":\"error\",\"objectId\":" +
            JsonString(hostId) + ",\"fieldPath\":null,\"expected\":null,\"actual\":null,\"stateRevision\":" +
            std::to_string(state.StateRevision) + ",\"retryable\":" +
            (retryable ? "true" : "false") + ",\"message\":" +
            JsonString(message) + "}]}";
    };
    const auto* expectedFrame = state.FindFrameSet(expectedFrameId);
    if (!expectedFrame || expectedFrame->StateRevision != state.StateRevision)
        return failure("UI_FRAME_STALE", "The observed frame is no longer current", true);
    auto* host = state.FindHost(hostId);
    if (!host || !host->Context || !host->Window)
        return failure("UI_HOST_NOT_FOUND", "The requested host is not live", true);
    if (event > 2)
        return failure("UI_KEY_SEQUENCE_INVALID", "Unknown key event", false);
    auto* focusBeforeElement = host->Context->GetFocusElement();
    const auto focusBefore = Impl::StableAncestorId(*host, focusBeforeElement);
    if (!focusBeforeElement)
        return failure("UI_TARGET_NOT_INTERACTIVE", "The host has no focused element", false);
    const auto beforeRevision = state.StateRevision;
    state.System.SetWindow(host->Window);
    state.SetCurrentHost(*host);
    if (event == 0)
        host->Context->ProcessKeyDown(RmlWin32::ConvertKey(static_cast<int>(key)),
                                      static_cast<int>(modifiers));
    else if (event == 1)
        host->Context->ProcessKeyUp(RmlWin32::ConvertKey(static_cast<int>(key)),
                                    static_cast<int>(modifiers));
    else
        host->Context->ProcessTextInput(Rml::String(text));
    if (!Render())
        return failure("UI_RENDER_UNAVAILABLE", "The input was accepted but no frame was presented", true);
    state.PinCurrentFrame();
    auto* focusAfterElement = host->Context->GetFocusElement();
    const auto focusAfter = Impl::StableAncestorId(*host, focusAfterElement);
    const auto path = state.EventPath(*host, focusAfterElement);
    std::ostringstream result;
    result << "{\"accepted\":true,\"beforeStateRevision\":" << beforeRevision
           << ",\"afterStateRevision\":" << state.StateRevision
           << ",\"frameId\":" << state.FrameId
           << ",\"targetElementId\":"
           << (focusAfter.empty() ? "null" : JsonString(focusAfter))
           << ",\"eventPath\":[";
    for (size_t index = 0; index < path.size(); ++index)
    {
        if (index) result << ',';
        result << JsonString(path[index]);
    }
    result << "],\"focusBefore\":"
           << (focusBefore.empty() ? "null" : JsonString(focusBefore))
           << ",\"focusAfter\":"
           << (focusAfter.empty() ? "null" : JsonString(focusAfter))
           << ",\"diagnostics\":[]}";
    return result.str();
}

uint32_t RmlUiShell::ReadFrame(uint64_t frameId, std::string_view hostId,
                               unsigned char* pixels, uint32_t capacity,
                               uint32_t& width, uint32_t& height) const
{
    width = 0;
    height = 0;
    const auto* frame = State->FindFrame(frameId, hostId);
    if (!frame) return 0;
    width = frame->Width;
    height = frame->Height;
    if (frame->Pixels.size() > UINT32_MAX) return 0;
    const auto required = static_cast<uint32_t>(frame->Pixels.size());
    if (pixels && capacity >= required)
        std::memcpy(pixels, frame->Pixels.data(), required);
    return required;
}

bool RmlUiShell::CaptureScreenshot(const std::filesystem::path& path)
{
    auto* host = State->FindMainHost();
    if (!host || !State->Gl) return false;
    RECT rect{};
    GetClientRect(host->Window, &rect);
    if (rect.right <= 0 || rect.bottom <= 0) return false;
    if (!State->SetCurrentHost(*host)) return false;
    std::vector<unsigned char> pixels(
        static_cast<size_t>(rect.right) * rect.bottom * 4);
    glReadBuffer(GL_FRONT);
    glReadPixels(0, 0, rect.right, rect.bottom, 0x80E1,
                 GL_UNSIGNED_BYTE, pixels.data());
    BITMAPFILEHEADER file{};
    file.bfType = 0x4d42;
    file.bfOffBits = sizeof(file) + sizeof(BITMAPINFOHEADER);
    file.bfSize = file.bfOffBits + static_cast<DWORD>(pixels.size());
    BITMAPINFOHEADER info{};
    info.biSize = sizeof(info);
    info.biWidth = rect.right;
    info.biHeight = rect.bottom;
    info.biPlanes = 1;
    info.biBitCount = 32;
    info.biCompression = BI_RGB;
    info.biSizeImage = static_cast<DWORD>(pixels.size());
    std::ofstream output(path, std::ios::binary);
    output.write(reinterpret_cast<const char*>(&file), sizeof(file));
    output.write(reinterpret_cast<const char*>(&info), sizeof(info));
    output.write(reinterpret_cast<const char*>(pixels.data()), pixels.size());
    return output.good();
}
