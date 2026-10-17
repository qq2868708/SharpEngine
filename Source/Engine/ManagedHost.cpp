#include "ManagedHost.h"

#include <Windows.h>

#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <sstream>
#include <string_view>

namespace
{
    using HostFxrHandle = void*;

    enum HostFxrDelegateType : int
    {
        HdtLoadAssemblyAndGetFunctionPointer = 5,
    };

    using HostFxrInitializeForRuntimeConfig = int(__cdecl*)(
        const wchar_t* runtimeConfigPath,
        const void* parameters,
        HostFxrHandle* hostContext);

    using HostFxrGetRuntimeDelegate = int(__cdecl*)(
        HostFxrHandle hostContext,
        HostFxrDelegateType type,
        void** delegate);

    using HostFxrClose = int(__cdecl*)(HostFxrHandle hostContext);

    using LoadAssemblyAndGetFunctionPointerFn = int(__stdcall*)(
        const wchar_t* assemblyPath,
        const wchar_t* typeName,
        const wchar_t* methodName,
        const wchar_t* delegateTypeName,
        void* reserved,
        void** delegate);

    using ManagedInitialize = int(__cdecl*)(void* windowHandle,
                                            uint32_t width,
                                            uint32_t height,
                                            const ManagedHostApi* api);
    using ManagedUpdate = void(__cdecl*)(float deltaSeconds);
    using ManagedShutdown = void(__cdecl*)();
    using ManagedPointerDown = void(__cdecl*)(void* windowHandle,
                                              int32_t x, int32_t y,
                                              uint32_t button);

    using ManagedInvokeUiAction = void(__cdecl*)(const char* actionUtf8);
    using ManagedViewportInput = void(__cdecl*)(void* windowHandle,
                                                 int32_t x, int32_t y,
                                                 uint32_t event, uint32_t buttons,
                                                 int32_t wheelDelta);
    using ManagedViewportKey = void(__cdecl*)(uint32_t key, uint8_t pressed,
                                               uint8_t viewportFocused);
    using ManagedRunMcp = int(__cdecl*)();

    const wchar_t* const UnmanagedCallersOnlyMethod =
        reinterpret_cast<const wchar_t*>(static_cast<uintptr_t>(-1));

    std::wstring Utf8ToWide(const std::string& value)
    {
        if (value.empty())
        {
            return {};
        }

        const int length = MultiByteToWideChar(
            CP_UTF8, 0, value.data(), static_cast<int>(value.size()), nullptr, 0);
        if (length <= 0)
        {
            return {};
        }

        std::wstring result(static_cast<size_t>(length), L'\0');
        MultiByteToWideChar(
            CP_UTF8,
            0,
            value.data(),
            static_cast<int>(value.size()),
            result.data(),
            length);
        return result;
    }

    std::filesystem::path Utf8Path(const std::string& value)
    {
        return std::filesystem::path(Utf8ToWide(value));
    }

    bool ReadJsonString(const std::string& json,
                        std::string_view key,
                        std::string& value)
    {
        const std::string token = "\"" + std::string(key) + "\"";
        size_t keyPosition = json.find(token);
        size_t colon = std::string::npos;
        while (keyPosition != std::string::npos)
        {
            size_t candidate = keyPosition + token.size();
            while (candidate < json.size() &&
                   (json[candidate] == ' ' || json[candidate] == '\t' ||
                    json[candidate] == '\r' || json[candidate] == '\n'))
            {
                ++candidate;
            }

            if (candidate < json.size() && json[candidate] == ':')
            {
                colon = candidate;
                break;
            }

            keyPosition = json.find(token, keyPosition + token.size());
        }
        if (keyPosition == std::string::npos || colon == std::string::npos)
        {
            return false;
        }

        size_t cursor = colon + 1;
        while (cursor < json.size() &&
               (json[cursor] == ' ' || json[cursor] == '\t' ||
                json[cursor] == '\r' || json[cursor] == '\n'))
        {
            ++cursor;
        }

        if (cursor >= json.size() || json[cursor] != '"')
        {
            return false;
        }

        ++cursor;
        value.clear();
        while (cursor < json.size())
        {
            const char character = json[cursor++];
            if (character == '"')
            {
                return true;
            }

            if (character != '\\' || cursor >= json.size())
            {
                value.push_back(character);
                continue;
            }

            const char escaped = json[cursor++];
            switch (escaped)
            {
            case '"': value.push_back('"'); break;
            case '\\': value.push_back('\\'); break;
            case '/': value.push_back('/'); break;
            case 'b': value.push_back('\b'); break;
            case 'f': value.push_back('\f'); break;
            case 'n': value.push_back('\n'); break;
            case 'r': value.push_back('\r'); break;
            case 't': value.push_back('\t'); break;
            default: value.push_back(escaped); break;
            }
        }

        return false;
    }

