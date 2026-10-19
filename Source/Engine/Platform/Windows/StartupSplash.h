#pragma once

#include <windows.h>
#include <atomic>
#include <chrono>
#include <future>
#include <mutex>
#include <string>
#include <thread>

// Editor-owned transient presentation. No renderer or managed runtime is needed,
// and its message loop remains responsive while the engine thread loads modules.
class StartupSplash
{
public:
    StartupSplash(bool Enabled, const std::wstring& Project);
    ~StartupSplash();
    StartupSplash(const StartupSplash&) = delete;
    StartupSplash& operator=(const StartupSplash&) = delete;

    void Report(int Percent, const char* Module, const char* Detail);
    void Close();

private:
    static LRESULT CALLBACK WindowProcedure(HWND Window, UINT Message, WPARAM WParam, LPARAM LParam);
    void Run();
    void Paint(HWND Window, HDC Target);
    void Refresh(HWND Window);

    std::thread UiThread;
    std::promise<void> Ready;
    std::atomic<HWND> Handle{};
    std::mutex StateMutex;
    int Percent = 0;
    std::wstring Module = L"Engine";
    std::wstring Detail = L"Preparing editor startup";
    std::wstring Project;
    HICON Icon = nullptr;
    const std::chrono::steady_clock::time_point Started = std::chrono::steady_clock::now();
};
