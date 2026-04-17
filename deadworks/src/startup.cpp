#include "Lib/Module.hpp"
#include "Memory/MemoryDataLoader.hpp"
#include "Memory/Scanner.hpp"
#include "Logging/ConsoleLogger.hpp"
#include "Logging/LogOptions.hpp"
#include "Core/Hooks/CoreHooks.hpp"

#include <vector>

using namespace std::literals;

using Source2MainFn = int (*)(void *hInstance, void *hPrevInstance, const char *pszCmdLine, int nShowCmd, const char *pszBaseDir, const char *pszGame);

int main(int argc, char **argv) {
    auto log = deadworks::ConsoleLogger{"bootstrap"};

    // Resolve paths from executable location, not cwd
    auto exePath = std::filesystem::path(argv[0]).parent_path();
    if (exePath.empty()) exePath = std::filesystem::current_path();
    else exePath = std::filesystem::absolute(exePath);

    auto serverModule = deadworks::Module((exePath / "../../citadel/bin/win64/server.dll").string());

    auto engineModule = deadworks::Module{"engine2.dll"};
    if (!engineModule.IsValid()) {
        log.Critical("Failed to load engine2");
        return 1;
    }

    auto Source2Main = engineModule.GetSymbol<Source2MainFn>("Source2Main");
    if (!Source2Main) {
        log.Critical("Failed to get Source2Main");
        return 1;
    }

    auto &data = deadworks::MemoryDataLoader::Get();
    auto loadResult = data.Load((exePath / "../../citadel/cfg/deadworks_mem.jsonc").string());
    if (!loadResult.has_value()) {
        log.Critical("Failed to load data: {}", loadResult.error());
        return 1;
    }

    std::array requiredSignatures = {
        "UTIL_Remove",
        "CMaterialSystem2AppSystemDict::OnAppSystemLoaded",
        "CServerSideClientBase::FilterMessage",
        "GetVDataInstanceByName",
        "CModifierProperty::AddModifier"};

    for (const auto &signature : requiredSignatures) {
        if (!data.GetOffset(signature).has_value()) {
            log.Critical("Failed to get signature {}", signature);
            return 1;
        }
    }

    auto onAppSystemLoaded = data.GetOffset("CMaterialSystem2AppSystemDict::OnAppSystemLoaded");
    if (!onAppSystemLoaded.has_value()) {
        log.Critical("Failed to get OnAppSystemLoaded");
        return 1;
    }

    // todo abstract
    deadworks::hooks::g_OnAppSystemLoaded = safetyhook::create_inline(onAppSystemLoaded.value(), deadworks::hooks::Hook_OnAppSystemLoaded);

    constexpr auto DEFAULT_CMD_LINE = "-dedicated -console -dev -insecure -allow_no_lobby_connect +tv_citadel_auto_record 0 +spec_replay_enable 0 +tv_enable 0 +citadel_upload_replay_enabled 0 +hostport 27015 +map dl_midtown"sv;

    constexpr std::string_view kLogLevelFlag = "-dw_loglevel";

    std::vector<std::string> passedArgs;
    for (int i = 1; i < argc; i++) {
        std::string_view arg = argv[i];

        if (arg == kLogLevelFlag) {
            if (i + 1 >= argc) {
                log.Warning("{} requires a value (verbose|debug|info|warning|error|critical)", kLogLevelFlag);
                continue;
            }
            if (auto v = deadworks::ParseVerbosity(argv[i + 1])) {
                deadworks::g_FileLogLevel = *v;
                log.Info("Log level set to {}", argv[i + 1]);
            } else {
                log.Warning("Unknown log level '{}', keeping default", argv[i + 1]);
            }
            ++i;
            continue;
        }
        if (arg.starts_with(std::string(kLogLevelFlag) + "=")) {
            auto value = arg.substr(kLogLevelFlag.size() + 1);
            if (auto v = deadworks::ParseVerbosity(value)) {
                deadworks::g_FileLogLevel = *v;
                log.Info("Log level set to {}", value);
            } else {
                log.Warning("Unknown log level '{}', keeping default", value);
            }
            continue;
        }

        passedArgs.emplace_back(arg);
    }

    std::string cmdLine;
    if (!passedArgs.empty()) {
        for (size_t i = 0; i < passedArgs.size(); i++) {
            if (i > 0) cmdLine += ' ';
            cmdLine += passedArgs[i];
        }
    } else {
        cmdLine = DEFAULT_CMD_LINE;
    }

    log.Info("handoff to Source2Main. have fun!!");
    int res = Source2Main(nullptr, nullptr, cmdLine.c_str(), 0, exePath.string().c_str(), "citadel");
    return res;
}
