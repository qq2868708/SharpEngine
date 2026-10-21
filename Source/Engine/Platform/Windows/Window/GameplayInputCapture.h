#pragma once

#include <windows.h>
#include <vector>

namespace SharpEngine::Windows
{
    // Process-local native mechanics. Semantic input still flows through RmlUi/InputRuntime.
    class GameplayInputCapture
    {
    public:
        ~GameplayInputCapture() { Release(); }

        bool Acquire(HWND Window, const RECT& ScreenBounds)
        {
            if (!Window || !IsWindowVisible(Window) || IsIconic(Window))
            {
                return false;
            }
            if (Active && Owner == Window)
            {
                Bounds = ScreenBounds;
                return ClipCursor(&Bounds) != FALSE;
            }
            Release();
            SetForegroundWindow(Window);
            SetFocus(Window);
            if (GetForegroundWindow() != Window)
            {
                return false;
            }
            RAWINPUTDEVICE Mouse{};
            Mouse.usUsagePage = 1;
            Mouse.usUsage = 2;
            Mouse.hwndTarget = Window;
            if (!RegisterRawInputDevices(&Mouse, 1, sizeof(Mouse)))
            {
                return false;
            }
            Owner = Window;
            Bounds = ScreenBounds;
            MouseRegistered = true;
            Active = true;
            SetCapture(Window);
            if (GetCapture() != Window)
            {
                Release();
                return false;
            }
            if (!ClipCursor(&Bounds))
            {
                Release();
                return false;
            }
            while (ShowCursor(FALSE) >= 0)
            {
                ++CursorAdjustments;
            }
            ++CursorAdjustments;
            AbsoluteKnown = false;
            return true;
        }

        void UpdateBounds(const RECT& ScreenBounds)
        {
            if (Active)
            {
                Bounds = ScreenBounds;
                ClipCursor(&Bounds);
            }
        }

        void Release()
        {
            const bool WasActive = Active;
            Active = false;
            if (WasActive && GetCapture() == Owner)
            {
                ReleaseCapture();
            }
            RECT Current{};
            if (WasActive && GetClipCursor(&Current) && EqualRect(&Current, &Bounds))
            {
                ClipCursor(nullptr);
            }
            while (CursorAdjustments > 0)
            {
                ShowCursor(TRUE);
                --CursorAdjustments;
            }
            if (MouseRegistered)
            {
                RAWINPUTDEVICE Mouse{};
                Mouse.usUsagePage = 1;
                Mouse.usUsage = 2;
                Mouse.dwFlags = RIDEV_REMOVE;
                RegisterRawInputDevices(&Mouse, 1, sizeof(Mouse));
            }
            MouseRegistered = false;
            Owner = nullptr;
            AbsoluteKnown = false;
        }

        bool IsActive() const { return Active; }
        HWND Window() const { return Owner; }

        bool ReadMouseDelta(LPARAM Message, float& X, float& Y)
        {
            X = 0;
            Y = 0;
            if (!Active)
            {
                return false;
            }
            UINT Size = 0;
            if (GetRawInputData(reinterpret_cast<HRAWINPUT>(Message), RID_INPUT,
                nullptr, &Size, sizeof(RAWINPUTHEADER)) != 0 || Size == 0)
            {
                return false;
            }
            std::vector<BYTE> Data(Size);
            if (GetRawInputData(reinterpret_cast<HRAWINPUT>(Message), RID_INPUT,
                Data.data(), &Size, sizeof(RAWINPUTHEADER)) != Size)
            {
                return false;
            }
            const auto* Input = reinterpret_cast<const RAWINPUT*>(Data.data());
            if (Input->header.dwType != RIM_TYPEMOUSE)
            {
                return false;
            }
            if ((Input->data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0)
            {
                X = static_cast<float>(Input->data.mouse.lLastX);
                Y = static_cast<float>(Input->data.mouse.lLastY);
            }
            else
            {
                const bool Virtual = (Input->data.mouse.usFlags & MOUSE_VIRTUAL_DESKTOP) != 0;
                POINT Position{};
                Position.x = MulDiv(Input->data.mouse.lLastX,
                    GetSystemMetrics(Virtual ? SM_CXVIRTUALSCREEN : SM_CXSCREEN), 65535);
                Position.y = MulDiv(Input->data.mouse.lLastY,
                    GetSystemMetrics(Virtual ? SM_CYVIRTUALSCREEN : SM_CYSCREEN), 65535);
                if (AbsoluteKnown)
                {
                    X = static_cast<float>(Position.x - AbsolutePosition.x);
                    Y = static_cast<float>(Position.y - AbsolutePosition.y);
                }
                AbsolutePosition = Position;
                AbsoluteKnown = true;
            }
            return X != 0 || Y != 0;
        }

    private:
        HWND Owner = nullptr;
        RECT Bounds{};
        bool Active = false;
        bool MouseRegistered = false;
        int CursorAdjustments = 0;
        bool AbsoluteKnown = false;
        POINT AbsolutePosition{};
    };
}
