#pragma once

#include <string_view>
#include <map>
#include <string>
#include <vector>
#include <expected>
#include <optional>
#include <cstdint>

namespace deadworks {

class MemoryDataLoader {
public:
    static MemoryDataLoader &Get() {
        static MemoryDataLoader instance;
        return instance;
    }

    std::expected<void, std::string> Load(std::string_view configPath);

    // Signatures Load() could not find. Non-empty means the game build is one this Deadworks
    // does not match, as opposed to a broken install or config.
    const std::vector<std::string> &GetMissing() const { return m_missing; }

    std::optional<uintptr_t> GetOffset(const std::string &key) {
        const auto it = m_resolved.find(key);
        if (it == m_resolved.end()) return std::nullopt;
        return it->second;
    }

    std::optional<int> GetVirtual(const std::string &key) {
        const auto it = m_virtuals.find(key);
        if (it == m_virtuals.end()) return std::nullopt;
        return it->second;
    }

    std::optional<uintptr_t> GetPatch(const std::string &key) {
        const auto it = m_patches.find(key);
        if (it == m_patches.end()) return std::nullopt;
        return it->second;
    }

private:
    std::map<std::string, uintptr_t> m_resolved;
    std::map<std::string, uintptr_t> m_patches;
    std::map<std::string, int> m_virtuals;
    std::vector<std::string> m_missing;
};

} // namespace deadworks
