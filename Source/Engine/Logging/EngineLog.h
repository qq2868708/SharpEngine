#pragma once

#include <filesystem>
#include <string_view>

namespace EngineLog
{
    enum class Level
    {
        Trace,
        Debug,
        Info,
        Warning,
        Error,
        Critical,
    };

    void Configure(const std::filesystem::path& root, std::string_view source,
                   bool standardOutputReserved = false);
    void Write(Level level, std::string_view category, std::string_view message);

    inline void Trace(std::string_view category, std::string_view message)
    { Write(Level::Trace, category, message); }
    inline void Debug(std::string_view category, std::string_view message)
    { Write(Level::Debug, category, message); }
    inline void Info(std::string_view category, std::string_view message)
    { Write(Level::Info, category, message); }
    inline void Warning(std::string_view category, std::string_view message)
    { Write(Level::Warning, category, message); }
    inline void Error(std::string_view category, std::string_view message)
    { Write(Level::Error, category, message); }
    inline void Critical(std::string_view category, std::string_view message)
    { Write(Level::Critical, category, message); }
}
