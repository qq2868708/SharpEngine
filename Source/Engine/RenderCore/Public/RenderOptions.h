#pragma once

#include "RenderCore.h"
#include <string>
#include <string_view>

// Shared by Editor and Game. Selection is transient launch configuration,
// independent of the host target; invalid or conflicting choices fail early.
struct RenderOptions
{
    RenderBackendType Backend = RenderBackendType::OpenGL;
    bool Explicit = false;

    bool Parse(int& index, int argc, wchar_t** argv, std::string& error)
    {
        const std::wstring_view argument(argv[index]);
        constexpr std::wstring_view prefix = L"--render-backend=";
        std::wstring_view value;
        if (argument == L"--render-backend")
        {
            if (index + 1 >= argc)
            {
                error = "RENDER_BACKEND_REQUIRED: use --render-backend=opengl|vulkan";
                return true;
            }
            value = argv[++index];
        }
        else if (argument.starts_with(prefix))
        {
            value = argument.substr(prefix.size());
        }
        else
        {
            return false;
        }

        RenderBackendType selected;
        if (value == L"opengl")
        {
            selected = RenderBackendType::OpenGL;
        }
        else if (value == L"vulkan")
        {
            selected = RenderBackendType::Vulkan;
        }
        else
        {
            error = "RENDER_BACKEND_INVALID: expected opengl or vulkan";
            return true;
        }
        if (Explicit && selected != Backend)
        {
            error = "RENDER_BACKEND_CONFLICT: select one graphics backend";
        }
        else
        {
            Backend = selected;
            Explicit = true;
        }
        return true;
    }

    const char* Name() const { return Backend == RenderBackendType::Vulkan ? "vulkan" : "opengl"; }
};