    std::filesystem::path ResolvePath(const std::filesystem::path& base,
                                      const std::string& value)
    {
        const auto candidate = Utf8Path(value);
        if (candidate.is_absolute())
        {
            return candidate.lexically_normal();
        }
        return (base / candidate).lexically_normal();
    }

    std::filesystem::path ReadEnvironmentPath(const std::string& name)
    {
        const std::wstring wideName = Utf8ToWide(name);
        if (wideName.empty())
        {
            return {};
        }

        wchar_t buffer[32768]{};
        const DWORD length = GetEnvironmentVariableW(
            wideName.c_str(), buffer, static_cast<DWORD>(std::size(buffer)));
        if (length == 0 || length >= std::size(buffer))
        {
            return {};
        }
        return std::filesystem::absolute(
            std::filesystem::path(std::wstring(buffer, length)))
            .lexically_normal();
    }

    std::filesystem::path FindDotNetRoot()
    {
        wchar_t dotnetRoot[MAX_PATH]{};
        const DWORD rootLength = GetEnvironmentVariableW(
            L"DOTNET_ROOT",
            dotnetRoot,
            static_cast<DWORD>(std::size(dotnetRoot)));

        if (rootLength != 0 && rootLength < std::size(dotnetRoot))
        {
            return dotnetRoot;
        }

        wchar_t programFiles[MAX_PATH]{};
        const DWORD length = GetEnvironmentVariableW(
            L"ProgramFiles", programFiles, static_cast<DWORD>(std::size(programFiles)));

        if (length != 0 && length < std::size(programFiles))
        {
            return std::filesystem::path(programFiles) / L"dotnet";
        }

        return L"C:\\Program Files\\dotnet";
    }

    std::filesystem::path FindHostFxr()
    {
        const auto fxrRoot = FindDotNetRoot() / L"host" / L"fxr";
        std::filesystem::path selected;

        std::error_code error;
        for (const auto& entry : std::filesystem::directory_iterator(fxrRoot, error))
        {
            if (!entry.is_directory(error))
            {
                continue;
            }

            const auto candidate = entry.path() / L"hostfxr.dll";
            if (std::filesystem::exists(candidate, error) && candidate > selected)
            {
                selected = candidate;
            }
        }

        return selected;
    }

    void PrintHostError(const char* operation, int result)
    {
        std::cerr << "[ManagedHost] " << operation
                  << " failed, HRESULT=0x" << std::hex
                  << static_cast<uint32_t>(result) << std::dec << std::endl;
    }
}

ManagedHost::~ManagedHost()
{
    Shutdown();
}

bool ResolveManagedProject(const std::filesystem::path& projectManifest,
                           ManagedProjectSelection& selection)
{
    std::ifstream input(projectManifest, std::ios::binary);
    if (!input)
    {
        std::wcerr << L"[ManagedHost] Could not open project manifest: "
                   << projectManifest << std::endl;
        return false;
    }

    std::ostringstream stream;
    stream << input.rdbuf();
    const std::string json = stream.str();

    std::string association;
    std::string requestedEngineId;
    std::string requestedEngineVersion;
    std::string engineManifest = "engine.json";
    if (!ReadJsonString(json, "association", association) ||
        !ReadJsonString(json, "id", requestedEngineId) ||
        !ReadJsonString(json, "version", requestedEngineVersion))
    {
        std::wcerr << L"[ManagedHost] Project manifest requires engine association, id, and version.\n"
                   << projectManifest << std::endl;
        return false;
    }

    ReadJsonString(json, "manifest", engineManifest);

    const auto manifestPath = std::filesystem::absolute(projectManifest).lexically_normal();
    const auto manifestDirectory = manifestPath.parent_path();
    const auto projectRoot = manifestDirectory;

    const auto resolvedEngineRoot = ReadEnvironmentPath("SHARPENGINE_ROOT");
    if (resolvedEngineRoot.empty())
    {
        std::wcerr << L"[ManagedHost] SHARPENGINE_ROOT is not configured. "
                   << L"Open .sharpproject files through SharpEngine.ProjectSelector."
                   << std::endl;
        return false;
    }

    const auto engineManifestPath = ResolvePath(resolvedEngineRoot, engineManifest);
    if (!std::filesystem::exists(engineManifestPath))
    {
        std::wcerr << L"[ManagedHost] Engine manifest is missing: "
                   << engineManifestPath << std::endl;
        return false;
    }
    std::ifstream engineInput(engineManifestPath, std::ios::binary);
    std::ostringstream engineStream;
    engineStream << engineInput.rdbuf();
    std::string resolvedEngineId;
    std::string resolvedEngineVersion;
    if (!ReadJsonString(engineStream.str(), "engineId", resolvedEngineId) ||
        !ReadJsonString(engineStream.str(), "version", resolvedEngineVersion) ||
        resolvedEngineId != requestedEngineId ||
        resolvedEngineVersion != requestedEngineVersion)
    {
        std::wcerr << L"[ManagedHost] The selected Engine does not match the project binding."
                   << std::endl;
        return false;
    }

    selection.ProjectManifest = manifestPath;
    selection.ProjectRoot = projectRoot;
    selection.EngineRoot = resolvedEngineRoot;
    return true;
}

