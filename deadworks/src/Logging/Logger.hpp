#pragma once

#include <optional>
#include <string>
#include <string_view>
#include <format>

namespace deadworks {
enum class LoggingVerbosity {
    Verbose,
    Debug,
    Info,
    Warning,
    Error,
    Critical,
    Fatal,
};

inline std::string_view GetVerbosityName(LoggingVerbosity verbosity) {
    switch (verbosity) {
    case LoggingVerbosity::Verbose:
        return "VRB";
    case LoggingVerbosity::Debug:
        return "DBG";
    case LoggingVerbosity::Info:
        return "INF";
    case LoggingVerbosity::Warning:
        return "WRN";
    case LoggingVerbosity::Error:
        return "ERR";
    case LoggingVerbosity::Critical:
        return "CRT";
    }
    return "???";
}

inline std::optional<LoggingVerbosity> ParseVerbosity(std::string_view s) {
    std::string lower;
    lower.reserve(s.size());
    for (char c : s) lower.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(c))));

    if (lower == "verbose") return LoggingVerbosity::Verbose;
    if (lower == "debug") return LoggingVerbosity::Debug;
    if (lower == "info") return LoggingVerbosity::Info;
    if (lower == "warning") return LoggingVerbosity::Warning;
    if (lower == "error") return LoggingVerbosity::Error;
    if (lower == "critical") return LoggingVerbosity::Critical;
    return std::nullopt;
}

class Logger {
public:
    virtual void Log(LoggingVerbosity verbosity, std::string_view message) = 0;

    template <typename... Args>
    void Verbose(std::format_string<Args...> fmt, Args &&...args) {
        std::string fmtd = std::format(fmt, std::forward<Args>(args)...);
        Log(LoggingVerbosity::Verbose, fmtd);
    }

    void Verbose(std::string_view message) {
        Log(LoggingVerbosity::Verbose, message);
    }

    template <typename... Args>
    void Debug(std::format_string<Args...> fmt, Args &&...args) {
        std::string fmtd = std::format(fmt, std::forward<Args>(args)...);
        Log(LoggingVerbosity::Debug, fmtd);
    }

    void Debug(std::string_view message) {
        Log(LoggingVerbosity::Debug, message);
    }

    template <typename... Args>
    void Info(std::format_string<Args...> fmt, Args &&...args) {
        std::string fmtd = std::format(fmt, std::forward<Args>(args)...);
        Log(LoggingVerbosity::Info, fmtd);
    }

    void Info(std::string_view message) {
        Log(LoggingVerbosity::Info, message);
    }

    template <typename... Args>
    void Warning(std::format_string<Args...> fmt, Args &&...args) {
        std::string fmtd = std::format(fmt, std::forward<Args>(args)...);
        Log(LoggingVerbosity::Warning, fmtd);
    }

    void Warning(std::string_view message) {
        Log(LoggingVerbosity::Warning, message);
    }

    template <typename... Args>
    void Error(std::format_string<Args...> fmt, Args &&...args) {
        std::string fmtd = std::format(fmt, std::forward<Args>(args)...);
        Log(LoggingVerbosity::Error, fmtd);
    }

    void Error(std::string_view message) {
        Log(LoggingVerbosity::Error, message);
    }

    template <typename... Args>
    void Critical(std::format_string<Args...> fmt, Args &&...args) {
        std::string fmtd = std::format(fmt, std::forward<Args>(args)...);
        Log(LoggingVerbosity::Critical, fmtd);
    }

    void Critical(std::string_view message) {
        Log(LoggingVerbosity::Critical, message);
    }
};
} // namespace deadworks
