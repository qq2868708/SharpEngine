#include <windows.h>

#include <chrono>
#include <filesystem>
#include <string>
#include <thread>

#include "../ManagedHost.h"
#include "../Logging/EngineLog.h"

namespace
{
    std::filesystem::path ExecutableDirectory()
    {
        std::wstring Path(32768, L'\0');
        Path.resize(GetModuleFileNameW(nullptr, Path.data(), static_cast<DWORD>(Path.size())));
        return std::filesystem::path(Path).parent_path();
    }

    std::filesystem::path FindEngineRoot(std::filesystem::path Directory)
    {
        while (!Directory.empty())
        {
            if (std::filesystem::exists(Directory / L"engine.json"))
            {
                return Directory;
            }

            const auto Parent = Directory.parent_path();
            if (Parent == Directory)
            {
                break;
            }

            Directory = Parent;
        }

        return {};
    }

    bool HasArgument(int Count, wchar_t** Arguments, const std::wstring& Name)
    {
        for (int Index = 1; Index < Count; ++Index)
        {
            if (Arguments[Index] == Name)
            {
                return true;
            }
        }

        return false;
    }

    uint64_t ReadUnsignedArgument(int Count, wchar_t** Arguments,
                                  const std::wstring& Name)
    {
        for (int Index = 1; Index + 1 < Count; ++Index)
        {
            if (Arguments[Index] == Name)
            {
                try
                {
                    return std::stoull(Arguments[Index + 1]);
                }
                catch (...)
                {
                    return 0;
                }
            }
        }

        return 0;
    }
}

int wmain(int ArgumentCount, wchar_t** Arguments)
{
    std::filesystem::path ProjectManifest;
    for (int Index = 1; Index + 1 < ArgumentCount; ++Index)
    {
        if (std::wstring(Arguments[Index]) == L"--project")
        {
            ProjectManifest = Arguments[++Index];
        }
    }

    if (ProjectManifest.empty())
    {
        EngineLog::Error("Server", "SERVER_PROJECT_REQUIRED: use --project <file.sharpproject>");
        return 2;
    }

    const auto EngineRoot = FindEngineRoot(ExecutableDirectory());
    if (EngineRoot.empty())
    {
        EngineLog::Error("Server", "Could not locate engine.json from the Server executable");
        return 2;
    }

    SetEnvironmentVariableW(L"SHARPENGINE_ROOT", EngineRoot.c_str());
    ManagedProjectSelection Selection;
    if (!ResolveManagedProject(ProjectManifest, Selection))
    {
        return 2;
    }

    SetEnvironmentVariableW(L"SHARPENGINE_ROOT", Selection.EngineRoot.c_str());
    SetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST", Selection.ProjectManifest.c_str());
    EngineLog::Configure(Selection.ProjectRoot, "Server");

    const auto Directory = ExecutableDirectory();
    ManagedHost Managed;
    if (!Managed.Initialize((Directory / L"SharpEngine.Host.runtimeconfig.json").wstring(),
                            (Directory / L"SharpEngine.Host.dll").wstring()))
    {
        return 3;
    }

    if (!Managed.Start(nullptr, 0, 0, nullptr))
    {
        Managed.Shutdown();
        return 4;
    }

    const uint64_t TickLimit = ReadUnsignedArgument(ArgumentCount, Arguments, L"--ticks");
    const uint64_t DurationMilliseconds = ReadUnsignedArgument(
        ArgumentCount, Arguments, L"--duration-milliseconds");
    const auto Start = std::chrono::steady_clock::now();
    uint64_t Ticks = 0;
    while (true)
    {
        Managed.Update(1.0f / 60.0f);
        ++Ticks;
        if (TickLimit != 0 && Ticks >= TickLimit)
        {
            break;
        }

        if (DurationMilliseconds != 0 &&
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - Start).count() >=
                static_cast<int64_t>(DurationMilliseconds))
        {
            break;
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }

    Managed.Shutdown();
    return 0;
}