bool ManagedHost::Initialize(const std::wstring& runtimeConfigPath,
                             const std::wstring& assemblyPath,
                             const std::wstring& entryPointTypeName)
{
    Shutdown();

    if (!std::filesystem::exists(runtimeConfigPath) ||
        !std::filesystem::exists(assemblyPath))
    {
        std::wcerr << L"[ManagedHost] Managed files are missing. runtimeConfig="
                   << runtimeConfigPath << L" assembly=" << assemblyPath << std::endl;
        return false;
    }

    const auto hostFxrPath = FindHostFxr();
    if (hostFxrPath.empty())
    {
        std::cerr << "[ManagedHost] Could not find hostfxr.dll." << std::endl;
        return false;
    }

    HostFxrModule = LoadLibraryW(hostFxrPath.c_str());
    if (!HostFxrModule)
    {
        std::wcerr << L"[ManagedHost] LoadLibrary failed for "
                   << hostFxrPath << std::endl;
        return false;
    }

    auto getExport = [this](const char* name) -> FARPROC
    {
        return GetProcAddress(static_cast<HMODULE>(HostFxrModule), name);
    };

    const auto initialize = reinterpret_cast<HostFxrInitializeForRuntimeConfig>(
        getExport("hostfxr_initialize_for_runtime_config"));
    const auto getDelegate = reinterpret_cast<HostFxrGetRuntimeDelegate>(
        getExport("hostfxr_get_runtime_delegate"));
    const auto close = reinterpret_cast<HostFxrClose>(
        getExport("hostfxr_close"));

    if (!initialize || !getDelegate || !close)
    {
        std::cerr << "[ManagedHost] hostfxr exports are incomplete." << std::endl;
        ResetRuntime();
        return false;
    }

    const int initializeResult = initialize(
        runtimeConfigPath.c_str(), nullptr, &RuntimeContext);
    // hostfxr may return a non-zero positive success code when another
    // compatible runtime is already initialized. HRESULT failures are
    // negative.
    if (initializeResult < 0)
    {
        PrintHostError("hostfxr_initialize_for_runtime_config", initializeResult);
        ResetRuntime();
        return false;
    }

    void* loader = nullptr;
    const int delegateResult = getDelegate(
        RuntimeContext,
        HdtLoadAssemblyAndGetFunctionPointer,
        &loader);

    if (delegateResult != 0 || !loader)
    {
        PrintHostError("hostfxr_get_runtime_delegate", delegateResult);
        ResetRuntime();
        return false;
    }

    LoadAssemblyAndGetFunctionPointerFunction = loader;

    auto loadFunction = reinterpret_cast<LoadAssemblyAndGetFunctionPointerFn>(
        LoadAssemblyAndGetFunctionPointerFunction);

    const wchar_t* const typeName = entryPointTypeName.empty()
        ? L"SharpEngine.Game.GameEntrypoint, SharpEngine.Game"
        : entryPointTypeName.c_str();

    const auto loadManagedFunction = [&](const wchar_t* methodName,
                                         void** target) -> bool
    {
        const int result = loadFunction(
            assemblyPath.c_str(),
            typeName,
            methodName,
            UnmanagedCallersOnlyMethod,
            nullptr,
            target);

        if (result != 0 || !*target)
        {
            PrintHostError("load_assembly_and_get_function_pointer", result);
            return false;
        }

        return true;
    };

    if (!loadManagedFunction(L"Initialize", &ManagedInitializeFunction) ||
        !loadManagedFunction(L"Update", &ManagedUpdateFunction) ||
        !loadManagedFunction(L"Shutdown", &ManagedShutdownFunction))
    {
        ResetRuntime();
        return false;
    }

    // PointerDown is an optional extension so older managed game assemblies
    // remain loadable with the versioned native API.
    if (!loadManagedFunction(L"PointerDown", &ManagedPointerDownFunction))
    {
        ManagedPointerDownFunction = nullptr;
    }

    loadManagedFunction(L"InvokeUiAction", &ManagedInvokeUiActionFunction);
    // ViewportInput is optional so older public managed assemblies remain loadable.
    if (!loadManagedFunction(L"ViewportInput", &ManagedViewportInputFunction))
        ManagedViewportInputFunction = nullptr;
    if (!loadManagedFunction(L"ViewportKey", &ManagedViewportKeyFunction))
        ManagedViewportKeyFunction = nullptr;
    if (!loadManagedFunction(L"RunMcp", &ManagedRunMcpFunction))
        ManagedRunMcpFunction = nullptr;
    return true;
}

