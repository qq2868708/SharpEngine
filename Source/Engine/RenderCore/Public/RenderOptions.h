#pragma once

#include "RenderCore.h"
#include <string>
#include <string_view>

// Shared by Editor and Game. Selection is transient launch configuration,
// independent of the host target; invalid or conflicting choices fail early.
struct RenderOptions
{
    RenderBackendType Backend = RenderBackendType::OpenGL;
    bool EnableVSync = true;
    bool EnableValidation = false;
    bool BackendExplicit = false;
    bool VSyncExplicit = false;
    bool ValidationExplicit = false;

    bool Parse(int& index, int argc, wchar_t** argv, std::string& error)
    {
        const std::wstring_view argument(argv[index]);
        constexpr std::wstring_view BackendPrefix = L"--render-backend=";
        constexpr std::wstring_view VSyncPrefix = L"--render-vsync=";
        constexpr std::wstring_view ValidationPrefix = L"--render-validation=";
        std::wstring_view value;
        bool BackendArgument = false;
        bool VSyncArgument = false;
        if (argument == L"--render-backend")
        {
            if (index + 1 >= argc)
            {
                error = "RENDER_BACKEND_REQUIRED: use --render-backend=opengl|vulkan";
                return true;
            }
            value = argv[++index];
            BackendArgument = true;
        }
        else if (argument.starts_with(BackendPrefix))
        {
            value = argument.substr(BackendPrefix.size());
            BackendArgument = true;
        }
        else if (argument == L"--render-vsync")
        {
            if (index + 1 >= argc)
            {
                error = "RENDER_VSYNC_REQUIRED: use --render-vsync=on|off";
                return true;
            }
            value = argv[++index];
            VSyncArgument = true;
        }
        else if (argument.starts_with(VSyncPrefix))
        {
            value = argument.substr(VSyncPrefix.size());
            VSyncArgument = true;
        }
        else if (argument == L"--render-validation")
        {
            if (index + 1 >= argc)
            {
                error = "RENDER_VALIDATION_REQUIRED: use --render-validation=on|off";
                return true;
            }
            value = argv[++index];
        }
        else if (argument.starts_with(ValidationPrefix))
        {
            value = argument.substr(ValidationPrefix.size());
        }
        else
        {
            return false;
        }

        if (VSyncArgument)
        {
            bool SelectedVSync;
            if (value == L"on")
            {
                SelectedVSync = true;
            }
            else if (value == L"off")
            {
                SelectedVSync = false;
            }
            else
            {
                error = "RENDER_VSYNC_INVALID: expected on or off";
                return true;
            }
            if (VSyncExplicit && SelectedVSync != EnableVSync)
            {
                error = "RENDER_VSYNC_CONFLICT: select one VSync mode";
            }
            else
            {
                EnableVSync = SelectedVSync;
                VSyncExplicit = true;
            }
            return true;
        }

        if (!BackendArgument)
        {
            bool SelectedValidation;
            if (value == L"on")
            {
                SelectedValidation = true;
            }
            else if (value == L"off")
            {
                SelectedValidation = false;
            }
            else
            {
                error = "RENDER_VALIDATION_INVALID: expected on or off";
                return true;
            }
            if (ValidationExplicit && SelectedValidation != EnableValidation)
            {
                error = "RENDER_VALIDATION_CONFLICT: select one validation mode";
            }
            else
            {
                EnableValidation = SelectedValidation;
                ValidationExplicit = true;
            }
            return true;
        }

        RenderBackendType SelectedBackend;
        if (value == L"opengl")
        {
            SelectedBackend = RenderBackendType::OpenGL;
        }
        else if (value == L"vulkan")
        {
            SelectedBackend = RenderBackendType::Vulkan;
        }
        else
        {
            error = "RENDER_BACKEND_INVALID: expected opengl or vulkan";
            return true;
        }
        if (BackendExplicit && SelectedBackend != Backend)
        {
            error = "RENDER_BACKEND_CONFLICT: select one graphics backend";
        }
        else
        {
            Backend = SelectedBackend;
            BackendExplicit = true;
        }
        return true;
    }

    const char* Name() const { return Backend == RenderBackendType::Vulkan ? "vulkan" : "opengl"; }
};
