#pragma once

#include <algorithm>
#include <string>
#include <string_view>

namespace RmlUiSourceUrl
{
    // StreamFile escapes Windows drive colons before constructing Rml::URL.
    // LoadDocumentFromMemory constructs that URL directly, so mirror the same convention.
    inline std::string FromFilePath(std::string_view Path)
    {
        std::string Result(Path);
        std::replace(Result.begin(), Result.end(), '\\', '/');
        const bool HasDriveLetter = Result.size() >= 3 &&
            ((Result[0] >= 'A' && Result[0] <= 'Z') || (Result[0] >= 'a' && Result[0] <= 'z')) &&
            Result[1] == ':' && Result[2] == '/';
        if (HasDriveLetter)
        {
            Result[1] = '|';
        }
        return Result;
    }
}