bool ManagedHost::Start(void* windowHandle,
                        uint32_t width,
                        uint32_t height,
                        const ManagedHostApi* api)
{
    if (!ManagedInitializeFunction)
    {
        return false;
    }

    return reinterpret_cast<ManagedInitialize>(ManagedInitializeFunction)(
               windowHandle, width, height, api) == 0;
}

void ManagedHost::Update(float deltaSeconds)
{
    if (ManagedUpdateFunction)
    {
        reinterpret_cast<ManagedUpdate>(ManagedUpdateFunction)(deltaSeconds);
    }
}

void ManagedHost::PointerDown(void* windowHandle, int32_t x, int32_t y,
                              uint32_t button)
{
    if (ManagedPointerDownFunction)
    {
        reinterpret_cast<ManagedPointerDown>(ManagedPointerDownFunction)(
            windowHandle, x, y, button);
    }
}

void ManagedHost::InvokeUiAction(const char* actionUtf8)
{
    if (ManagedInvokeUiActionFunction) reinterpret_cast<ManagedInvokeUiAction>(ManagedInvokeUiActionFunction)(actionUtf8);
}

void ManagedHost::ViewportInput(void* windowHandle, int32_t x, int32_t y,
                                uint32_t event, uint32_t buttons,
                                int32_t wheelDelta)
{
    if (ManagedViewportInputFunction)
        reinterpret_cast<ManagedViewportInput>(ManagedViewportInputFunction)(
            windowHandle, x, y, event, buttons, wheelDelta);
}

void ManagedHost::ViewportKey(uint32_t key, uint8_t pressed, uint8_t viewportFocused)
{
    if (ManagedViewportKeyFunction)
        reinterpret_cast<ManagedViewportKey>(ManagedViewportKeyFunction)(
            key, pressed, viewportFocused);
}

int ManagedHost::RunMcp()
{
    if (!ManagedRunMcpFunction) return -1;
    return reinterpret_cast<ManagedRunMcp>(ManagedRunMcpFunction)();
}

void ManagedHost::Shutdown()
{
    if (ManagedShutdownFunction)
    {
        reinterpret_cast<ManagedShutdown>(ManagedShutdownFunction)();
        ManagedShutdownFunction = nullptr;
    }

    ResetRuntime();
}

void ManagedHost::ResetRuntime()
{
    ManagedInitializeFunction = nullptr;
    ManagedUpdateFunction = nullptr;
    ManagedShutdownFunction = nullptr;
    ManagedPointerDownFunction = nullptr;
    ManagedInvokeUiActionFunction = nullptr;
    ManagedViewportInputFunction = nullptr;
    ManagedViewportKeyFunction = nullptr;
    ManagedRunMcpFunction = nullptr;
    LoadAssemblyAndGetFunctionPointerFunction = nullptr;

    if (RuntimeContext && HostFxrModule)
    {
        const auto close = reinterpret_cast<HostFxrClose>(
            GetProcAddress(static_cast<HMODULE>(HostFxrModule), "hostfxr_close"));
        if (close)
        {
            close(RuntimeContext);
        }
    }

    RuntimeContext = nullptr;

    if (HostFxrModule)
    {
        FreeLibrary(static_cast<HMODULE>(HostFxrModule));
        HostFxrModule = nullptr;
    }
}

