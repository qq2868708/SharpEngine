#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#include <filesystem>
#include <string>
#include <string_view>
#include <vector>

namespace RmlUiResourcePath
{
    inline std::filesystem::path FromUtf8(std::string_view Value)
    {
        return std::filesystem::path(std::u8string(Value.begin(), Value.end()));
    }

    inline std::filesystem::path ProjectRootFromEnvironment()
    {
        const auto Size = GetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST", nullptr, 0);
        if (Size == 0)
        {
            return {};
        }
        std::wstring Manifest(Size, L'\0');
        const auto Written = GetEnvironmentVariableW(L"SHARPENGINE_PROJECT_MANIFEST", Manifest.data(), Size);
        if (Written == 0 || Written >= Size)
        {
            return {};
        }
        Manifest.resize(Written);
        return std::filesystem::path(Manifest).parent_path();
    }

    inline bool IsAllowed(std::string_view Relative)
    {
        if (Relative.empty() || Relative.front() == '/' || Relative.find_first_of("\\:?#") != std::string_view::npos)
        {
            return false;
        }
        std::vector<std::string_view> Segments;
        size_t Start = 0;
        while (Start <= Relative.size())
        {
            const auto End = Relative.find('/', Start);
            const auto Segment = Relative.substr(Start, End == std::string_view::npos ? Relative.size() - Start : End - Start);
            if (Segment.empty() || Segment == "." || Segment == "..")
            {
                return false;
            }
            Segments.push_back(Segment);
            if (End == std::string_view::npos)
            {
                break;
            }
            Start = End + 1;
        }
        return Segments[0] == "Resources" ||
            (Segments.size() >= 4 && Segments[0] == "Plugins" && Segments[2] == "Resources");
    }

    inline std::filesystem::path Resolve(const std::filesystem::path& EngineRoot,
        const std::filesystem::path& ProjectRoot, std::string_view Value)
    {
        constexpr std::string_view Scheme = "sharpengine-resource://";
        if (!Value.starts_with(Scheme))
        {
            return FromUtf8(Value);
        }
        const auto Payload = Value.substr(Scheme.size());
        const auto Separator = Payload.find('/');
        if (Separator == std::string_view::npos || Separator == 0 || Separator + 1 >= Payload.size())
        {
            return {};
        }
        const auto Owner = Payload.substr(0, Separator);
        const auto Relative = Payload.substr(Separator + 1);
        const auto Root = Owner == "engine" ? EngineRoot : Owner == "project" ? ProjectRoot : std::filesystem::path{};
        if (Root.empty() || !IsAllowed(Relative))
        {
            return {};
        }
        const auto NormalizedRoot = Root.lexically_normal();
        const auto Result = (NormalizedRoot / FromUtf8(Relative)).lexically_normal();
        const auto Within = Result.lexically_relative(NormalizedRoot).generic_string();
        if (Within.empty() || Within == ".." || Within.starts_with("../"))
        {
            return {};
        }
        return Result;
    }
}
