#include "RmlUiShell.h"
#include <RmlUi/Core.h>
#include <RmlUi/Core/Box.h>
#include <RmlUi/Core/ElementInstancer.h>
#include <shobjidl.h>
#include "RmlUi_Platform_Win32.h"
#include "RmlUi_Renderer_GL2.h"
#include <gl/GL.h>
#include <windowsx.h>
#include <iostream>
#include <vector>
#include <fstream>
#include <cstring>

namespace
{
    RmlUiShell* GShell = nullptr; WNDPROC GOriginalProc = nullptr;
    std::function<void(int, int)> GPointer;
    std::function<void(int, int, uint32_t, uint32_t, int32_t)> GViewportInput;
    std::function<void(uint32_t, bool, bool)> GViewportKey;
    std::string Utf8File(const std::filesystem::path& path)
    {
        const auto utf8 = path.u8string();
        return { reinterpret_cast<const char*>(utf8.data()), utf8.size() };
    }
    Rml::String ChooseProjectPath(HWND window, bool create)
    {
        IFileDialog* dialog = nullptr;
        const HRESULT created = create
            ? CoCreateInstance(CLSID_FileSaveDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog))
            : CoCreateInstance(CLSID_FileOpenDialog,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&dialog));
        if (FAILED(created)) return {};
        DWORD options{}; dialog->GetOptions(&options);
        dialog->SetOptions(options | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR | FOS_NOTESTFILECREATE);
        dialog->SetTitle(create ? L"New Project - choose parent folder and enter a project name" : L"Open SharpEngine Project");
        dialog->SetOkButtonLabel(create ? L"Create project" : L"Open project");
        if (create) { dialog->SetFileName(L"NewGame"); dialog->SetFileNameLabel(L"Project name:"); }
        else { const COMDLG_FILTERSPEC filter{L"SharpEngine project (project.json)",L"project.json"}; dialog->SetFileTypes(1,&filter); }
        Rml::String path;
        if (SUCCEEDED(dialog->Show(window)))
        {
            IShellItem* item = nullptr;
            if (SUCCEEDED(dialog->GetResult(&item)))
            {
                PWSTR wide = nullptr;
                if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH,&wide))) { path=RmlWin32::ConvertToUTF8(wide); CoTaskMemFree(wide); }
                item->Release();
            }
        }
        dialog->Release(); return path;
    }
}
class CountedRenderer : public RenderInterface_GL2
{
public:
    uint32_t DrawCalls{};
    void RenderGeometry(Rml::CompiledGeometryHandle handle, Rml::Vector2f translation, Rml::TextureHandle texture) override
    {
        ++DrawCalls;
        RenderInterface_GL2::RenderGeometry(handle, translation, texture);
    }
};
class Win32FileInterface : public Rml::FileInterface
{
public:
    Rml::FileHandle Open(const Rml::String& path) override
    { return reinterpret_cast<Rml::FileHandle>(_wfopen(RmlWin32::ConvertToUTF16(path).c_str(),L"rb")); }
    void Close(Rml::FileHandle file) override { fclose(reinterpret_cast<FILE*>(file)); }
    size_t Read(void* buffer,size_t size,Rml::FileHandle file) override { return fread(buffer,1,size,reinterpret_cast<FILE*>(file)); }
    bool Seek(Rml::FileHandle file,long offset,int origin) override { return fseek(reinterpret_cast<FILE*>(file),offset,origin)==0; }
    size_t Tell(Rml::FileHandle file) override { return ftell(reinterpret_cast<FILE*>(file)); }
};
class ViewportElement : public Rml::Element
{
public:
    explicit ViewportElement(const Rml::String& tag) : Rml::Element(tag) {}
protected:
    void OnRender() override
    {
        if (!GShell) return;
        const auto p = GetAbsoluteOffset(); const auto size = GetBox().GetSize();
        GShell->RenderViewport({p.x,p.y,size.x,size.y});
    }
};
static Rml::ElementInstancerGeneric<ViewportElement> GViewportInstancer;
struct RmlUiShell::Impl : Rml::EventListener
{
    HWND Window{}; HDC Dc{}; HGLRC Gl{};
    SystemInterface_Win32 System;
    TextInputMethodEditor_Win32 Ime;
    Win32FileInterface Files;
    CountedRenderer Renderer;
    Rml::Context* Context{};
    Rml::ElementDocument* Document{};
    std::function<void(const char*)> Action;
    Rml::Element* OpenPopup{};
    std::vector<UiSceneVertex> Triangles, Lines;
    UiViewportRect SceneRect{};
    bool Reported{};
    bool Ready{};
    bool ComInitialized{};

