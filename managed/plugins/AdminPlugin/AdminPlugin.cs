using System.Text.Json.Serialization;
using DeadworksManaged.Api;

namespace DeadworksAdmin;

/// <summary>Settings in <c>configs/AdminPlugin/AdminPlugin.jsonc</c>.</summary>
public sealed class AdminPluginConfig : IConfig
{
    [JsonPropertyName("default_kick_reason")]
    public string DefaultKickReason { get; set; } = "Kicked by an admin";

    [JsonPropertyName("default_ban_reason")]
    public string DefaultBanReason { get; set; } = "Banned by an admin";

    [JsonPropertyName("default_gag_reason")]
    public string DefaultGagReason { get; set; } = "Gagged by an admin";

    /// <summary>When true, ban, addban and gag refuse to run without a reason.</summary>
    [JsonPropertyName("require_reason")]
    public bool RequireReason { get; set; }

    [JsonPropertyName("map_change_delay_seconds")]
    public int MapChangeDelaySeconds { get; set; } = 3;

    public void Validate()
    {
        MapChangeDelaySeconds = Math.Clamp(MapChangeDelaySeconds, 0, 60);
    }
}

/// <summary>
/// The admin commands that ship with Deadworks: moderation (<c>admin.moderation.*</c>) and server control
/// (<c>admin.server.*</c>). Penalties, announcements and the admin log are core features; this plugin only
/// decides who may use them and how.
/// </summary>
public sealed partial class AdminPlugin : DeadworksPluginBase
{
    public override string Name => "Admin";

    [PluginConfig]
    public AdminPluginConfig Config { get; set; } = new();
}

/// <summary>
/// Every permission the plugin uses, in one place. None is also the parent of another: a permission covers everything
/// its command can do (ban includes permanent and offline bans, cvar includes sv_cheats and password cvars).
/// </summary>
internal static class Perm
{
    public const string Kick = "admin.moderation.kick";
    public const string Ban = "admin.moderation.ban";
    public const string Unban = "admin.moderation.unban";
    public const string Gag = "admin.moderation.gag";
    public const string Slay = "admin.moderation.slay";
    public const string Who = "admin.moderation.who";

    public const string Map = "admin.server.map";
    public const string Rcon = "admin.server.rcon";
    public const string Cvar = "admin.server.cvar";
    public const string Config = "admin.server.config";
}
