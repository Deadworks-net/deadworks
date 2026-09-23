#include "MemoryDataLoader.hpp"
#include "Scanner.hpp"
#include "../Lib/Module.hpp"

#include <nlohmann/json.hpp>
#include <fstream>

namespace deadworks {

std::expected<void, std::string> MemoryDataLoader::Load(std::string_view configPath) {
    std::ifstream file(configPath.data());
    if (!file.is_open()) {
        return std::unexpected("Failed to open config file");
    }

    try {
        auto data = nlohmann::json::parse(file, nullptr, false, true);

        for (auto &[key, value] : data["signatures"].items()) {
            auto library = value["library"].get<std::string>();
#ifdef _WIN32
            auto pattern = value["windows"].get<std::string>();
#else
            auto pattern = value["linux"].get<std::string>();
#endif
            Module module{library};
            if (!module.IsValid()) {
                return std::unexpected("Failed to load module");
            }

            const auto scanResult = Scanner::FindFirst(module.GetSectionMemory(".text"), Scanner::ParseSignature(pattern).value());
            if (scanResult.has_value()) {
                m_resolved[key] = *scanResult;
            } else {
                // Keep going: after a game update the whole list is what is wanted, not the first.
                m_missing.push_back(key);
            }
        }

        if (data.contains("patches")) {
            for (auto &[key, value] : data["patches"].items()) {
                auto library = value["library"].get<std::string>();
#ifdef _WIN32
                auto pattern = value["windows"].get<std::string>();
#else
                auto pattern = value["linux"].get<std::string>();
#endif
                Module module{library};
                if (!module.IsValid()) {
                    return std::unexpected("Failed to load module for patch " + key);
                }

                const auto scanResult = Scanner::FindFirst(module.GetSectionMemory(".text"), Scanner::ParseSignature(pattern).value());
                if (scanResult.has_value()) {
                    m_patches[key] = *scanResult;
                } else {
                    m_missing.push_back("patch " + key);
                }
            }
        }

        if (!m_missing.empty()) {
            std::string list;
            for (const auto &key : m_missing) {
                if (!list.empty()) list += ", ";
                list += key;
            }
            return std::unexpected("Failed to find signatures: " + list);
        }

        if (data.contains("virtuals")) {
            for (auto &[key, value] : data["virtuals"].items()) {
                m_virtuals[key] = value.get<int>();
            }
        }
    } catch (nlohmann::json::exception &e) {
        return std::unexpected(std::string("Failed to load config: ") + e.what());
    }

    return {};
}

} // namespace deadworks
