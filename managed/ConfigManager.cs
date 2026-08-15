using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using DeadworksManaged.Api;
using DeadworksManaged.Telemetry;

namespace DeadworksManaged;

internal static class ConfigManager
{
	private static ILogger _logger = null!;
	private static string _configsDir = "";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public static void Initialize()
	{
		_logger = DeadworksTelemetry.CreateLogger("ConfigManager");

		// Configs live as a sibling of managed/ (e.g. game/bin/win64/configs/)
		// so they survive the post-build rmdir of managed/.
		var managedDir = Path.GetDirectoryName(typeof(ConfigManager).Assembly.Location);
		_configsDir = Path.GetFullPath(Path.Combine(managedDir!, "..", "configs"));

		ConfigResolver.ReloadConfig = ReloadConfig;
		ConfigResolver.GetConfigPath = GetConfigPath;
	}

	public static void LoadConfig(IDeadworksPlugin plugin)
	{
		var prop = FindConfigProperty(plugin);
		if (prop == null)
			return;

		LoadConfigForProperty(plugin, prop, isReload: false);
	}

	private static bool ReloadConfig(IDeadworksPlugin plugin)
	{
		var prop = FindConfigProperty(plugin);
		if (prop == null)
			return false;

		if (!LoadConfigForProperty(plugin, prop, isReload: true))
			return false;

		try
		{
			plugin.OnConfigReloaded();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "{PluginName}.OnConfigReloaded() threw", plugin.Name);
		}

		return true;
	}

	private static string GetConfigKey(IDeadworksPlugin plugin) => plugin.GetType().Name;

	private static string? GetConfigPath(IDeadworksPlugin plugin)
	{
		var key = GetConfigKey(plugin);
		var filePath = Path.Combine(_configsDir, key, $"{key}.jsonc");
		return File.Exists(filePath) ? filePath : null;
	}

	private static bool LoadConfigForProperty(IDeadworksPlugin plugin, PropertyInfo prop, bool isReload)
	{
		var configType = prop.PropertyType;
		var key = GetConfigKey(plugin);
		var dir = Path.Combine(_configsDir, key);
		var filePath = Path.Combine(dir, $"{key}.jsonc");

		object? config;

		if (!File.Exists(filePath))
		{
			config = Activator.CreateInstance(configType);
			try
			{
				Directory.CreateDirectory(dir);
				var json = JsonSerializer.Serialize(config, configType, JsonOptions);
				File.WriteAllText(filePath, $"// Configuration for {plugin.Name}\n{json}\n");
				_logger.LogInformation("Created default config for {PluginName}: {ConfigPath}", plugin.Name, filePath);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to write default config for {PluginName}", plugin.Name);
			}
		}
		else
		{
			try
			{
				var json = File.ReadAllText(filePath);
				config = JsonSerializer.Deserialize(json, configType, JsonOptions)
					?? Activator.CreateInstance(configType);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to parse config for {PluginName}", plugin.Name);
				if (isReload)
					return false;
				config = Activator.CreateInstance(configType);
			}
		}

		if (config is IConfig validatable)
		{
			try
			{
				validatable.Validate();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "{PluginName} config Validate() threw", plugin.Name);
				if (isReload)
					return false;
				config = Activator.CreateInstance(configType);
			}
		}

		prop.SetValue(plugin, config);
		return true;
	}

	private static PropertyInfo? FindConfigProperty(IDeadworksPlugin plugin)
	{
		return plugin.GetType()
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.FirstOrDefault(p => p.GetCustomAttribute<PluginConfigAttribute>() != null && p.CanWrite);
	}
}
