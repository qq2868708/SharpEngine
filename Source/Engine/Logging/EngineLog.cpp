#include "EngineLog.h"

#include <windows.h>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <sstream>

namespace
{
    std::mutex GMutex;
    std::filesystem::path GFile;
    std::string GSource = "Native";
    HANDLE GNamedMutex{};

    const char* LevelName(EngineLog::Level level)
    {
        switch (level)
        {
        case EngineLog::Level::Trace: return "trace";
        case EngineLog::Level::Debug: return "debug";
        case EngineLog::Level::Info: return "info";
        case EngineLog::Level::Warning: return "warning";
        case EngineLog::Level::Error: return "error";
        case EngineLog::Level::Critical: return "critical";
        }
        return "info";
    }

    std::string Escape(std::string_view value)
    {
        std::string result;
        result.reserve(value.size() + 8);
        for (const unsigned char character : value)
        {
            switch (character)
            {
            case '"': result += "\\\""; break;
            case '\\': result += "\\\\"; break;
            case '\b': result += "\\b"; break;
            case '\f': result += "\\f"; break;
            case '\n': result += "\\n"; break;
            case '\r': result += "\\r"; break;
            case '\t': result += "\\t"; break;
            default:
                if (character < 0x20)
                {
                    std::ostringstream escaped;
                    escaped << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                            << static_cast<int>(character);
                    result += escaped.str();
                }
                else result.push_back(static_cast<char>(character));
                break;
            }
        }
        return result;
    }

    std::string TimestampUtc()
    {
        SYSTEMTIME time{};
        GetSystemTime(&time);
        std::ostringstream result;
        result << std::setfill('0') << std::setw(4) << time.wYear << '-'
               << std::setw(2) << time.wMonth << '-' << std::setw(2) << time.wDay
               << 'T' << std::setw(2) << time.wHour << ':' << std::setw(2) << time.wMinute
               << ':' << std::setw(2) << time.wSecond << '.' << std::setw(3)
               << time.wMilliseconds << 'Z';
        return result.str();
    }

    HANDLE LogMutex()
    {
        if (!GNamedMutex)
            GNamedMutex = CreateMutexW(nullptr, FALSE, L"Local\\SharpEngine.EngineLog");
        return GNamedMutex;
    }
}

namespace EngineLog
{
    void Configure(const std::filesystem::path& root, std::string_view source)
    {
        std::scoped_lock lock(GMutex);
        GSource = source.empty() ? "Native" : std::string(source);
        try
        {
            const auto directory = root / "Saved" / "Logs";
            std::filesystem::create_directories(directory);
            GFile = directory / "Engine.log";
        }
        catch (const std::exception& error)
        {
            GFile.clear();
            std::cerr << "[error][Logging] native file sink setup failed: "
                      << error.what() << std::endl;
        }
    }

    void Write(Level level, std::string_view category, std::string_view message)
    {
        std::scoped_lock lock(GMutex);
        const std::string safeCategory = category.empty()
            ? std::string("General") : std::string(category);
        std::cerr << '[' << LevelName(level) << "][" << safeCategory << "] "
                  << message << std::endl;
        if (GFile.empty()) return;
        const auto mutex = LogMutex();
        bool acquired = false;
        if (mutex)
            acquired = WaitForSingleObject(mutex, 2000) == WAIT_OBJECT_0;
        if (!acquired) return;
        try
        {
            std::ofstream stream(GFile, std::ios::app | std::ios::binary);
            if (stream)
            {
                stream << "{\"schemaVersion\":1,\"timestamp\":\"" << TimestampUtc()
                       << "\",\"level\":\"" << LevelName(level)
                       << "\",\"source\":\"" << Escape(GSource)
                       << "\",\"category\":\"" << Escape(safeCategory)
                       << "\",\"message\":\"" << Escape(message)
                       << "\",\"processId\":" << GetCurrentProcessId()
                       << ",\"threadId\":" << GetCurrentThreadId()
                       << ",\"exception\":null,\"properties\":{}}\n";
                stream.flush();
            }
        }
        catch (const std::exception& error)
        {
            std::cerr << "[error][Logging] native file sink failed: "
                      << error.what() << std::endl;
        }
        ReleaseMutex(mutex);
    }
}
