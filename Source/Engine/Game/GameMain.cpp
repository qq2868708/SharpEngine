#include <windows.h>
#include <windowsx.h>
#include <shobjidl.h>
#include <GL/gl.h>
#include <chrono>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>
#include "ManagedHost.h"
#include "Platform/Windows/SharpEngineBranding.h"
#include "RenderCore/Public/PreviewRenderer.h"

namespace {
    ManagedHost* Managed{};
    HWND Window{};
    POINT LastPointer{};
    bool PointerKnown{};
    std::vector<UiSceneVertex> Triangles, Lines;
    uint8_t __cdecl Viewport(float* x,float* y,float* w,float* h) {
        RECT r{}; GetClientRect(Window,&r);
        *x=0; *y=0; *w=static_cast<float>(r.right); *h=static_cast<float>(r.bottom);
        return r.right>0 && r.bottom>0;
    }
    void __cdecl Submit(const float* t,uint32_t nt,const float* l,uint32_t nl) {
        Triangles.clear(); Lines.clear();
        if (nt) { auto* p=reinterpret_cast<const UiSceneVertex*>(t); Triangles.assign(p,p+nt); }
        if (nl) { auto* p=reinterpret_cast<const UiSceneVertex*>(l); Lines.assign(p,p+nl); }
    }
    LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM wp,LPARAM lp) {
        if (message==WM_CLOSE) { PostQuitMessage(0); return 0; }
        if (message==WM_ERASEBKGND) return 1;
        if (Managed) {
            uint32_t modifiers=(GetKeyState(VK_SHIFT)<0?1u:0u)|(GetKeyState(VK_CONTROL)<0?2u:0u)|(GetKeyState(VK_MENU)<0?4u:0u);
            uint32_t kind=99,code=0;
            float dx=0,dy=0;
            int x=GET_X_LPARAM(lp),y=GET_Y_LPARAM(lp);
            if (message==WM_KEYDOWN||message==WM_SYSKEYDOWN) { kind=0; code=static_cast<uint32_t>(wp); }
            if (message==WM_KEYUP||message==WM_SYSKEYUP) { kind=1; code=static_cast<uint32_t>(wp); }
            if (message==WM_LBUTTONDOWN||message==WM_RBUTTONDOWN||message==WM_MBUTTONDOWN) {
                kind=3; code=message==WM_LBUTTONDOWN?0:message==WM_RBUTTONDOWN?1:2; SetCapture(window);
            }
            if (message==WM_LBUTTONUP||message==WM_RBUTTONUP||message==WM_MBUTTONUP) {
                kind=4; code=message==WM_LBUTTONUP?0:message==WM_RBUTTONUP?1:2; ReleaseCapture();
            }
            if (message==WM_XBUTTONDOWN||message==WM_XBUTTONUP) {
                kind=message==WM_XBUTTONDOWN?3:4; code=GET_XBUTTON_WPARAM(wp)==XBUTTON1?3:4;
                if(kind==3) SetCapture(window); else ReleaseCapture();
            }
            if (message==WM_MOUSEMOVE) {
                kind=2; if(PointerKnown){dx=static_cast<float>(x-LastPointer.x);dy=static_cast<float>(y-LastPointer.y);}
                LastPointer={x,y};PointerKnown=true;
            }
            if (message==WM_MOUSEWHEEL||message==WM_MOUSEHWHEEL) {
                kind=5; POINT point{x,y};ScreenToClient(window,&point);x=point.x;y=point.y;
                const float wheel=static_cast<float>(GET_WHEEL_DELTA_WPARAM(wp))/WHEEL_DELTA;
                if(message==WM_MOUSEWHEEL)dy=wheel;else dx=wheel;
            }
            if (message==WM_KILLFOCUS) {kind=6;PointerKnown=false;}
            if (message==WM_CAPTURECHANGED) kind=7;
            if (kind!=99) Managed->InputEvent("game",kind,code,x,y,dx,dy,0,modifiers,0,1);
        }
        return DefWindowProcW(window,message,wp,lp);
    }
    void Screenshot(const std::filesystem::path& path,int width,int height) {
        std::vector<uint8_t> pixels(static_cast<size_t>(width)*height*4);
        glReadPixels(0,0,width,height,0x80E1,GL_UNSIGNED_BYTE,pixels.data());
        BITMAPFILEHEADER file{}; file.bfType=0x4d42; file.bfOffBits=sizeof(file)+sizeof(BITMAPINFOHEADER);
        file.bfSize=file.bfOffBits+static_cast<DWORD>(pixels.size());
        BITMAPINFOHEADER info{}; info.biSize=sizeof(info); info.biWidth=width;info.biHeight=height;
        info.biPlanes=1;info.biBitCount=32;info.biSizeImage=static_cast<DWORD>(pixels.size());
        std::ofstream stream(path,std::ios::binary); stream.write(reinterpret_cast<char*>(&file),sizeof(file));
        stream.write(reinterpret_cast<char*>(&info),sizeof(info)); stream.write(reinterpret_cast<char*>(pixels.data()),pixels.size());
    }
}

