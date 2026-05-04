using DeadworksManaged.Api;
using DeadworksManaged.Commands;
using Xunit;

namespace DeadworksManaged.Tests;

public sealed class CommandManifestTests : IDisposable
{
    private readonly string _tempDir;

    public CommandManifestTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "deadworks-command-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        PluginCommandManifestManager.Initialize(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);

        PluginRegistrationTracker.Clear();
        ConCommandManager.Clear();
    }

    [Fact]
    public void LoadOrCreate_writes_empty_manifest_for_plugins_without_commands()
    {
        var commands = PluginCommandManifestManager.LoadOrCreate("EmptyPlugin", "empty", []);

        Assert.Empty(commands);

        var json = File.ReadAllText(Path.Combine(_tempDir, "EmptyPlugin", "EmptyPlugin.commands.jsonc"));
        Assert.Contains("\"plugin\": \"empty\"", json);
        Assert.Contains("\"commands\": []", json);
    }

    [Fact]
    public void LoadOrCreate_writes_default_command_manifest()
    {
        var commands = PluginCommandManifestManager.LoadOrCreate("ModerationPlugin", "moderation", [
            new PluginCommandManifestSource
            {
                Id = "moderation.ban",
                Name = "ban",
                Aliases = ["banish"],
                Description = "Ban a player from the server",
                Permission = "moderation.player.ban"
            }
        ]);

        Assert.True(commands.TryGetValue("moderation.ban", out var command));
        Assert.Equal("ban", command.Name);
        Assert.Equal(["banish"], command.Aliases ?? []);
        Assert.Equal("moderation.player.ban", command.Permission);

        var path = Path.Combine(_tempDir, "ModerationPlugin", "ModerationPlugin.commands.jsonc");
        var json = File.ReadAllText(path);
        Assert.Contains("\"plugin\": \"moderation\"", json);
        Assert.Contains("\"id\": \"moderation.ban\"", json);
        Assert.Contains("\"name\": \"ban\"", json);
        Assert.Contains("\"aliases\":", json);
    }

    [Fact]
    public void LoadOrCreate_moves_legacy_commands_folder_manifest_to_plugin_config_folder()
    {
        var legacyPath = Path.Combine(_tempDir, "commands", "ModerationPlugin", "ModerationPlugin.commands.jsonc");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath,
            """
            {
              "plugin": "moderation",
              "commands": [
                {
                  "id": "moderation.ban",
                  "name": "my_ban",
                  "aliases": ["mb"],
                  "description": "Custom ban description",
                  "permission": "custom.ban"
                }
              ]
            }
            """);

        var commands = PluginCommandManifestManager.LoadOrCreate("ModerationPlugin", "moderation", [
            new PluginCommandManifestSource
            {
                Id = "moderation.ban",
                Name = "ban",
                Aliases = ["banish"],
                Description = "Ban a player from the server",
                Permission = "moderation.player.ban"
            }
        ]);

        var path = Path.Combine(_tempDir, "ModerationPlugin", "ModerationPlugin.commands.jsonc");
        Assert.False(File.Exists(legacyPath));
        Assert.True(File.Exists(path));
        Assert.Equal("my_ban", commands["moderation.ban"].Name);
        Assert.Equal(["mb"], commands["moderation.ban"].Aliases ?? []);
        Assert.Equal("custom.ban", commands["moderation.ban"].Permission);
    }

    [Fact]
    public void LoadOrCreate_uses_existing_overrides_merges_new_source_commands_and_removes_stale_ids()
    {
        var manifestPath = Path.Combine(_tempDir, "ModerationPlugin", "ModerationPlugin.commands.jsonc");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath,
            """
            {
              "plugin": "moderation",
              "commands": [
                {
                  "id": "moderation.ban",
                  "name": "my_ban",
                  "aliases": ["mb", "/bad", "my_ban"],
                  "description": "Custom ban description",
                  "permission": "custom.ban"
                },
                {
                  "id": "moderation.old",
                  "name": "old",
                  "aliases": [],
                  "description": "Removed command",
                  "permission": "moderation.player.old"
                }
              ]
            }
            """);

        var commands = PluginCommandManifestManager.LoadOrCreate("ModerationPlugin", "moderation", [
            new PluginCommandManifestSource
            {
                Id = "moderation.ban",
                Name = "ban",
                Aliases = ["banish"],
                Description = "Ban a player from the server",
                Permission = "moderation.player.ban"
            },
            new PluginCommandManifestSource
            {
                Id = "moderation.kick",
                Name = "kick",
                Description = "Kick a player from the server",
                Permission = "moderation.player.kick"
            }
        ]);

        Assert.Equal("my_ban", commands["moderation.ban"].Name);
        Assert.Equal(["mb"], commands["moderation.ban"].Aliases ?? []);
        Assert.Equal("Custom ban description", commands["moderation.ban"].Description);
        Assert.Equal("custom.ban", commands["moderation.ban"].Permission);

        Assert.Equal("kick", commands["moderation.kick"].Name);
        Assert.Equal("moderation.player.kick", commands["moderation.kick"].Permission);

        var json = File.ReadAllText(manifestPath);
        Assert.Contains("\"id\": \"moderation.kick\"", json);
        Assert.DoesNotContain("\"id\": \"moderation.old\"", json);
    }

    [Fact]
    public void LoadOrCreate_migrates_known_admin_default_permissions_without_overwriting_custom_permissions()
    {
        var manifestPath = Path.Combine(_tempDir, "AdminPlugin", "AdminPlugin.commands.jsonc");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath,
            """
            {
              "plugin": "Admin",
              "commands": [
                {
                  "id": "admin.map",
                  "name": "map",
                  "aliases": [],
                  "description": "Change map",
                  "permission": "server.map"
                },
                {
                  "id": "admin.ban",
                  "name": "ban",
                  "aliases": [],
                  "description": "Ban player",
                  "permission": "custom.ban"
                }
              ]
            }
            """);

        var commands = PluginCommandManifestManager.LoadOrCreate("AdminPlugin", "Admin", [
            new PluginCommandManifestSource
            {
                Id = "admin.map",
                Name = "map",
                Description = "Change map",
                Permission = "admin.changemap"
            },
            new PluginCommandManifestSource
            {
                Id = "admin.ban",
                Name = "ban",
                Description = "Ban player",
                Permission = "admin.ban"
            }
        ]);

        Assert.Equal("admin.changemap", commands["admin.map"].Permission);
        Assert.Equal("custom.ban", commands["admin.ban"].Permission);

        var json = File.ReadAllText(manifestPath);
        Assert.Contains("\"permission\": \"admin.changemap\"", json);
        Assert.Contains("\"permission\": \"custom.ban\"", json);
    }

    [Fact]
    public void CommandRegistration_uses_manifest_names_aliases_and_permissions()
    {
        var manifestPath = Path.Combine(_tempDir, nameof(ManifestCommandPlugin), $"{nameof(ManifestCommandPlugin)}.commands.jsonc");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath,
            """
            {
              "plugin": "moderation",
              "commands": [
                {
                  "id": "moderation.ban",
                  "name": "my_ban",
                  "aliases": ["mb"],
                  "description": "Custom ban description",
                  "permission": ""
                }
              ]
            }
            """);

        ConCommandManager.Clear();
        PluginRegistrationTracker.Clear();

        var plugin = new ManifestCommandPlugin();
        var chatRegistry = new HandlerRegistry<string, Func<ChatCommandContext, HookResult>>(StringComparer.OrdinalIgnoreCase);

        CommandRegistration.RegisterPluginCommands("manifest-test-plugin", [plugin], chatRegistry);

        Assert.NotNull(chatRegistry.Snapshot("my_ban"));
        Assert.NotNull(chatRegistry.Snapshot("mb"));
        Assert.Null(chatRegistry.Snapshot("ban"));
        Assert.Null(chatRegistry.Snapshot("banish"));

        Assert.True(ConCommandManager.IsRegistered("dw_my_ban"));
        Assert.True(ConCommandManager.IsRegistered("dw_mb"));
        Assert.False(ConCommandManager.IsRegistered("dw_ban"));
        Assert.False(ConCommandManager.IsRegistered("dw_banish"));

        var handler = chatRegistry.Snapshot("my_ban")!.Single();
        var result = handler(new ChatCommandContext(
            new ChatMessage
            {
                SenderSlot = 0,
                ChatText = "/my_ban",
                AllChat = false,
                LaneColor = LaneColor.Invalid
            },
            "my_ban",
            [],
            '/'));

        Assert.Equal(HookResult.Handled, result);
        Assert.Equal(1, plugin.Calls);
    }

    private sealed class ManifestCommandPlugin : IDeadworksPlugin
    {
        public string Name => "moderation";
        public int Calls { get; private set; }

        public void OnLoad(bool isReload) { }
        public void OnUnload() { }

        [Command("ban", "banish", Description = "Ban a player from the server", Permission = "moderation.player.ban")]
        private void Ban()
        {
            Calls++;
        }
    }
}