    void HidePopups()
    {
        if (OpenPopup)
        {
            OpenPopup->SetAttribute("open", false);
            OpenPopup->SetProperty("display", "none");
            OpenPopup = nullptr;
        }
        if (auto* contextMenu = Document->GetElementById("contextMenu"))
            contextMenu->SetProperty("display", "none");
    }

    void ProcessEvent(Rml::Event& event) override
    {
        Rml::Element* target = event.GetTargetElement();
        if (event.GetId() == Rml::EventId::Change && target && target->GetId() == "projectSearch")
        {
            const auto value = target->GetAttribute<Rml::String>("value", "");
            if (Action) Action((Rml::String("editor.search-assets:") + value).c_str());
            return;
        }
        if (event.GetId() == Rml::EventId::Mouseup &&
            event.GetParameter<int>("button", -1) == 1)
        {
            for (auto* element = target; element; element = element->GetParentNode())
            {
                const auto kind = element->GetAttribute<Rml::String>("data-context-kind", "");
                if (kind.empty()) continue;
                const auto id = element->GetAttribute<Rml::String>("data-context-id", "");
                const auto source = element->GetAttribute<Rml::String>("data-context-source", "");
                const auto x = event.GetParameter<int>("mouse_x", 0);
                const auto y = event.GetParameter<int>("mouse_y", 0);
                HidePopups();
                if (Action)
                {
                    const auto request = Rml::String("editor.context:") +
                        std::to_string(x) + ":" + std::to_string(y) + ":" +
                        kind + ":" + source + ":" + id;
                    Action(request.c_str());
                }
                return;
            }
            return;
        }
        if (event.GetId() != Rml::EventId::Click) return;
        while (target)
        {
            const auto popupId = target->GetAttribute<Rml::String>("data-popup-id", "");
            if (!popupId.empty())
            {
                auto* popup = Document->GetElementById(popupId);
                const bool wasOpen = popup && popup == OpenPopup;
                HidePopups();
                if (popup && !wasOpen)
                {
                    popup->SetAttribute("open", true);
                    popup->SetProperty("display", "block");
                    OpenPopup = popup;
                }
                return;
            }
            auto action = target->GetAttribute<Rml::String>("data-action", "");
            if (action.empty()) action = target->GetAttribute<Rml::String>("action", "");
            if (!action.empty())
            {
                HidePopups();
                if (action == "editor.new-project" || action == "editor.open-project" ||
                    action == "editor.command:core.new-project" ||
                    action == "editor.command:core.open-project")
                {
                    const bool create = action == "editor.new-project" ||
                        action == "editor.command:core.new-project";
                    const auto path = ChooseProjectPath(Window,create);
                    if (!path.empty() && Action) Action((Rml::String(create ? "editor.create-project:" : "editor.open-project:") + path).c_str());
                    return;
                }
                if (Action) Action(action.c_str());
                std::cout << "[RmlUi] action=" << action << std::endl;
                return;
            }
            target = target->GetParentNode();
        }
        HidePopups();
    }
};
static Rml::Context* GContext{};
static TextInputMethodEditor_Win32* GIme{};
static LRESULT CALLBACK RmlWindowProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
{
    if (GContext && GIme)
    {
        if (msg == WM_NCHITTEST)
        {
            // Native resize edges take precedence. Within the title/menu bar,
            // only non-interactive elements act as a caption; no fixed menu or
            // window-control coordinates are needed when the layout changes.
            const LRESULT hit = CallWindowProcW(GOriginalProc, hwnd, msg, wp, lp);
            if (hit != HTCLIENT) return hit;
            POINT point{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
            ScreenToClient(hwnd, &point);
            for (auto* element = GContext->GetElementAtPoint(
                     {static_cast<float>(point.x), static_cast<float>(point.y)});
                 element; element = element->GetParentNode())
            {
                if (element->GetTagName() == "button" ||
                    element->GetTagName() == "input" ||
                    element->HasAttribute("action") || element->HasAttribute("data-action"))
                    return HTCLIENT;
                if (element->GetId() == "menubar") return HTCAPTION;
            }
            return HTCLIENT;
        }
        if (msg == WM_ERASEBKGND) return 1;
        if (msg == WM_PAINT) { PAINTSTRUCT p{}; BeginPaint(hwnd, &p); EndPaint(hwnd, &p); return 0; }
        if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN ||
            msg == WM_KEYUP || msg == WM_SYSKEYUP || msg == WM_KILLFOCUS)
        {
            bool viewportFocused = false;
            if (msg != WM_KILLFOCUS)
            {
                for (auto* element = GContext->GetFocusElement(); element;
                     element = element->GetParentNode())
                    if (element->GetId() == "viewportCanvas") { viewportFocused = true; break; }
            }
            if (GViewportKey)
            {
                const bool pressed = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                GViewportKey(msg == WM_KILLFOCUS ? 0u : static_cast<uint32_t>(wp),
                             pressed, viewportFocused);
            }
        }
        const auto isButtonMessage = msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN ||
            msg == WM_MBUTTONDOWN || msg == WM_LBUTTONUP || msg == WM_RBUTTONUP ||
            msg == WM_MBUTTONUP;
        if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN) SetCapture(hwnd);
        if (isButtonMessage || msg == WM_MOUSEMOVE || msg == WM_MOUSEWHEEL)
        {
            int x = static_cast<short>(LOWORD(lp)); int y = static_cast<short>(HIWORD(lp));
            if (msg == WM_MOUSEWHEEL)
            {
                POINT point{GET_X_LPARAM(lp), GET_Y_LPARAM(lp)};
                ScreenToClient(hwnd, &point); x = point.x; y = point.y;
            }
            GContext->ProcessMouseMove(x,y,RmlWin32::GetKeyModifierState());
            bool viewport = false;
            Rml::Element* viewportElement = nullptr;
            for (auto* element=GContext->GetHoverElement(); element; element=element->GetParentNode())
                if (element->GetId()=="viewportCanvas") { viewport = true; viewportElement = element; break; }
            const bool captured = GetCapture() == hwnd;
            if (viewport || captured)
            {
                uint32_t buttons = static_cast<uint32_t>(wp) &
                    (MK_LBUTTON | MK_RBUTTON | MK_MBUTTON | MK_SHIFT | MK_CONTROL);
                if (msg == WM_LBUTTONUP) buttons |= MK_LBUTTON;
                if (msg == WM_RBUTTONUP) buttons |= MK_RBUTTON;
                if (msg == WM_MBUTTONUP) buttons |= MK_MBUTTON;
                uint32_t inputEvent = 0;
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN) inputEvent = 1;
                else if (msg == WM_LBUTTONUP || msg == WM_RBUTTONUP || msg == WM_MBUTTONUP) inputEvent = 2;
                else if (msg == WM_MOUSEWHEEL) inputEvent = 3;
                const int32_t wheel = msg == WM_MOUSEWHEEL ? GET_WHEEL_DELTA_WPARAM(wp) : 0;
                if (GViewportInput) GViewportInput(x, y, inputEvent, buttons, wheel);
                if (msg == WM_LBUTTONDOWN && viewport)
                {
                    if (viewportElement) viewportElement->Focus();
                    if (GShell && GPointer) GPointer(x,y);
                }
            }
        }
        if (msg == WM_LBUTTONUP || msg == WM_RBUTTONUP || msg == WM_MBUTTONUP) ReleaseCapture();
        if (!RmlWin32::WindowProcedure(GContext, *GIme, hwnd, msg, wp, lp)) return 0;
    }
    return CallWindowProcW(GOriginalProc, hwnd, msg, wp, lp);
}
RmlUiShell::RmlUiShell() : State(std::make_unique<Impl>()) {}
RmlUiShell::~RmlUiShell() { Shutdown(); }
bool RmlUiShell::Initialize(HWND window, const std::filesystem::path& engineRoot)
{
    auto& s = *State;
    s.Window = window;
    s.ComInitialized = SUCCEEDED(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED));
    s.Dc = GetDC(window);
    PIXELFORMATDESCRIPTOR pfd{}; pfd.nSize = sizeof(pfd); pfd.nVersion = 1;
    pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    pfd.iPixelType = PFD_TYPE_RGBA; pfd.cColorBits = 32; pfd.cDepthBits = 24; pfd.cStencilBits = 8;
    const int pixelFormat = ChoosePixelFormat(s.Dc, &pfd);
    if (!pixelFormat || !SetPixelFormat(s.Dc, pixelFormat, &pfd)) return false;
    s.Gl = wglCreateContext(s.Dc);
    if (!s.Gl || !wglMakeCurrent(s.Dc, s.Gl)) return false;
    s.System.SetWindow(window);
    Rml::SetSystemInterface(&s.System); Rml::SetRenderInterface(&s.Renderer); Rml::SetTextInputHandler(&s.Ime); Rml::SetFileInterface(&s.Files);
    if (!Rml::Initialise()) return false;
    s.Ready = true;
    Rml::Factory::RegisterElementInstancer("viewport", &GViewportInstancer);
    RECT client{}; GetClientRect(window, &client);
    s.Context = Rml::CreateContext("editor", {client.right, client.bottom});
    if (!s.Context) return false;
    // Windows fonts stay system-owned; the distribution includes RmlUi's open font as fallback.
    const bool regular = Rml::LoadFontFace("C:/Windows/Fonts/segoeui.ttf");
    Rml::LoadFontFace("C:/Windows/Fonts/segoeuib.ttf");
    Rml::LoadFontFace("C:/Windows/Fonts/msyh.ttc", true);
    if (!regular) Rml::LoadFontFace(Utf8File(engineRoot / "External/RmlUi/Samples/assets/LatoLatin-Regular.ttf"));
    const auto documentPath = Utf8File(engineRoot / "Content/UI/Rml/Editor.rml");
    s.Document = s.Context->LoadDocument(documentPath);
    if (!s.Document) return false;
    s.Document->AddEventListener(Rml::EventId::Click, &s);
    s.Document->AddEventListener(Rml::EventId::Change, &s);
    s.Document->AddEventListener(Rml::EventId::Mouseup, &s);
    s.Document->Show(); s.Context->Update();
    GShell = this; GContext = s.Context; GIme = &s.Ime;
    GOriginalProc = reinterpret_cast<WNDPROC>(SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&RmlWindowProc)));
    std::cout << "[RmlUi] version=6.1 context=editor document=" << documentPath << std::endl;
    return true;
}
void RmlUiShell::Shutdown()
{
    auto& s = *State;
    if (s.Window && GShell == this && GOriginalProc) SetWindowLongPtrW(s.Window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(GOriginalProc));
    GContext = nullptr; GIme = nullptr; GShell = nullptr; GPointer = {}; GViewportInput = {}; GViewportKey = {};
    if (s.Ready) { Rml::Shutdown(); s.Ready = false; }
    s.Document = nullptr; s.Context = nullptr;
    if (s.Gl) { wglMakeCurrent(nullptr, nullptr); wglDeleteContext(s.Gl); s.Gl = nullptr; }
    if (s.Dc && s.Window) { ReleaseDC(s.Window, s.Dc); s.Dc = nullptr; }
    if (s.ComInitialized) { CoUninitialize(); s.ComInitialized=false; }
}
bool RmlUiShell::Render()
{
    auto& s = *State; if (!s.Context) return false;
    RECT client{}; GetClientRect(s.Window, &client);
    if (client.right <= 0 || client.bottom <= 0) return true;
    wglMakeCurrent(s.Dc, s.Gl);
    s.Context->SetDimensions({client.right, client.bottom});
    s.Context->Update(); s.Renderer.SetViewport(client.right, client.bottom);
    s.Renderer.DrawCalls = 0;
    s.Renderer.BeginFrame(); s.Renderer.Clear(); s.Context->Render(); s.Renderer.EndFrame();
    const bool presented = SwapBuffers(s.Dc) != FALSE;
    if (presented && !s.Reported)
    {
        UiViewportRect rect{}; GetViewport(rect);
        std::cout << "[RmlUi] first-frame presented viewport=" << rect.X << "," << rect.Y << "," << rect.Width << "," << rect.Height
                  << " ui-draw-calls=" << s.Renderer.DrawCalls << " scene-triangles=" << s.Triangles.size()/3 << " scene-lines=" << s.Lines.size()/2 << std::endl;
        s.Reported = true;
    }
    return presented;
}
void RmlUiShell::RenderViewport(const UiViewportRect& viewport)
{
    auto& s = *State; RECT client{}; GetClientRect(s.Window, &client);
    if (!s.Triangles.empty() || !s.Lines.empty())
    {
        glPushAttrib(GL_ALL_ATTRIB_BITS); glPushClientAttrib(GL_CLIENT_ALL_ATTRIB_BITS);
        glMatrixMode(GL_PROJECTION); glPushMatrix(); glMatrixMode(GL_MODELVIEW); glPushMatrix();
        glViewport(static_cast<int>(viewport.X), client.bottom - static_cast<int>(viewport.Y + viewport.Height), static_cast<int>(viewport.Width), static_cast<int>(viewport.Height));
        glEnable(GL_SCISSOR_TEST); glScissor(static_cast<int>(viewport.X), client.bottom - static_cast<int>(viewport.Y + viewport.Height), static_cast<int>(viewport.Width), static_cast<int>(viewport.Height));
        glDisable(GL_TEXTURE_2D); glDisable(GL_BLEND); glDisable(GL_STENCIL_TEST);
        glClearColor(0.055f, 0.065f, 0.08f, 1); glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        glMatrixMode(GL_PROJECTION); glLoadIdentity(); glOrtho(0, viewport.Width, viewport.Height, 0, 0, -1);
        glMatrixMode(GL_MODELVIEW); glLoadIdentity();
        glEnable(GL_DEPTH_TEST); glDepthFunc(GL_LEQUAL);
        glEnable(GL_POLYGON_OFFSET_FILL); glPolygonOffset(1.0f, 1.0f);
        glBegin(GL_TRIANGLES);
        for (const auto& v : s.Triangles) { glColor4f(v.R,v.G,v.B,v.A); glVertex3f(v.X,v.Y,v.Z); }
        glEnd(); glDisable(GL_POLYGON_OFFSET_FILL);
        glBegin(GL_LINES);
        for (const auto& v : s.Lines) { glColor4f(v.R,v.G,v.B,v.A); glVertex3f(v.X,v.Y,v.Z); }
        glEnd(); glDisable(GL_DEPTH_TEST); glDisable(GL_SCISSOR_TEST);
        glMatrixMode(GL_MODELVIEW); glPopMatrix(); glMatrixMode(GL_PROJECTION); glPopMatrix();
        glPopClientAttrib(); glPopAttrib();
    }
}
void RmlUiShell::SetText(const char* id, const char* utf8)
{
    if (!State->Document || !id || !utf8) return;
    if (auto* e = State->Document->GetElementById(id))
    {
        const Rml::String text = Rml::StringUtilities::EncodeRml(utf8);
        if (e->GetInnerRML() != text) e->SetInnerRML(text);
    }
}
void RmlUiShell::SetMarkup(const char* id, const char* markup)
{
    if (!State->Document || !id || !markup) return;
    if (strcmp(id, "topMenus") == 0 || strcmp(id, "toolbarLeft") == 0 ||
        strcmp(id, "toolbarCenter") == 0 || strcmp(id, "toolbarRight") == 0)
        State->OpenPopup = nullptr;
    if (auto* e = State->Document->GetElementById(id)) e->SetInnerRML(markup);
}
void RmlUiShell::SetProperty(const char* id, const char* name, const char* value)
{
    if (!State->Document || !id || !name || !value) return;
    if (auto* e = State->Document->GetElementById(id)) e->SetProperty(name, value);
}
bool RmlUiShell::GetViewport(UiViewportRect& rect) const
{
    if (!State->Document) return false;
    if (auto* e = State->Document->GetElementById("viewportCanvas"))
    { const auto p = e->GetAbsoluteOffset(); const auto size = e->GetBox().GetSize(); rect={p.x,p.y,size.x,size.y}; return true; }
    return false;
}
void RmlUiShell::SubmitScene(const UiViewportRect& rect, const UiSceneVertex* triangles, int triangleVertices, const UiSceneVertex* lines, int lineVertices)
{
    auto& s=*State; s.SceneRect=rect;
    if (triangles && triangleVertices>0) s.Triangles.assign(triangles,triangles+triangleVertices); else s.Triangles.clear();
    if (lines && lineVertices>0) s.Lines.assign(lines,lines+lineVertices); else s.Lines.clear();
}
void RmlUiShell::SetActionHandler(std::function<void(const char*)> handler) { State->Action=std::move(handler); }
void RmlUiShell::SetPointerHandler(std::function<void(int, int)> handler) { GPointer=std::move(handler); }
void RmlUiShell::SetViewportInputHandler(std::function<void(int, int, uint32_t, uint32_t, int32_t)> handler) { GViewportInput=std::move(handler); }
void RmlUiShell::SetViewportKeyHandler(std::function<void(uint32_t, bool, bool)> handler) { GViewportKey=std::move(handler); }
bool RmlUiShell::IsInitialized() const { return State->Context != nullptr; }
bool RmlUiShell::CaptureScreenshot(const std::filesystem::path& path)
{
    RECT r{}; GetClientRect(State->Window, &r);
    if (!State->Gl || r.right <= 0 || r.bottom <= 0) return false;
    std::vector<unsigned char> pixels(static_cast<size_t>(r.right) * r.bottom * 4);
    glReadBuffer(GL_FRONT); glReadPixels(0, 0, r.right, r.bottom, 0x80E1, GL_UNSIGNED_BYTE, pixels.data());
    BITMAPFILEHEADER file{}; file.bfType=0x4d42; file.bfOffBits=sizeof(file)+sizeof(BITMAPINFOHEADER); file.bfSize=file.bfOffBits+static_cast<DWORD>(pixels.size());
    BITMAPINFOHEADER info{}; info.biSize=sizeof(info); info.biWidth=r.right; info.biHeight=r.bottom; info.biPlanes=1; info.biBitCount=32; info.biCompression=BI_RGB; info.biSizeImage=static_cast<DWORD>(pixels.size());
    std::ofstream out(path, std::ios::binary); out.write(reinterpret_cast<const char*>(&file),sizeof(file)); out.write(reinterpret_cast<const char*>(&info),sizeof(info)); out.write(reinterpret_cast<const char*>(pixels.data()),pixels.size());
    return out.good();
}