int wmain(int argc,wchar_t** argv) {
    std::filesystem::path project,screenshot; uint32_t limit=0; bool hidden=false;
    for(int i=1;i<argc;++i) {
        const std::wstring arg=argv[i];
        if(arg==L"--project"&&i+1<argc) project=argv[++i];
        else if(arg==L"--frames"&&i+1<argc) limit=std::stoul(argv[++i]);
        else if(arg==L"--smoke-test") limit=30;
        else if(arg==L"--screenshot"&&i+1<argc) screenshot=argv[++i];
        else if(arg==L"--hidden") hidden=true;
    }
    if(project.empty()) { std::cerr<<"GAME_PROJECT_REQUIRED: use --project <file.sharpproject>\n"; return 2; }
    std::wstring exe(32768,L'\0');exe.resize(GetModuleFileNameW(nullptr,exe.data(),static_cast<DWORD>(exe.size())));
    const auto directory=std::filesystem::path(exe).parent_path();
    auto root=directory;
    while(!root.empty()&&!std::filesystem::exists(root/L"engine.json")) { auto parent=root.parent_path();if(parent==root)break;root=parent; }
    SetEnvironmentVariableW(L"SHARPENGINE_ROOT",root.c_str());
    ManagedProjectSelection selection;
    if(!ResolveManagedProject(project,selection)) return 2;
    SetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST",selection.ProjectManifest.c_str());
    SetEnvironmentVariableW(L"SHARPENGINE_ROOT",selection.EngineRoot.c_str());
    SetCurrentProcessExplicitAppUserModelID(L"SharpEngine.Game");
    WNDCLASSW wc{};wc.lpfnWndProc=Procedure;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"SharpEngineGameWindow";wc.style=CS_OWNDC;wc.hCursor=LoadCursor(nullptr,IDC_ARROW);
    wc.hIcon=SharpEngine::Windows::LoadBrandIcon(wc.hInstance,GetSystemMetrics(SM_CXICON),GetSystemMetrics(SM_CYICON));
    RegisterClassW(&wc);
    Window=CreateWindowW(wc.lpszClassName,L"SharpEngine Game",WS_OVERLAPPEDWINDOW,CW_USEDEFAULT,CW_USEDEFAULT,1280,720,nullptr,nullptr,wc.hInstance,nullptr);
    if(!Window) return 3;
    SharpEngine::Windows::ApplyBrandIcon(Window, wc.hInstance);
    auto dc=GetDC(Window);
    PIXELFORMATDESCRIPTOR format{};format.nSize=sizeof(format);format.nVersion=1;
    format.dwFlags=PFD_DRAW_TO_WINDOW|PFD_SUPPORT_OPENGL|PFD_DOUBLEBUFFER;format.iPixelType=PFD_TYPE_RGBA;format.cColorBits=32;format.cDepthBits=24;
    if(!SetPixelFormat(dc,ChoosePixelFormat(dc,&format),&format)) return 3;
    auto gl=wglCreateContext(dc);if(!gl||!wglMakeCurrent(dc,gl)) return 3;
    ManagedHost managed;
    ManagedHostApi api{};api.GetViewportRect=Viewport;api.SubmitViewportFrame=Submit;
    if(!managed.Initialize((directory/L"SharpEngine.Host.runtimeconfig.json").wstring(),(directory/L"SharpEngine.Host.dll").wstring())||
        !managed.Start(Window,1280,720,&api)) { managed.Shutdown();wglMakeCurrent(nullptr,nullptr);wglDeleteContext(gl);ReleaseDC(Window,dc);DestroyWindow(Window);return 4; }
    Managed=&managed;
    if(!hidden)ShowWindow(Window,SW_SHOW);
    auto last=std::chrono::steady_clock::now();uint32_t frames=0;bool running=true;
    while(running) {
        MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) { if(message.message==WM_QUIT){running=false;break;}TranslateMessage(&message);DispatchMessageW(&message); }
        if(!running)break;
        auto now=std::chrono::steady_clock::now();float delta=std::chrono::duration<float>(now-last).count();last=now;
        managed.Update(limit?1.0f/60.0f:(delta>0.1f?0.1f:delta));
        RECT bounds{};GetClientRect(Window,&bounds);
        RenderPreviewScene(0,0,static_cast<float>(bounds.right),static_cast<float>(bounds.bottom),static_cast<float>(bounds.bottom),Triangles.data(),static_cast<uint32_t>(Triangles.size()),Lines.data(),static_cast<uint32_t>(Lines.size()));
        if(limit&&frames+1>=limit&&!screenshot.empty()) Screenshot(screenshot,bounds.right,bounds.bottom);
        SwapBuffers(dc);++frames;
        if(limit&&frames>=limit)break;
        Sleep(1);
    }
    Managed=nullptr;managed.Shutdown();wglMakeCurrent(nullptr,nullptr);wglDeleteContext(gl);ReleaseDC(Window,dc);DestroyWindow(Window);
    return 0;
}
