using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using DeadworksManaged.Api;
using DeadworksManaged.Telemetry;

namespace DeadworksManaged;

internal class ServerCredentials
{
    [JsonPropertyName("server_id")]
    public string ServerId { get; set; } = "";

    [JsonPropertyName("server_token")]
    public string ServerToken { get; set; } = "";
}

internal static class ServerBrowser
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static ILogger _logger = null!;
    private static ServerBrowserConfig _config = null!;
    private static ServerCredentials? _credentials;
    private static Timer? _heartbeatTimer;
    private static string _credentialsDir = "";
    private static string _serverName = "";
    private static int _gamePort = 27015;

    public static void Initialize()
    {
        _logger = DeadworksTelemetry.CreateLogger("ServerBrowser");

        var managedDir = Path.GetDirectoryName(typeof(ServerBrowser).Assembly.Location);
        _credentialsDir = Path.GetFullPath(Path.Combine(managedDir!, "..", "configs", "ServerBrowser"));
        _config = DeadworksConfig.ServerBrowser;

        if (_config.Unlisted || Server.HasCommandLineParm("-nomaster"))
        {
            _logger.LogInformation("Server is unlisted - heartbeat disabled");
            return;
        }

        ResolveConVars();
        ApplyServerAddons();
        LoadOrCreateCredentials();
    }

    public static void OnStartupServer()
    {
        if (_config.Unlisted || Server.HasCommandLineParm("-nomaster")) return;
        ApplyServerAddons();
    }

    public static void Shutdown()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    private static void ResolveConVars()
    {
        try
        {
            var hostport = ConVar.Find("hostport");
            if (hostport != null)
            {
                var port = hostport.GetInt();
                if (port > 0) _gamePort = port;
            }

            var hostname = ConVar.Find("hostname");
            if (hostname != null)
            {
                var name = hostname.GetString();
                if (!string.IsNullOrEmpty(name)) _serverName = name;
            }

            _logger.LogInformation("Resolved server config (port={Port}, name={ServerName})", _gamePort, _serverName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve convars");
        }
    }

    private static void LoadOrCreateCredentials()
    {
        var credPath = Path.Combine(_credentialsDir, "credentials.json");

        // Try loading existing credentials
        if (File.Exists(credPath))
        {
            try
            {
                var json = File.ReadAllText(credPath);
                _credentials = JsonSerializer.Deserialize<ServerCredentials>(json, JsonOptions);
                if (_credentials != null && !string.IsNullOrEmpty(_credentials.ServerId) && !string.IsNullOrEmpty(_credentials.ServerToken))
                {
                    _logger.LogInformation("Loaded credentials (serverId={ServerId})", _credentials.ServerId);
                    StartHeartbeat();
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read credentials");
            }
        }

        // Auto-register with the API
        RegisterAsync(credPath);
    }

    private static async void RegisterAsync(string credPath)
    {
        if (await TryRegister(credPath))
        {
            StartHeartbeat();
        }
        else
        {
            // Registration failed - start the heartbeat timer anyway so it retries
            _logger.LogWarning("Will retry registration on next heartbeat tick");
            StartHeartbeat();
        }
    }

    private static async Task<bool> TryRegister(string credPath)
    {
        using var activity = DeadworksTracing.Source.StartActivity("heartbeat.register");

        try
        {
            var url = $"{_config.ApiUrl.TrimEnd('/')}/api/servers/register";
            var payload = new
            {
                name = !string.IsNullOrEmpty(_serverName) ? _serverName : "Deadworks Server",
                port = _gamePort,
                max_players = GlobalVars.IsValid ? GlobalVars.MaxClients : 12
            };

            var response = await Http.PostAsJsonAsync(url, payload);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Auto-registration failed: HTTP {StatusCode}", (int)response.StatusCode);
                activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {(int)response.StatusCode}");
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            var id = result.GetProperty("id").GetString()!;
            var token = result.GetProperty("token").GetString()!;

            _credentials = new ServerCredentials { ServerId = id, ServerToken = token };
            SaveCredentials(credPath);
            _logger.LogInformation("Auto-registered with API (serverId={ServerId})", id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-registration error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return false;
        }
    }

    private static void SaveCredentials(string credPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(credPath)!);
            var json = JsonSerializer.Serialize(_credentials, JsonOptions);
            File.WriteAllText(credPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save credentials");
        }
    }


    private static void StartHeartbeat()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_config.HeartbeatIntervalSeconds, 10));
        _heartbeatTimer = new Timer(_ => SendHeartbeat(), null, interval, interval);
    }

    private static void SendHeartbeatNow()
    {
        if (_credentials == null) return;
        _ = Task.Run(() => SendHeartbeat());
    }

    private static async void SendHeartbeat()
    {
        if (_credentials == null)
        {
            // Retry registration
            var credPath = Path.Combine(_credentialsDir, "credentials.json");
            await TryRegister(credPath);
            return;
        }

        using var activity = DeadworksTracing.Source.StartActivity("heartbeat.send");
        activity?.SetTag("server.id", _credentials.ServerId);

        var sw = Stopwatch.StartNew();
        DeadworksMetrics.HeartbeatsSent.Add(1);

        try
        {
            var payload = BuildPayload();
            var url = $"{_config.ApiUrl.TrimEnd('/')}/api/servers/{_credentials.ServerId}/heartbeat";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _credentials.ServerToken);
            request.Content = JsonContent.Create(payload);

            var response = await Http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Heartbeat failed: HTTP {StatusCode}", (int)response.StatusCode);
                DeadworksMetrics.HeartbeatsFailed.Add(1);
                activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            DeadworksMetrics.HeartbeatsFailed.Add(1);
            _logger.LogError(ex, "Heartbeat error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
        finally
        {
            DeadworksMetrics.HeartbeatDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }

    private static void ApplyServerAddons()
    {
        if (_config.ContentAddons.Count == 0) return;

        var addons = string.Join(",", _config.ContentAddons);
        Server.SetAddons(addons);
        _logger.LogInformation("Server addons set to {Addons}", addons);

        foreach (var addon in _config.ContentAddons)
        {
            var vpkPath = $"deadworks_mods/vpks/{addon}.vpk";
            if (Server.AddSearchPath(vpkPath))
                _logger.LogDebug("Mounted server addon: {VpkPath}", vpkPath);
            else
                _logger.LogWarning("Failed to mount addon: {VpkPath}", vpkPath);
        }
    }

    private static object BuildPayload()
    {
        var players = new List<object>();

        foreach (var controller in Players.GetAll())
        {
            if (controller.IsBot) continue;

            var pawn = controller.GetHeroPawn();
            var stats = pawn?.PlayerData;

            players.Add(new
            {
                name = controller.PlayerName ?? "",
                hero = stats != null ? ((Heroes)stats.HeroID).ToDisplayName() : "",
                team = pawn?.TeamNum ?? 0,
                kills = stats?.PlayerKills ?? 0,
                deaths = stats?.Deaths ?? 0,
                assists = stats?.PlayerAssists ?? 0,
                level = stats?.Level ?? 0,
            });
        }

        var gameMode = "";
        if (GameRules.IsValid)
            gameMode = GameRules.GameMode.ToString();

        return new
        {
            player_count = players.Count,
            max_players = GlobalVars.IsValid ? GlobalVars.MaxClients : 0,
            map = Server.MapName ?? "",
            game_mode = gameMode,
            players,
            mods = PluginRegistry.GetLoadedPluginNames()
                .Select(n => new { name = n, type = "plugin", version = "1.0.0" })
                .ToList<object>(),
            content_addons = _config.ContentAddons,
            extra_maps = _config.ExtraMaps,
            name = ConVar.Find("hostname")?.GetString() ?? _serverName,
            version = typeof(ServerBrowser).Assembly.GetName().Version?.ToString() ?? "",
            port = _gamePort,
        };
    }
}
