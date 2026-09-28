using System.Reflection;
using System.Text.Json;
using DeadworksManaged.Api;

namespace DeadworksManaged;

internal static class ConfigManager
{
	private static string _configsDir = "";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public static void Initialize()
	{
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

		LastError = null;
		if (!LoadConfigForProperty(plugin, prop, isReload: true))
			return false;

		try
		{
			plugin.OnConfigReloaded();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[ConfigManager] {plugin.Name}.OnConfigReloaded() threw: {ex.Message}");
		}

		return true;
	}

	/// <summary>Why the last config reload failed, or null; for replying to whoever asked for it.</summary>
	public static string? LastError { get; private set; }

	/// <summary>Whether the plugin has a config at all.</summary>
	public static bool HasConfig(IDeadworksPlugin plugin) => FindConfigProperty(plugin) != null;

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
				Console.WriteLine($"[ConfigManager] Created default config for {plugin.Name}: {filePath}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ConfigManager] Failed to write default config for {plugin.Name}: {ex.Message}");
			}
		}
		else
		{
			try
			{
				var json = File.ReadAllText(filePath);
				config = JsonSerializer.Deserialize(json, configType, JsonOptions)
					?? Activator.CreateInstance(configType);
				UnknownJsonKeys.Warn(json, configType, Path.GetFileName(filePath), "[ConfigManager] WARNING:", plugin.Name);
			}
			catch (Exception ex)
			{
				LastError = $"{Path.GetFileName(filePath)}: {ex.Message.TrimEnd('.')}";
				if (isReload)
				{
					Console.WriteLine($"[ConfigManager] Failed to reload {LastError}. {plugin.Name} keeps its previous settings.");
					return false;
				}
				// Loud, because the defaults can be less strict than what the owner wrote (require_reason, say).
				Console.WriteLine($"[ConfigManager] ERROR: failed to parse {LastError}. {plugin.Name} is using its default settings "
				                  + $"until it's fixed and dw_reloadconfig {plugin.Name} is run.");
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
				Console.WriteLine($"[ConfigManager] {plugin.Name} config Validate() threw: {ex.Message}");
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
