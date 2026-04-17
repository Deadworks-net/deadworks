#pragma once

#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <string>

#include "Logger.hpp"
#include "../Utils/Chrono.hpp"

namespace deadworks {

class TeeLogger : public Logger {
public:
    TeeLogger(std::unique_ptr<Logger> inner,
              const std::filesystem::path &filePath,
              LoggingVerbosity fileMinVerbosity = LoggingVerbosity::Info)
        : m_inner(std::move(inner)), m_fileMin(fileMinVerbosity) {
        m_file.open(filePath, std::ios::out | std::ios::app);
    }

    void Log(LoggingVerbosity verbosity, std::string_view message) override {
        if (m_inner) m_inner->Log(verbosity, message);

        if (verbosity < m_fileMin) return;

        std::lock_guard<std::mutex> lock(m_fileMutex);
        if (!m_file.is_open()) return;

        const auto ts = utils::FormattedNow();
        m_file << '[' << ts << "] [" << GetVerbosityName(verbosity) << "] " << message << '\n';
        m_file.flush();
    }

private:
    std::unique_ptr<Logger> m_inner;
    LoggingVerbosity m_fileMin;
    std::ofstream m_file;
    std::mutex m_fileMutex;
};

} // namespace deadworks
